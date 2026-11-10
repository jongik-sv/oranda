using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Core.Transfer.Copy.CrossDbms;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 접속 간 복사 사전 점검(설계 7장). 모든 조회는 대량 실행 경로의 읽기(<see cref="IBulkExecutionService.StreamRowsAsync"/>)로 보내고
/// 하나씩 끝까지 읽은 뒤 다음 조회를 보낸다. 표 구조는 <see cref="IMetadataProvider"/> 로 읽는다(원본·대상 모두 점검 때만, 메모 D3).
/// 판정 규칙 가운데 DB 가 필요 없는 것은 <see cref="PrecheckRules"/> 에 있다.
/// DBMS 마다 다른 것은 생성자로 받는다(<see cref="CopyDbmsParts"/>): SELECT 글 = <see cref="CopyDbmsParts.Catalog"/>(<c>copy.*</c>),
/// 서버 오류 분류 = <see cref="CopyDbmsParts.Errors"/>(바깥 예외만, <see cref="CopyErrors"/>), 기능 차이·안내 글 = <see cref="CopyDbmsParts.PrecheckDialect"/>,
/// 미리 보기 문장 = <see cref="CopyDbmsParts.Dialect"/>, 값 해석 = <see cref="CopyDbmsParts.Codec"/>.
/// 원본 벌(<paramref name="source"/>)은 원본 세션 조회(접속 권한·원본 표 권한·건수·문자열 날짜·시퀀스 최대값)·읽기 일관성·원본 열 형식·원본 이름 판정에,
/// 대상 벌(<paramref name="target"/>)은 나머지 전부에 쓰고, 오류 분류는 둘을 합친다(<see cref="CopyErrors.Combine"/>, xdb-copy XC2).
/// DBMS 별 하위 형식(본문 없음)이 그 DBMS 의 벌을 두 번 넘기고 키 서비스로 등록된다(Oracle = <c>OracleCopyPrecheckService</c>).
/// </summary>
public partial class CopyPrecheckService(IBulkExecutionService bulk, IMetadataProvider metadata, IExecutionPolicy policy, CopyDbmsParts source,
                                         CopyDbmsParts target)
    : ICopyPrecheckService
{
    /// <summary>같은 DBMS 복사: 같은 벌(<paramref name="parts"/>)을 원본·대상에 같은 인스턴스로 넘긴다(DBMS 별 하위 형식이 쓴다).</summary>
    protected CopyPrecheckService(IBulkExecutionService bulk, IMetadataProvider metadata, IExecutionPolicy policy, CopyDbmsParts parts)
        : this(bulk, metadata, policy, parts, parts)
    {
    }

    public async Task<PrecheckReport> RunAsync(CopySessions sessions, CopyJob job, ResolvedPeriod period,
                                               IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(period);

        // 운영 대상이면 이 오류 하나만 낸다. 다른 조회도 하지 않는다(메모 D19).
        if (!CopyTargetGuard.IsAllowedTarget(sessions.Target.Profile))
        {
            return new PrecheckReport(
                [new CheckItem(null, "TargetProduction", CheckLevel.Error,
                    $"대상 접속 '{sessions.Target.Profile.Name}' 은 운영 접속이라 복사 대상으로 쓸 수 없습니다.")],
                [], [], new Dictionary<string, long>());
        }

        // 규칙 사이 모순(같은 원본 표 둘, 같은 대상 표에 쓰는 규칙 둘): 규칙 키·대상 표가 겹치면 나머지 점검의 뜻이 없다. 이 오류들만 낸다.
        if (CopyOwners.Problems(job) is { Count: > 0 } problems)
        {
            return new PrecheckReport([.. problems.Select(p => new CheckItem(null, "RuleInvalid", CheckLevel.Error, p))], [], [], new Dictionary<string, long>());
        }

        // 같은 DB 에서 어떤 규칙이 쓰는 대상 표가 어떤 규칙이 읽는 원본 표와 같으면 대상 정리가 원본 행을 지운다(표 단위·규칙 사이). 이 오류들만 낸다.
        if (CopySameDatabase.SameSourceTargets(sessions, job) is { Count: > 0 } same)
        {
            return new PrecheckReport(
                [.. same.Select(x => new CheckItem(x.Key, "SameSourceTarget", CheckLevel.Error,
                    $"{x.Key}: 대상 표 {x.Table} 이(가) 같은 DB 에서 원본으로 읽는 표입니다. 대상 정리가 원본 행을 지울 수 있어 실행할 수 없습니다. 대상 계정을 바꾸세요."))],
                [], [], new Dictionary<string, long>());
        }

        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        return await new Run(bulk, metadata, policy, source, target.Dbms, target.Catalog, CopyErrors.Combine(target.Errors, source.Errors), target.PrecheckDialect,
                             target.Dialect, target.Codec, sessions, job, period, progress)
            .ExecuteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 한 번의 점검이 쓰는 상태. 점검 항목을 순서대로 쌓는다. <paramref name="catalog"/>·<paramref name="dialect"/>·<paramref name="statements"/>·<paramref name="codec"/> 은
    /// 대상 벌, <paramref name="errors"/> 는 원본·대상을 합친 분류기, 원본 쪽 것은 <paramref name="sourceParts"/> 에서 꺼낸다.
    /// </summary>
    private sealed class Run(IBulkExecutionService bulk, IMetadataProvider metadata, IExecutionPolicy policy, CopyDbmsParts sourceParts, Dbms targetDbms, ISqlCatalog catalog,
                             IDbErrorClassifier errors, ICopyPrecheckDialect dialect, ICopyDialect statements, IBulkValueCodec codec,
                             CopySessions sessions, CopyJob job, ResolvedPeriod period, IProgress<string>? progress)
    {
        /// <summary>읽기 시간 어림에 쓰는 초당 행 수(보수적으로 작게 잡는다).</summary>
        private const double AssumedRowsPerSecond = 2000;

        private readonly List<CheckItem> _items = [];
        private readonly Dictionary<string, long> _estimated = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TableInfo> _sourceTables = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TableInfo> _targetTables = new(StringComparer.Ordinal);

        /// <summary>대상에 표가 없어 이번 실행이 만들 표의 규칙 키(「없으면 만들기」, <see cref="CopyTablePlan.CreateMissing"/>).</summary>
        private readonly HashSet<string> _creating = new(StringComparer.Ordinal);
        private SessionAccess _sourceAccess = null!;
        private SessionAccess _targetAccess = null!;
        private CopySqlBuilder _builder = null!;

        private ISession Source => sessions.Source;

        private ISession Target => sessions.Target;

        /// <summary>「테이블 다시 만들기」(DROP·CREATE)는 개발 라벨 대상에서만 만든다(Global Constraints).</summary>
        private bool CanRecreate => CopyTargetGuard.CanRecreate(Target.Profile);

        private static bool IsRecreate(TableRule rule) => rule.Action == TargetAction.Recreate;

        public async Task<PrecheckReport> ExecuteAsync(CancellationToken ct)
        {
            _sourceAccess = await CopyCatalogQueries.ReadAccessAsync(bulk, sourceParts.Catalog, sourceParts.Codec, Source, ct).ConfigureAwait(false);
            _targetAccess = await CopyCatalogQueries.ReadAccessAsync(bulk, catalog, codec, Target, ct).ConfigureAwait(false);

            var active = job.Tables.Where(r => r.Kind != RuleKind.Exclude).ToList();
            await LoadTablesAsync(active, ct).ConfigureAwait(false);
            _builder = new CopySqlBuilder(job, period, _sourceTables, _targetTables, _creating);

            if (PrecheckRules.EmptyPeriod(job, period) is { } empty)
            {
                _items.Add(empty);
            }

            // 대상 접속 계정이 아닌 계정의 표를 지우고 넣는다: 범위가 다른 계정으로 넓어지므로 확인을 받는다(묶음 B 7장).
            if (PrecheckRules.OtherOwnerTargets(job, _targetAccess.User) is { } other)
            {
                _items.Add(other);
            }

            var plans = new List<CopyTablePlan>();
            foreach (var rule in active.Where(r => _sourceTables.ContainsKey(Key(r)) && _targetTables.ContainsKey(Key(r))))
            {
                plans.Add(await PrepareTableAsync(rule, ct).ConfigureAwait(false));
            }

            await CheckParentsAsync(active, ct).ConfigureAwait(false);
            _items.AddRange(PrecheckRules.CheckParentLinkMasks(job));

            if (PrecheckRules.CrossCharsetNote(sourceParts.Dbms, targetDbms, plans) is { } charset)
            {
                _items.Add(charset);
            }

            var order = BuildOrder(plans);
            await CheckSequencesAsync(plans, ct).ConfigureAwait(false);
            await CheckReadConsistencyAsync(ct).ConfigureAwait(false);
            var preview = await BuildPreviewAsync(order, ct).ConfigureAwait(false);
            return new PrecheckReport(_items, order, preview, _estimated);
        }

        // ---- 표 구조 ----

        private async Task LoadTablesAsync(List<TableRule> active, CancellationToken ct)
        {
            foreach (var rule in active)
            {
                var key = Key(rule);
                var sourceOwner = SourceOwner(rule);
                var targetOwner = TargetOwner(rule);
                progress?.Report($"{key}: 구조 읽는 중");
                if (IsRecreate(rule) && !CanRecreate)
                {
                    _items.Add(new CheckItem(key, "RecreateNotAllowed", CheckLevel.Error,
                        $"{key}: 대상 접속 '{Target.Profile.Name}' 이 개발 접속이 아니라 「테이블 다시 만들기」를 쓸 수 없습니다. 대상 처리를 바꾸세요."));
                }

                await CheckWrittenAsAsync(rule, key, ct).ConfigureAwait(false);

                var source = await TryGetTableAsync(Source, sourceOwner, rule.Table, ct).ConfigureAwait(false);
                if (source is null)
                {
                    var hint = rule.WrittenAs is null && await IsSynonymAsync(sourceOwner, rule.Table, ct).ConfigureAwait(false)
                        ? " 시노님 이름이면 원본 접속을 연 뒤 표를 다시 추가하세요(실제 계정·표로 풀어 저장합니다)."
                        : string.Empty;
                    _items.Add(new CheckItem(key, "SourceNoSelect", CheckLevel.Error,
                        $"{key}: 원본에 표 {sourceOwner}.{rule.Table} 이(가) 없거나 SELECT 권한이 없습니다(권한이 없으면 표가 없는 것처럼 보입니다).{hint}"));
                }

                // 대상은 표여야 한다(뷰·시노님·MV 에 지우고 넣지 않는다). 표가 아니거나 확인하지 못하면(조회 오류) 구조를 읽지 않고 막는다.
                var isTable = await TryCountAsync(TargetSql("copy.target_is_table"), ct, ("OWN", targetOwner), ("TBL", rule.Table)).ConfigureAwait(false);
                var target = isTable is > 0 ? await TryGetTableAsync(Target, targetOwner, rule.Table, ct).ConfigureAwait(false) : null;

                // 「없으면 만들기」: 대상 계정에 그 이름의 객체가 아무것도 없을 때만 원본 구조로 만든다(대상 표가 있으면 선택을 무시한다).
                string? createRefused = null;
                if (target is null && source is not null && rule.CreateIfMissing && !IsRecreate(rule) && isTable == 0)
                {
                    createRefused = await CreateRefusedAsync(targetOwner, rule.Table, ct).ConfigureAwait(false);
                    if (createRefused is null)
                    {
                        source = await DropMissingSequenceDefaultsAsync(rule, key, source, ct).ConfigureAwait(false);
                        _sourceTables[key] = source;
                        _targetTables[key] = PrecheckRules.AsRecreated(source, targetOwner);
                        _creating.Add(key);
                        continue;
                    }
                }

                if (target is null)
                {
                    _items.Add(new CheckItem(key, "TargetMissing", CheckLevel.Error, isTable is null
                        ? $"{key}: 대상 {targetOwner}.{rule.Table} 이(가) 표인지 확인하지 못했습니다(ALL_TABLES·ALL_MVIEWS 조회 실패). 확인할 수 있을 때까지 실행하지 않습니다."
                        : $"{key}: 대상에 표 {targetOwner}.{rule.Table} 이(가) 없거나 표가 아니거나(뷰·시노님·MV) 접근 권한이 없습니다(권한이 없으면 표가 없는 것처럼 보입니다)."
                          + (createRefused is null ? string.Empty : $" 「없으면 만들기」를 쓸 수 없습니다: {createRefused}")));
                }

                if (source is null || target is null)
                {
                    continue;
                }

                if (IsRecreate(rule) && CanRecreate)
                {
                    source = await DropMissingSequenceDefaultsAsync(rule, key, source, ct).ConfigureAwait(false);
                }

                _sourceTables[key] = source;
                // 다시 만들 표는 적재할 때 원본 구조다: 열·키·문장을 옛 대상 구조가 아니라 다시 만든 구조로 정한다.
                _targetTables[key] = IsRecreate(rule) && CanRecreate
                    ? PrecheckRules.AsRecreated(source, targetOwner)
                    : await AdjustIdentityAsync(target, targetOwner, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 교차 복사로 표를 만들 때(없으면 만들기·다시 만들기): 열 기본값이 <c>시퀀스.NEXTVAL</c> 인데 그 시퀀스가 대상에 없으면 그 열의 DEFAULT 만 빼고 만든다
        /// (<see cref="CopyCheckCodes.SequenceDefaultDropped"/> 확인 항목). 뺀 구조를 점검 계획(<see cref="CopyTablePlan.Source"/>)에 담으므로 미리 보기 DDL 과 실행 DDL 이 같다.
        /// 소유자 접두가 없으면 대상 소유자, 원본 소유자면 대상 소유자로 바뀌는 쪽(<see cref="CreateTableDdl"/> 의 소유자 치환), 다른 소유자는 그 소유자에서 찾는다.
        /// 시퀀스 목록을 읽지 못하면 있는 것으로 보고 기본값을 둔다(같은 DBMS 복사는 바꾸지 않는다).
        /// </summary>
        private async Task<TableInfo> DropMissingSequenceDefaultsAsync(TableRule rule, string key, TableInfo source, CancellationToken ct)
        {
            if (sourceParts.Dbms == targetDbms)
            {
                return source;
            }

            var targetOwner = TargetOwner(rule);
            var sourceOwner = SourceOwner(rule);
            var known = new Dictionary<string, HashSet<string>?>(CopyOwners.OwnerComparer);
            var drop = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in source.Columns.Where(c => !c.IsVirtual && !c.IsIdentity))
            {
                foreach (var (owner, name) in PrecheckRules.SequenceReferences(c.DefaultValue))
                {
                    var resolved = owner is null || CopyOwners.SameOwner(owner, sourceOwner) ? targetOwner : owner;
                    if (!known.TryGetValue(resolved, out var names))
                    {
                        names = await ReadSequenceNamesAsync(resolved, ct).ConfigureAwait(false);
                        known[resolved] = names;
                    }

                    if (names is not null && !names.Contains(name))
                    {
                        drop.Add(c.Name);
                        _items.Add(new CheckItem(key, CopyCheckCodes.SequenceDefaultDropped, CheckLevel.Confirm,
                            $"{key}: 대상 {resolved} 에 시퀀스 {name} 이(가) 없어 열 {c.Name} 의 기본값({c.DefaultValue!.Trim()})을 빼고 만듭니다. 시퀀스를 만든 뒤 기본값을 다시 걸어야 합니다.")
                        {
                            Column = c.Name,
                        });
                        break;
                    }
                }
            }

            return PrecheckRules.WithoutDefaults(source, drop);
        }

        /// <summary>한 소유자의 시퀀스 이름(<c>copy.sequence_names</c>, ALL_SEQUENCES). 읽지 못하면 null.</summary>
        private async Task<HashSet<string>?> ReadSequenceNamesAsync(string owner, CancellationToken ct)
        {
            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.sequence_names"), ct, ("OWN", owner)).ConfigureAwait(false);
                return rows.Select(r => codec.ToText(r[0])).OfType<string>().ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (CopyErrors.IsNoAccess(errors, ex))
            {
                return null;
            }
        }

        /// <summary>
        /// 대상에 표가 없을 때 「없으면 만들기」를 쓸 수 없는 이유(쓸 수 있으면 null). 개발 접속에서만 만들 수 있고,
        /// 같은 이름의 다른 객체(뷰·시노님·MV·시퀀스 …)가 있거나 있는지 확인하지 못하면 만들 수 없다(CREATE 가 이름 충돌로 실패한다).
        /// </summary>
        private async Task<string?> CreateRefusedAsync(string targetOwner, string table, CancellationToken ct)
        {
            if (!CanRecreate)
            {
                return $"대상 접속 '{Target.Profile.Name}' 이 개발 접속이 아니라 표를 만들 수 없습니다. 개발 접속에서만 만들 수 있습니다.";
            }

            var objects = await TryCountAsync(TargetSql("copy.object_exists"), ct, ("OWN", targetOwner), ("NAME", table)).ConfigureAwait(false);
            return objects switch
            {
                null => "같은 이름의 객체가 있는지 확인하지 못했습니다(ALL_OBJECTS 조회 실패).",
                > 0 => $"{targetOwner} 에 같은 이름의 표가 아닌 객체가 있습니다.",
                _ => null,
            };
        }

        /// <summary>
        /// 표를 더할 때 적은 시노님(<see cref="TableRule.WrittenAs"/>)을 지금 원본에서 다시 풀어 본다. 다른 표를 가리키거나 풀리지 않으면 확인 항목만 낸다
        /// (작업은 저장된 계정·표를 그대로 복사한다. 자동으로 바꾸지 않는다, 결정 B-4 a).
        /// </summary>
        private async Task CheckWrittenAsAsync(TableRule rule, string key, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(rule.WrittenAs))
            {
                return;
            }

            var saved = $"{SourceOwner(rule)}.{rule.Table}";
            var now = await CopyTableInput.ResolveAsync((o, n) => metadata.GetNameStepAsync(Source, o, n, ct), rule.WrittenAs, job.SourceOwner, _sourceAccess.User)
                .ConfigureAwait(false);
            if (now.Table is { } t && CopyOwners.SameOwner(t.Owner, SourceOwner(rule)) && string.Equals(t.Table, rule.Table, StringComparison.Ordinal))
            {
                return;
            }

            _items.Add(new CheckItem(key, CopyCheckCodes.SynonymChanged, CheckLevel.Confirm, now.Table is { } moved
                ? $"{key}: 더할 때 적은 시노님 {rule.WrittenAs} 이(가) 지금은 {moved.Owner}.{moved.Table} 을(를) 가리킵니다. 작업은 저장된 {saved} 를 그대로 복사합니다. 바꾸려면 표를 다시 추가하세요."
                : $"{key}: 더할 때 적은 시노님 {rule.WrittenAs} 을(를) 지금 풀 수 없습니다({now.Problem}). 작업은 저장된 {saved} 를 그대로 복사합니다."));
        }

        private async Task<bool> IsSynonymAsync(string owner, string name, CancellationToken ct)
        {
            var step = await metadata.GetNameStepAsync(Source, owner, name, ct).ConfigureAwait(false);
            if (step is null)
            {
                step = await metadata.GetNameStepAsync(Source, "PUBLIC", name, ct).ConfigureAwait(false);
            }

            return step is { IsSynonym: true };
        }

        // 표가 안 보이면 InvalidOperationException 이다(권한이 없어도 같다).
        private async Task<TableInfo?> TryGetTableAsync(ISession s, string owner, string table, CancellationToken ct)
        {
            try
            {
                // 교차 복사: Oracle 카탈로그가 가상 열로 표시하는 XMLTYPE 열을 형식 판정이 보게 한다(점검 계획·실행기가 쓰는 구조 모두 여기서 온다).
                // 같은 DBMS 는 예전처럼 가상 열로 두어 빼고 복사한다(SameDbmsXmlTypeNotes 가 확인 항목으로 알림).
                var info = await metadata.GetTableAsync(s, new DbObjectRef(owner, table, "TABLE"), ct).ConfigureAwait(false);
                return sourceParts.Dbms != targetDbms ? PrecheckRules.ClearXmlTypeVirtual(info) : info;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        // ColumnInfo.IsIdentity 는 ALWAYS 와 BY DEFAULT 를 가리지 않는다. 값을 넣을 수 있는 BY DEFAULT 열은 거짓으로 바꾼다(메모 D12).
        private async Task<TableInfo> AdjustIdentityAsync(TableInfo target, string targetOwner, CancellationToken ct)
        {
            if (!target.Columns.Any(c => c.IsIdentity))
            {
                return target;
            }

            HashSet<string> always;
            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.identity_cols"), ct,
                    ("OWN", targetOwner), ("TBL", target.Table.Name)).ConfigureAwait(false);
                always = rows.Where(r => string.Equals(codec.ToText(r[1]), "ALWAYS", StringComparison.OrdinalIgnoreCase))
                    .Select(r => codec.ToText(r[0])).OfType<string>().ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (CopyErrors.IsNoAccess(errors, ex))
            {
                return target; // 구분할 수 없으면 모두 ALWAYS 로 본다(오류 쪽으로 기운다).
            }

            return target with { Columns = [.. target.Columns.Select(c => c.IsIdentity && !always.Contains(c.Name) ? c with { IsIdentity = false } : c)] };
        }

        // ---- 표 하나 ----

        private async Task<CopyTablePlan> PrepareTableAsync(TableRule rule, CancellationToken ct)
        {
            var key = Key(rule);
            progress?.Report($"{key}: 점검 중");
            var source = _sourceTables[key];
            var target = _targetTables[key];

            // 교차 복사(DBMS 가 다름): 옮길 형식이 없는 열은 오류가 아니라 복사 열에서 빠지고(ColumnDropped), 실행기가 SELECT·DML 에 넣는 열과 같은 목록이 된다.
            // 대상 구조가 원본에서 만들어지는 표(없으면 만들기·다시 만들기)는 대상 열 형식을 옮긴 형식으로 본다.
            var fromSource = _creating.Contains(key) || (IsRecreate(rule) && CanRecreate);
            _items.AddRange(PrecheckRules.CompareColumns(sourceParts.Codec, codec, source, target, rule, key, sourceParts.Dbms, targetDbms, fromSource));
            _items.AddRange(PrecheckRules.SameDbmsXmlTypeNotes(source, rule, key, sourceParts.Dbms, targetDbms));
            var dropped = PrecheckRules.CrossDroppedColumns(sourceParts.Codec, source, target, rule, sourceParts.Dbms, targetDbms);
            var columns = PrecheckRules.SelectColumns(sourceParts.Codec, codec, source, target, rule, sourceParts.Dbms, targetDbms, fromSource);
            if (PrecheckRules.CrossBlobNote(sourceParts.Dbms, targetDbms, source, columns, key) is { } blob)
            {
                _items.Add(blob);
            }

            if (PrecheckRules.CrossStringByteLimitNote(sourceParts.Dbms, targetDbms, source, target, columns, key) is { } bytes)
            {
                _items.Add(bytes);
            }

            if (PrecheckRules.CrossCharPadNote(sourceParts.Dbms, targetDbms, source, target, columns, key) is { } pad)
            {
                _items.Add(pad);
            }

            var keys = PrecheckRules.KeyColumns(target, columns);
            _items.AddRange(PrecheckRules.CheckMasks(rule, source, keys, key, dropped));

            var creating = _creating.Contains(key);

            // 교차: PK 열이 옮길 형식이 없어 빠졌으면 KeyColumnDropped 오류가 이미 있다. 「PK 없음」은 그 이유를 가리므로 내지 않는다.
            var keyDropped = dropped.Any(c => IsPrimaryKeyColumn(source, c) || IsPrimaryKeyColumn(target, c));
            if (keys.Count == 0 && !keyDropped)
            {
                // 새로 만드는 표는 비어 있어 INSERT 로 넣는다: PK 기준 MERGE 에 쓸 키가 없어도 막지 않는다.
                _items.Add(rule.Action == TargetAction.MergeByPk && !creating
                    ? new CheckItem(key, "NoPrimaryKey", CheckLevel.Error,
                        $"{key}: PK 기준 MERGE 인데 대상에 쓸 수 있는 PK 가 없습니다. 대상 처리를 바꾸세요.")
                    : new CheckItem(key, "NoPrimaryKey", CheckLevel.Confirm,
                        $"{key}: PK 없음. 다시 실행하면 행이 겹칠 수 있습니다."));
            }

            var readable = await CheckSourcePrivilegeAsync(rule, ct).ConfigureAwait(false);
            var canDisableTriggers = false;
            IReadOnlyList<string> triggers = [];
            if (creating)
            {
                // 대상에 표가 없어 만든다: 옛 대상의 제약·권한·트리거·자식 FK 가 없다. 새 표라 지우지도 않는다.
                await CheckCreateAsync(rule, source, ct).ConfigureAwait(false);
            }
            else if (IsRecreate(rule))
            {
                // 지우고 다시 만들므로 옛 대상의 제약·권한 비교는 뜻이 없다. 트리거는 적재 뒤에 되살리므로 끌 것도 없다.
                await CheckRecreateAsync(rule, source, ct).ConfigureAwait(false);
            }
            else
            {
                await CheckTargetPrivilegesAsync(rule, keys, ct).ConfigureAwait(false);
                AddTargetOnlyConstraints(rule, source, target);
                (canDisableTriggers, triggers) = await CheckTriggersAsync(rule, ct).ConfigureAwait(false);
            }

            IReadOnlyList<DisabledObject> disablable = creating ? [] : await CheckChildForeignKeysAsync(rule, keys, ct).ConfigureAwait(false);

            if (readable)
            {
                await CountAsync(rule, ct).ConfigureAwait(false);
                await CheckStringDatesAsync(rule, ct).ConfigureAwait(false);
            }

            return new CopyTablePlan(rule, source, target, columns, keys)
            {
                CreateMissing = creating,
                CanDisableTriggers = canDisableTriggers,
                Triggers = triggers,
                DisablableForeignKeys = disablable,
            };
        }

        private static bool IsPrimaryKeyColumn(TableInfo table, string column)
            => table.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.PrimaryKey)?.Columns.Contains(column, StringComparer.Ordinal) == true;

        private async Task<bool> CheckSourcePrivilegeAsync(TableRule rule, CancellationToken ct)
        {
            var held = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, sourceParts.Catalog, sourceParts.Codec, Source, _sourceAccess, SourceOwner(rule), rule.Table, "TABLE",
                    sourceParts.PrecheckDialect.SupportsReadPrivilege, ct)
                .ConfigureAwait(false);
            if (held.Contains("SELECT"))
            {
                return true;
            }

            _items.Add(new CheckItem(Key(rule), "SourceNoSelect", CheckLevel.Error, $"{Key(rule)}: 원본 표 {SourceOwner(rule)}.{rule.Table} 에 SELECT 권한이 없습니다."));
            return false;
        }

        private async Task CheckTargetPrivilegesAsync(TableRule rule, IReadOnlyList<string> keys, CancellationToken ct)
        {
            var held = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, catalog, codec, Target, _targetAccess, TargetOwner(rule), rule.Table, "TABLE", dialect.SupportsReadPrivilege, ct)
                .ConfigureAwait(false);
            var missing = PrecheckRules.RequiredTargetPrivileges(rule, keys).Where(p => !held.Contains(p)).ToList();
            if (missing.Count > 0)
            {
                _items.Add(new CheckItem(Key(rule), "TargetNoWrite", CheckLevel.Error,
                    $"{Key(rule)}: 대상 표 {TargetOwner(rule)}.{rule.Table} 에 필요한 권한이 없습니다: {string.Join(", ", missing)}."));
            }
        }

        /// <summary>
        /// 「테이블 다시 만들기」 점검(조정 결정 9·10). 대상 세션이 대상 소유자이고 CREATE TABLE(트리거가 있으면 CREATE TRIGGER)이 있어야 한다:
        /// 비소유자는 트리거 DDL(GET_DDL)·권한 되살리기에 더 큰 권한이 들어 DROP 뒤에 되살리지 못할 수 있다.
        /// 통과하면 되돌릴 수 없음·휴지통·만들지 않는 FK 를 Confirm 항목 하나로 알린다.
        /// </summary>
        private async Task CheckRecreateAsync(TableRule rule, TableInfo source, CancellationToken ct)
        {
            if (!CanRecreate)
            {
                return; // RecreateNotAllowed 를 이미 냈다.
            }

            var key = Key(rule);
            var owner = TargetOwner(rule);
            var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.trigger_count_all"), ct,
                ("OWN", owner), ("TBL", rule.Table)).ConfigureAwait(false);
            var triggerCount = codec.ToInt64(rows.FirstOrDefault()?[0]) ?? 0;

            // 표마다 본다: 계정이 섞인 작업에서 접속 계정이 아닌 계정의 표는 다시 만들 수 없다.
            if (!_targetAccess.IsOwner(owner))
            {
                _items.Add(new CheckItem(key, "RecreateNoPrivilege", CheckLevel.Error,
                    $"{key}: 「테이블 다시 만들기」는 대상 소유자({owner}) 계정으로 접속해야 합니다(지금 {_targetAccess.User})."));
                return;
            }

            var missing = new List<string>();
            if (!_targetAccess.Has("CREATE TABLE") && !_targetAccess.Has("CREATE ANY TABLE"))
            {
                missing.Add("CREATE TABLE");
            }

            if (triggerCount > 0 && !_targetAccess.Has("CREATE TRIGGER") && !_targetAccess.Has("CREATE ANY TRIGGER"))
            {
                missing.Add("CREATE TRIGGER");
            }

            if (missing.Count > 0)
            {
                _items.Add(new CheckItem(key, "RecreateNoPrivilege", CheckLevel.Error,
                    $"{key}: 표를 지우고 다시 만들 권한이 없습니다: {string.Join(", ", missing)}."));
                return;
            }

            // 대상 계정이 같은 다시 만들기 표(먼저 지워지므로 이름 겹침에서 뺀다), 그중 원본 계정도 같은 표(작업 안 FK 를 원본대로 붙인다).
            var recreatedHere = RecreatedTables(owner, sourceOwner: null);
            var linked = BuiltTables(owner, SourceOwner(rule));
            var blocked = await RecreateBlockersAsync(rule, source, recreatedHere, linked, creating: false, ct).ConfigureAwait(false);
            if (blocked.Count > 0)
            {
                // DROP 뒤에야 드러나는 CREATE 실패(표가 휴지통에만 남음)를 지우기 전에 막는다(조정 결정 16).
                _items.Add(new CheckItem(key, "RecreateBlocked", CheckLevel.Error,
                    $"{key}: 지운 뒤 원본 구조로 다시 만들 수 없어 「테이블 다시 만들기」를 쓸 수 없습니다: {string.Join(" / ", blocked)}."));
                return;
            }

            var tablespace = codec.ToText((await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.tablespace"), ct,
                ("OWN", owner), ("TBL", rule.Table)).ConfigureAwait(false)).FirstOrDefault()?[0]);
            var skipped = source.Constraints
                .Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is { } p && !IsLinkedParent(p, rule, linked))
                .Select(c => $"{c.Name}(→{c.ReferencedTable!.Name})")
                .ToList();
            var message = $"{key}: 대상 표 {owner}.{rule.Table} 을(를) 지우고 원본 구조로 다시 만든 뒤 넣습니다."
                          + dialect.RecycleBinNote(tablespace)
                          + " DROP·CREATE 는 DDL 이라 커밋 방식과 상관없이 되돌릴 수 없습니다."
                          + $" 대상의 트리거 {Number(triggerCount)}개·다른 계정에 준 권한·이 표를 참조하던 FK 는 적재 뒤 되살립니다"
                          + "(참조 FK 는 지우기 전에 떼어 내고, 대상 계정에 보이지 않는 FK 가 있으면 지우지 않고 멈춥니다).";
            if (skipped.Count > 0)
            {
                message += $" 함께 다시 만들지 않는 표를 참조하는 원본 FK {string.Join(", ", skipped)} 는 만들지 않습니다.";
            }

            message += CrossSkippedForeignKeyNote(rule, source, linked);
            message += dialect.RecreateDifferenceNote(source.Columns.Any(c => c.Domain is not null)) + CrossCreateNote(source);
            var lost = await NotRestoredAsync(rule, ct).ConfigureAwait(false);
            if (lost.Count > 0)
            {
                message += $" 되살리지 않는 것: {string.Join(", ", lost)}.";
            }

            _items.Add(new CheckItem(key, "RecreateTable", CheckLevel.Confirm, message));
        }

        /// <summary>대상 계정이 <paramref name="targetOwner"/> 인 다시 만들기 표 이름(<paramref name="sourceOwner"/> 를 주면 원본 계정도 같은 것만).</summary>
        private HashSet<string> RecreatedTables(string targetOwner, string? sourceOwner)
            => job.Tables.Where(r => r.Kind != RuleKind.Exclude && IsRecreate(r)
                                     && CopyOwners.SameOwner(TargetOwner(r), targetOwner)
                                     && (sourceOwner is null || CopyOwners.SameOwner(SourceOwner(r), sourceOwner)))
                .Select(r => r.Table).ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// 이번 실행이 만드는 표(다시 만들기 + 없으면 만들기) 가운데 대상 계정이 <paramref name="targetOwner"/>, 원본 계정이 <paramref name="sourceOwner"/> 인 표 이름.
        /// 작업 안 FK 를 원본대로 붙이는 부모 후보다(실행기 <c>CopyRunner</c> 의 <c>planned</c> 와 같은 판정).
        /// </summary>
        private HashSet<string> BuiltTables(string targetOwner, string sourceOwner)
            => job.Tables.Where(r => r.Kind != RuleKind.Exclude && (IsRecreate(r) || _creating.Contains(Key(r)))
                                     && CopyOwners.SameOwner(TargetOwner(r), targetOwner)
                                     && CopyOwners.SameOwner(SourceOwner(r), sourceOwner))
                .Select(r => r.Table).ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// 「없으면 만들기」 점검(D4): 대상 소유자 계정으로 접속해 CREATE TABLE 이 있어야 하고, 원본 구조를 만들 수 없는 이유(<see cref="RecreateBlockersAsync"/> 와 같은 조건)가 없어야 한다.
        /// 통과하면 만드는 것·안 만드는 것·적재 실패 때의 정리를 Confirm 항목 하나로 알린다.
        /// </summary>
        private async Task CheckCreateAsync(TableRule rule, TableInfo source, CancellationToken ct)
        {
            var key = Key(rule);
            var owner = TargetOwner(rule);
            if (!_targetAccess.IsOwner(owner))
            {
                _items.Add(new CheckItem(key, CopyCheckCodes.CreateNoPrivilege, CheckLevel.Error,
                    $"{key}: 「없으면 만들기」는 대상 소유자({owner}) 계정으로 접속해야 합니다(지금 {_targetAccess.User})."));
                return;
            }

            if (!_targetAccess.Has("CREATE TABLE") && !_targetAccess.Has("CREATE ANY TABLE"))
            {
                _items.Add(new CheckItem(key, CopyCheckCodes.CreateNoPrivilege, CheckLevel.Error,
                    $"{key}: 표를 만들 권한이 없습니다: CREATE TABLE."));
                return;
            }

            var linked = BuiltTables(owner, SourceOwner(rule));
            var blocked = await RecreateBlockersAsync(rule, source, RecreatedTables(owner, sourceOwner: null), linked, creating: true, ct).ConfigureAwait(false);
            if (blocked.Count > 0)
            {
                _items.Add(new CheckItem(key, CopyCheckCodes.CreateBlocked, CheckLevel.Error,
                    $"{key}: 원본 구조로 만들 수 없어 「없으면 만들기」를 쓸 수 없습니다: {string.Join(" / ", blocked)}."));
                return;
            }

            var skipped = source.Constraints
                .Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is { } p && !IsLinkedParent(p, rule, linked))
                .Select(c => $"{c.Name}(→{c.ReferencedTable!.Name})")
                .ToList();
            var message = $"{key}: 대상에 표 {owner}.{rule.Table} 이(가) 없어 원본 구조로 만든 뒤 넣습니다(만드는 문장은 미리 보기에 있습니다). 새 표라 대상을 지우지 않습니다."
                          + " 넣기가 실패하면 만든 표를 휴지통 없이 지웁니다(DROP … PURGE)."
                          + " 만들지 않는 것: 트리거·다른 계정에 준 권한·시노님(원본 것을 가져오지 않습니다).";
            if (skipped.Count > 0)
            {
                message += $" 함께 만들지 않는 표를 참조하는 원본 FK {string.Join(", ", skipped)} 는 만들지 않습니다.";
            }

            message += CrossSkippedForeignKeyNote(rule, source, linked);
            message += dialect.RecreateDifferenceNote(source.Columns.Any(c => c.Domain is not null)) + CrossCreateNote(source);
            _items.Add(new CheckItem(key, CopyCheckCodes.CreateTable, CheckLevel.Confirm, message));
        }

        /// <summary>교차 복사로 만드는 표가 원본과 달라지는 것(<see cref="CrossTableMap"/>)을 확인 글에 덧붙일 문장(앞 공백 포함). 같은 DBMS 거나 달라지는 것이 없으면 빈 글.</summary>
        private string CrossCreateNote(TableInfo source)
        {
            var notes = CrossTableMap.MapTable(sourceParts.Dbms, targetDbms, source, sourceParts.Codec).Notes;
            return notes.Count == 0 ? string.Empty : " " + targetDbms + " 로 옮기며 달라지는 것: " + string.Join(" / ", notes) + ".";
        }

        /// <summary>
        /// 교차 복사에서 함께 만드는 부모를 참조하지만 대상에 만들 수 없는 작업 안 FK(자식·부모 열을 옮기지 않거나, 부모의 PK·UK 를 만들지 않음)와 이유.
        /// 실행기(<c>CopyRunner</c>)가 같은 판정(<see cref="CrossTableMap.FilterForeignKeys"/>)으로 붙이지 않는 FK 다. 같은 DBMS 면 비어 있다.
        /// </summary>
        private IReadOnlyList<(ConstraintInfo Fk, string Reason)> CrossSkippedForeignKeys(TableRule rule, TableInfo source, HashSet<string> linked)
            => CrossTableMap.SkippedForeignKeys(sourceParts.Dbms, targetDbms, source, sourceParts.Codec,
                source.Constraints.Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is { } p && IsLinkedParent(p, rule, linked)),
                parent => LinkedParentSource(parent, rule));

        private string CrossSkippedForeignKeyNote(TableRule rule, TableInfo source, HashSet<string> linked)
        {
            var skipped = CrossSkippedForeignKeys(rule, source, linked)
                .Select(x => CrossTableMap.FormatSkipped(x.Fk.Name, x.Fk.ReferencedTable!.Name, x.Reason)).ToList();
            return skipped.Count == 0 ? string.Empty : $" 대상으로 옮기지 않는 열·키를 쓰는 작업 안 FK {string.Join(", ", skipped)} 는 만들지 않습니다.";
        }

        /// <summary>함께 만드는 부모 표(<see cref="BuiltTables"/> 와 같은 판정: 원본·대상 계정이 같고 이번 실행이 만든다)의 원본 구조. 없으면 null.</summary>
        private TableInfo? LinkedParentSource(DbObjectRef parent, TableRule rule)
            => job.Tables.Where(r => r.Kind != RuleKind.Exclude && (IsRecreate(r) || _creating.Contains(Key(r)))
                                     && string.Equals(r.Table, parent.Name, StringComparison.Ordinal)
                                     && CopyOwners.SameOwner(SourceOwner(r), SourceOwner(rule))
                                     && CopyOwners.SameOwner(TargetOwner(r), TargetOwner(rule)))
                .Select(r => _sourceTables.GetValueOrDefault(Key(r)))
                .FirstOrDefault(t => t is not null);

        /// <summary>원본 FK 의 부모가 같은 원본 계정·같은 대상 계정으로 함께 다시 만드는 표인가(실행기 <c>RecreateStep.SourceForeignKeysAsync</c> 와 같은 판정).</summary>
        private bool IsLinkedParent(DbObjectRef parent, TableRule rule, HashSet<string> linked)
            => CopyOwners.SameOwner(parent.Owner, SourceOwner(rule)) && linked.Contains(parent.Name);

        /// <summary>
        /// 지우기 전에 알 수 있는 CREATE 실패 원인. 지원 밖 형식 열(건너뛴 열도 다시 만들 때 만들어진다: 객체형·VECTOR·JSON·XMLTYPE·LONG),
        /// STANDARD 한도를 넘는 길이(대상 MAX_STRING_SIZE 가 EXTENDED 로 확인되지 않으면), 식이 가리키는 객체가 대상 소유자에 없음,
        /// 대상 소유자의 다른 표와 겹치는 제약·인덱스 이름(함께 다시 만드는 표의 이름은 먼저 지워지므로 뺀다).
        /// </summary>
        private async Task<List<string>> RecreateBlockersAsync(TableRule rule, TableInfo source, HashSet<string> recreated, HashSet<string> linked,
                                                               bool creating, CancellationToken ct)
        {
            var owner = TargetOwner(rule);
            var blocked = new List<string>();
            // 새 표 만들기와 지우고 다시 만들기는 DBMS 가 따로 막을 수 있다.
            if ((creating ? dialect.CreateBlockedReason(Target) : dialect.RecreateBlockedReason(Target)) is { } reason)
            {
                blocked.Add(reason);
            }

            // 교차 복사(DBMS 가 다름)는 대상 DBMS 에 만들 구조(CrossTableMap)로 본다: 옮길 형식이 없는 열은 만들지 않으므로 막지 않고(ColumnDropped 로 알린다),
            // 옮긴 형식은 대상 코덱이 쓸 수 있어야 한다. 같은 DBMS 는 원본 열 형식을 원본 코덱으로 본다.
            var cross = sourceParts.Dbms != targetDbms;
            var mapped = CrossTableMap.MapTable(sourceParts.Dbms, targetDbms, source, sourceParts.Codec);
            var effective = cross ? mapped.Target : source;
            var unsupported = (cross
                    ? source.Columns.Where(c => !c.IsVirtual
                                                && CrossTypeMap.Map(sourceParts.Dbms, targetDbms, c) is { } map
                                                && !CrossTableMap.IsDropped(map, c, sourceParts.Codec)
                                                && !codec.IsCrossWritable(map.BindType!))
                    : source.Columns.Where(c => !c.IsVirtual && !sourceParts.Codec.IsSupported(c.DataType)))
                .Select(c => $"{c.Name}({c.DataType})").ToList();
            if (unsupported.Count > 0)
            {
                blocked.Add($"지원하지 않는 형식 열 {string.Join(", ", unsupported)}(건너뛴 열도 다시 만들 때 만들어집니다)");
            }

            // 교차: 원본은 LOB 이 아닌데 대상에서 LOB 이 되는 열(Tibero VARCHAR2 4000 초과·긴 NVARCHAR2·NCHAR·LONG·LONG RAW → CLOB·NCLOB·BLOB)에는 PK·UK 를 만들 수 없다.
            // 지우고 나서야 CREATE 가 실패하므로(Oracle ORA-02329) 지우기 전에 막는다. CHECK·FK·인덱스는 만들지 않고 넘어간다(CrossTableMap).
            foreach (var key in source.Constraints.Where(k => k.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique))
            {
                var lob = key.Columns.Where(mapped.LobMappedColumns.ContainsKey).ToList();
                if (lob.Count > 0)
                {
                    var described = lob.Select(name => $"{name}({source.Columns.FirstOrDefault(c => c.Name == name)?.DataType} → {mapped.LobMappedColumns[name]})");
                    blocked.Add($"{source.Table.Name} 의 {(key.Kind == ConstraintKind.PrimaryKey ? "PK" : "UK")} {key.Name} 의 열 {string.Join(", ", described)} 은(는) {targetDbms} 에서 LOB 형식이 되어 "
                                + "LOB 열에는 PK·UK 를 만들 수 없습니다");
                }
            }

            // 기본 문자열 한도를 넘는 길이(Oracle: MAX_STRING_SIZE 가 EXTENDED 로 확인되지 않으면 막는다). 교차는 CLOB 으로 옮긴 뒤의 구조로 본다.
            blocked.AddRange(await dialect.ExtendedStringBlockersAsync(effective, TargetParameterAsync, ct).ConfigureAwait(false));

            // 기본값·가상 열 식이 원본 스키마 객체(시퀀스·함수)를 가리키면 대상 소유자의 같은 이름 객체로 바꿔 만든다.
            var referenced = effective.Columns.Where(c => !c.IsIdentity)
                .SelectMany(c => CreateTableDdl.ReferencedObjects(c.DefaultValue, SourceOwner(rule)).Select(o => (Column: c.Name, Object: o)))
                .ToList();
            var missingObjects = new List<string>();
            foreach (var name in referenced.Select(x => x.Object).Distinct(StringComparer.Ordinal))
            {
                if (await TryCountAsync(TargetSql("copy.object_exists"), ct, ("OWN", owner), ("NAME", name)).ConfigureAwait(false) is not > 0)
                {
                    missingObjects.Add(name); // 확인하지 못해도 없는 것으로 본다(막는 쪽).
                }
            }

            if (missingObjects.Count > 0)
            {
                var columns = referenced.Where(x => missingObjects.Contains(x.Object)).Select(x => x.Column).Distinct(StringComparer.Ordinal);
                blocked.Add($"열 {string.Join(", ", columns)} 의 기본값·가상 열 식이 가리키는 객체 {string.Join(", ", missingObjects)} 가 대상 소유자({owner})에 없습니다");
            }

            // 이름 겹침: 원본 이름 그대로 만드는 제약·인덱스·작업 안 FK.
            // 서버가 지은 이름 판정은 실행기와 같아야 한다(DBMS 마다 다르다: Oracle SYS_…, Tibero <소유자>_CON<숫자>).
            // 원본 이름을 보는 판정이라 원본 방언이다.
            bool IsServer(string name) => sourceParts.PrecheckDialect.IsServerName(name, source.Table.Owner);
            var (constraints, indexes) = CreateTableDdl.NamedObjects(effective, IsServer);
            var notCreated = CrossSkippedForeignKeys(rule, source, linked).Select(x => x.Fk.Name).ToHashSet(StringComparer.Ordinal); // 교차: 만들지 않는 FK 는 이름이 겹쳐도 상관없다
            var foreignKeys = effective.Constraints
                .Where(c => c.Kind == ConstraintKind.ForeignKey && !IsServer(c.Name) && !notCreated.Contains(c.Name)
                            && c.ReferencedTable is { } p && IsLinkedParent(p, rule, linked))
                .Select(c => c.Name);
            var inUse = new List<string>();
            foreach (var name in constraints.Concat(foreignKeys).Distinct(StringComparer.Ordinal))
            {
                inUse.AddRange(await UsedByOtherTablesAsync(TargetSql("copy.constraint_owner"), owner, name, recreated, ct).ConfigureAwait(false));
            }

            foreach (var name in indexes)
            {
                inUse.AddRange(await UsedByOtherTablesAsync(TargetSql("copy.index_owner"), owner, name, recreated, ct).ConfigureAwait(false));
            }

            if (inUse.Count > 0)
            {
                blocked.Add($"대상 소유자의 다른 표가 이미 쓰는 이름 {string.Join(", ", inUse.Distinct(StringComparer.Ordinal))}");
            }

            return blocked;
        }

        private async Task<IEnumerable<string>> UsedByOtherTablesAsync(string sql, string owner, string name, HashSet<string> recreated, CancellationToken ct)
        {
            List<object?[]> rows;
            try
            {
                rows = await CopyCatalogQueries.QueryAsync(bulk, Target, sql, ct, ("OWN", owner), ("NAME", name)).ConfigureAwait(false);
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                return [$"{name}(확인 못 함)"]; // 겹치는지 모르면 막는 쪽.
            }

            return rows.Select(r => codec.ToText(r[0]))
                .Where(t => t is not null && !recreated.Contains(t))
                .Select(t => $"{name}({t})");
        }

        /// <summary>
        /// DROP 과 함께 사라지고 되살리지 않는 것(<see cref="ICopyPrecheckDialect.NotRestoredProbes"/>, Oracle = 열 권한, 다른 계정이 준 권한, MV 로그, VPD·FGA 정책).
        /// 볼 권한이 없으면 「확인 못 함」.
        /// </summary>
        private async Task<List<string>> NotRestoredAsync(TableRule rule, CancellationToken ct)
        {
            var lost = new List<string>();
            foreach (var (id, what) in dialect.NotRestoredProbes)
            {
                // 권한·뷰 열 차이 등 어떤 서버 오류든 「확인 못 함」으로 둔다(사전 점검 전체가 예외로 끝나지 않게).
                var n = await TryCountAsync(TargetSql(id), ct, ("OWN", TargetOwner(rule)), ("TBL", rule.Table)).ConfigureAwait(false);
                if (n is null)
                {
                    lost.Add($"{what}(확인 못 함)");
                }
                else if (n > 0)
                {
                    lost.Add($"{what} {Number(n.Value)}개");
                }
            }

            return lost;
        }

        /// <summary>대상에서 COUNT(*) 하나. 서버 오류면 null.</summary>
        private async Task<long?> TryCountAsync(string sql, CancellationToken ct, params (string Name, string Value)[] binds)
        {
            try
            {
                return codec.ToInt64((await CopyCatalogQueries.QueryAsync(bulk, Target, sql, ct, binds).ConfigureAwait(false)).FirstOrDefault()?[0]) ?? 0;
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                return null;
            }
        }

        // 대상에만 있는 CHECK·FK(켜진 것). 원본에 같은 조건·같은 열의 제약이 있으면 뺀다.
        private void AddTargetOnlyConstraints(TableRule rule, TableInfo source, TableInfo target)
        {
            var sourceChecks = source.Constraints.Where(c => c.Kind == ConstraintKind.Check && c.Condition is not null)
                .Select(c => Normalize(c.Condition!)).ToHashSet(StringComparer.Ordinal);
            var sourceForeignKeys = source.Constraints.Where(c => c.Kind == ConstraintKind.ForeignKey)
                .Select(ForeignKeySignature).ToHashSet(StringComparer.Ordinal);

            var onlyInTarget = new List<string>();
            foreach (var c in target.Constraints.Where(c => c.IsEnabled))
            {
                if (c.Kind == ConstraintKind.Check && c.Condition is not null && !sourceChecks.Contains(Normalize(c.Condition)))
                {
                    onlyInTarget.Add($"{c.Name}(CHECK)");
                }
                else if (c.Kind == ConstraintKind.ForeignKey && !sourceForeignKeys.Contains(ForeignKeySignature(c)))
                {
                    onlyInTarget.Add($"{c.Name}(FK)");
                }
            }

            if (onlyInTarget.Count > 0)
            {
                _items.Add(new CheckItem(Key(rule), "TargetOnlyConstraint", CheckLevel.Confirm,
                    $"{Key(rule)}: 대상에만 있는 제약 {string.Join(", ", onlyInTarget)} 때문에 실패 행이 생길 수 있습니다."));
            }
        }

        private async Task<(bool CanDisable, IReadOnlyList<string> Triggers)> CheckTriggersAsync(TableRule rule, CancellationToken ct)
        {
            var key = Key(rule);
            var owner = TargetOwner(rule);
            var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.enabled_triggers"), ct,
                ("OWN", owner), ("TBL", rule.Table)).ConfigureAwait(false);
            var all = rows.Select(r => (Owner: codec.ToText(r[0]), Name: codec.ToText(r[1]))).ToList();

            // 트리거 이름은 그 표의 대상 소유자 기준이다(메모 D4). 다른 소유자의 트리거는 보여 주기만 한다.
            var owned = all.Where(t => t.Name is not null && CopyOwners.SameOwner(t.Owner, owner))
                .Select(t => t.Name!).ToList();
            var canDisable = _targetAccess.IsOwner(owner) || _targetAccess.Has("ALTER ANY TRIGGER");

            if (all.Count > 0)
            {
                var offer = canDisable && owned.Count > 0;
                _items.Add(new CheckItem(key, "TargetTriggers", CheckLevel.Confirm,
                    offer
                        ? $"{key}: 대상 트리거 {Number(all.Count)}개가 복사한 행에도 실행됩니다. 복사하는 동안 끄고 끝나면 다시 켤 수 있습니다."
                        : $"{key}: 복사한 행에도 트리거가 실행됩니다({Number(all.Count)}개).")
                {
                    Choices = offer ? [CheckChoice.DisableTriggers] : [],
                });
            }
            else if (!_targetAccess.IsOwner(owner))
            {
                // 소유자가 아닌 쓰기 계정은 ALL_TRIGGERS·ALL_CONSTRAINTS 에서 남의 표 트리거·참조 FK 를 못 볼 수 있다.
                _items.Add(new CheckItem(key, "TargetTriggers", CheckLevel.Pass,
                    $"{key}: 대상 계정이 소유자가 아니라 트리거·이 표를 참조하는 FK 가 보이지 않을 수 있습니다. 있으면 복사한 행에도 트리거가 실행됩니다."));
            }

            return (canDisable, owned);
        }

        // 이 표를 참조하는 대상 외래키 중 ORA-02292 를 낼 수 있는 것(메모 D15). 작업 밖 자식 표도 본다.
        private async Task<IReadOnlyList<DisabledObject>> CheckChildForeignKeysAsync(TableRule rule, IReadOnlyList<string> keys, CancellationToken ct)
        {
            if (rule.Action is not (TargetAction.DeleteAllInsert or TargetAction.DeleteRangeInsert))
            {
                return [];
            }

            var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.referencing_fks"), ct,
                ("OWN", TargetOwner(rule)), ("TBL", rule.Table)).ConfigureAwait(false);
            var blocking = rows
                .Select(r => (Owner: codec.ToText(r[0]), Table: codec.ToText(r[1]), Name: codec.ToText(r[2])))
                .Where(fk => fk.Owner is not null && fk.Table is not null && fk.Name is not null && ChildKeepsRows(fk.Owner, fk.Table, rule))
                .Select(fk => new DisabledObject(DisabledKind.ForeignKey, fk.Owner!, fk.Table!, fk.Name!))
                .ToList();
            if (blocking.Count == 0)
            {
                return [];
            }

            var canAlterAny = _targetAccess.Has("ALTER ANY TABLE");
            var disablable = blocking.Where(fk => canAlterAny || _targetAccess.IsOwner(fk.Owner)).ToList();
            var choices = new List<CheckChoice>();
            if (keys.Count > 0)
            {
                choices.Add(CheckChoice.SwitchToMerge);
            }

            if (disablable.Count > 0)
            {
                choices.Add(CheckChoice.DisableForeignKeys);
            }

            var children = string.Join(", ", blocking.Select(fk => (CopyOwners.SameOwner(fk.Owner, TargetOwner(rule)) ? "" : fk.Owner + ".")
                                                                    + $"{fk.Table}({fk.Name})").Distinct(StringComparer.Ordinal));
            _items.Add(new CheckItem(Key(rule), "ChildRowsMayBlock", CheckLevel.Confirm,
                $"{Key(rule)}: 자식 표 {children} 의 행이 남아 있으면 지울 때 {dialect.ChildRecordFoundError} 가 날 수 있습니다. 대상 처리를 PK 기준 MERGE 로 바꾸세요.")
            {
                Choices = choices,
            });
            return disablable;
        }

        // 부모 행을 지워도 자식 행이 남을 수 있는가. 작업 안에서 자식이 먼저 비워지거나 부모 범위에 맞춰 지워지면 막지 않는다.
        // 자식 규칙은 대상 계정·표로 찾고, 부모 키 규칙이면 그 부모가 이 규칙(키)인지 본다.
        private bool ChildKeepsRows(string childOwner, string childTable, TableRule parentRule)
        {
            var childRule = CopyOwners.FindByTarget(job, childOwner, childTable);
            if (childRule is null || childRule.Kind == RuleKind.Exclude)
            {
                return true;
            }

            return childRule.Action switch
            {
                TargetAction.DeleteAllInsert or TargetAction.Recreate => false,
                TargetAction.DeleteRangeInsert => !(childRule.Kind == RuleKind.ParentKey && childRule.Parent is not null
                                                    && string.Equals(CopyOwners.ParentKey(job, childRule), Key(parentRule), StringComparison.Ordinal)),
                _ => true,
            };
        }

        // ---- 건수·문자열 날짜 ----

        private async Task CountAsync(TableRule rule, CancellationToken ct)
        {
            var key = Key(rule);
            progress?.Report($"{key}: 건수 세는 중");
            var count = Build(key, () => _builder.SourceCount(key));
            if (count is null)
            {
                return;
            }

            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, count.Sql, count.Binds, ct).ConfigureAwait(false);
                _estimated[key] = sourceParts.Codec.ToInt64(rows.FirstOrDefault()?[0]) ?? 0;
            }
            catch (Exception ex) when (CopyErrors.IsNoAccess(errors, ex))
            {
                _items.Add(new CheckItem(key, "SourceNoSelect", CheckLevel.Error, $"{key}: 원본 표(또는 부모 키 규칙의 부모 표)를 읽을 권한이 없습니다."));
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                AddInvalid(key, $"원본 건수를 세지 못했습니다: {ex.Message}");
            }
        }

        // 기간 범위 안의 행 가운데 길이가 형식과 다른 문자열 날짜를 센다. 기간 열마다 그 열의 범위만으로 센다(두 열 OR 조건과 섞지 않는다).
        private async Task CheckStringDatesAsync(TableRule rule, CancellationToken ct)
        {
            if (rule.Kind != RuleKind.Period)
            {
                return;
            }

            var key = Key(rule);
            foreach (var pc in rule.PeriodColumns.Where(p => p.StringFormat is not null))
            {
                var format = pc.StringFormat!;
                if (!DateStringFormat.IsValid(format))
                {
                    AddInvalid(key, $"{pc.Column}: 지원하지 않는 날짜 형식입니다({format}).");
                    continue;
                }

                var length = DateStringFormat.Format(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), format).Length;
                var single = new CopySqlBuilder(
                    job with { Tables = [.. job.Tables.Select(r => ReferenceEquals(r, rule) ? rule with { PeriodColumns = [pc] } : r)] },
                    period, _sourceTables, _targetTables);
                var select = Build(key, () => single.SourceSelect(key, [pc.Column]));
                if (select is null)
                {
                    continue;
                }

                var binds = new Dictionary<string, BindValue>(select.Binds, StringComparer.Ordinal)
                {
                    ["LEN_CHK"] = new BindValue(BindDataType.Number, (decimal)length),
                };
                var sql = $"SELECT COUNT(*) FROM ({select.Sql}) WHERE LENGTH({SqlIdentifier.QuoteAlways(pc.Column)}) <> :LEN_CHK";
                try
                {
                    var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, sql, binds, ct).ConfigureAwait(false);
                    var wrong = sourceParts.Codec.ToInt64(rows.FirstOrDefault()?[0]) ?? 0;
                    if (wrong > 0)
                    {
                        _items.Add(new CheckItem(key, "WrongLengthDates", CheckLevel.Confirm,
                            $"{key}.{pc.Column}: 기간 범위 안에 길이가 형식({format}, {Number(length)}자)과 다른 값이 {Number(wrong)}행 있습니다. 범위에서 빠지거나 겹칠 수 있습니다.")
                        {
                            Column = pc.Column,
                        });
                    }
                }
                catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
                {
                    AddInvalid(key, $"{pc.Column}: 문자열 날짜 길이를 세지 못했습니다: {ex.Message}");
                }
            }
        }

        // ---- 부모 키 규칙·순서 ----

        private async Task CheckParentsAsync(List<TableRule> active, CancellationToken ct)
        {
            foreach (var rule in active.Where(r => r.Kind == RuleKind.ParentKey))
            {
                var key = Key(rule);
                if (rule.Parent is null)
                {
                    AddInvalid(key, "부모 키 규칙에 부모 연결이 없습니다.");
                    continue;
                }

                // 부모 표를 이번에 새로 만들면(비어 있다) 대상에 있는 이 표의 범위 지우기가 아무것도 지우지 못해 행이 겹친다.
                if (!_creating.Contains(key) && rule.Action == TargetAction.DeleteRangeInsert && _creating.Contains(CopyOwners.ParentKey(job, rule)))
                {
                    _items.Add(new CheckItem(key, "ParentWillBeCreated", CheckLevel.Error,
                        $"{key}: 부모 표 {rule.Parent.ParentTable} 를 이번에 새로 만들어 비어 있어서, 부모 범위로 대상을 지우는 이 규칙은 아무것도 지우지 못합니다. 대상 처리를 바꾸거나 부모의 「없으면 만들기」를 끄세요."));
                }

                var parent = CopyOwners.Parent(job, rule);
                if (parent is not null && parent.Kind != RuleKind.Exclude)
                {
                    continue; // 부모 규칙이 자기 표의 권한을 본다.
                }

                var sourceParent = $"{CopyOwners.ParentSource(job, rule)}.{rule.Parent.ParentTable}";
                var targetParent = $"{CopyOwners.ParentTarget(job, rule)}.{rule.Parent.ParentTable}";
                _items.Add(new CheckItem(key, "ParentExcluded", CheckLevel.Confirm,
                    $"{key}: 부모 표 {sourceParent} 가 {(parent is null ? "작업에 없어" : "제외되어 있어")} 부모 전체를 기준으로 연결합니다(대상 쪽은 {targetParent})."));

                // 작업 밖 부모는 다른 규칙이 권한을 보지 않는다: 원본 조회·대상 범위 지우기·건수 비교가 부모 표를 읽으므로 양쪽 SELECT 를 본다.
                var sourceHeld = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, sourceParts.Catalog, sourceParts.Codec, Source, _sourceAccess,
                    CopyOwners.ParentSource(job, rule), rule.Parent.ParentTable, "TABLE", sourceParts.PrecheckDialect.SupportsReadPrivilege, ct).ConfigureAwait(false);
                if (!sourceHeld.Contains("SELECT"))
                {
                    _items.Add(new CheckItem(key, "SourceNoSelect", CheckLevel.Error,
                        $"{key}: 부모 키 규칙이 읽는 원본 부모 표 {sourceParent} 에 SELECT 권한이 없습니다."));
                }

                var targetHeld = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, catalog, codec, Target, _targetAccess,
                    CopyOwners.ParentTarget(job, rule), rule.Parent.ParentTable, "TABLE", dialect.SupportsReadPrivilege, ct).ConfigureAwait(false);
                if (!targetHeld.Contains("SELECT"))
                {
                    _items.Add(new CheckItem(key, "TargetNoWrite", CheckLevel.Error,
                        $"{key}: 부모 키 규칙이 대상 범위를 정할 때 읽는 대상 부모 표 {targetParent} 에 SELECT 권한이 없습니다."));
                }
            }
        }

        // 부모 → 자식. 간선은 원본·대상 FK 중 작업 안 표끼리와 부모 키 규칙이다. 제외 규칙 표와 점검에 실패한 표는 넣지 않는다.
        private IReadOnlyList<CopyTablePlan> BuildOrder(List<CopyTablePlan> plans)
        {
            var names = plans.Select(p => Key(p.Rule)).ToList();
            // FK 의 부모(계정, 표) → 규칙 키. 원본 FK 는 원본 계정·표로, 대상 FK 는 대상 계정·표로 찾는다.
            var bySource = plans.GroupBy(p => CopyOwners.QualifiedKey(SourceOwner(p.Rule), p.Rule.Table), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => Key(g.First().Rule), StringComparer.OrdinalIgnoreCase);
            var byTarget = plans.GroupBy(p => CopyOwners.QualifiedKey(TargetOwner(p.Rule), p.Rule.Table), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => Key(g.First().Rule), StringComparer.OrdinalIgnoreCase);
            var edges = new List<(string Child, string Parent)>();
            foreach (var plan in plans)
            {
                var key = Key(plan.Rule);
                edges.AddRange(ForeignKeyEdges(key, plan.Source, bySource));
                edges.AddRange(ForeignKeyEdges(key, plan.Target, byTarget));
                if (plan.Rule.Kind == RuleKind.ParentKey && plan.Rule.Parent is not null)
                {
                    edges.Add((key, CopyOwners.ParentKey(job, plan.Rule)));
                }
            }

            var sorted = CopyOrder.Sort(names, edges);
            foreach (var cycle in sorted.Cycles)
            {
                _items.Add(new CheckItem(null, "FkCycle", CheckLevel.Confirm,
                    $"표 {string.Join(", ", cycle)} 가 외래키로 서로 이어져 있어 부모 → 자식 순서를 정할 수 없습니다. 적재 순서에 따라 실패하는 행이 생길 수 있습니다."));
            }

            var byName = plans.ToDictionary(p => Key(p.Rule), StringComparer.Ordinal);
            return [.. sorted.ParentsFirst.Select(n => byName[n])];
        }

        private static IEnumerable<(string Child, string Parent)> ForeignKeyEdges(string key, TableInfo info, Dictionary<string, string> parents)
            => info.Constraints
                .Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is not null)
                .Select(c => parents.TryGetValue(CopyOwners.QualifiedKey(c.ReferencedTable!.Owner, c.ReferencedTable.Name), out var parent) ? parent : null)
                .OfType<string>()
                .Select(parent => (key, parent));

        // ---- 시퀀스 ----

        private async Task CheckSequencesAsync(IReadOnlyList<CopyTablePlan> plans, CancellationToken ct)
        {
            foreach (var rule in job.Sequences)
            {
                var plan = plans.FirstOrDefault(p => string.Equals(Key(p.Rule), rule.Table, StringComparison.Ordinal));
                if (plan is null)
                {
                    continue; // 제외 규칙이거나 점검에 실패한 표. 규칙이 아예 없는 연결은 앞의 RuleInvalid(CopyOwners.Problems)가 막았다.
                }

                progress?.Report($"시퀀스 {rule.Sequence}: 점검 중");
                await CheckSequenceAsync(rule, ct).ConfigureAwait(false);
            }

            await SuggestSequencesAsync(plans, ct).ConfigureAwait(false);
        }

        // NEXTVAL 을 부르지 않는다(부작용, 메모 D16). LAST_NUMBER 와 원본 범위의 최대값으로 어림한다.
        private async Task CheckSequenceAsync(SequenceRule rule, CancellationToken ct)
        {
            var cannot = new CheckItem(rule.Table, "SequenceGap", CheckLevel.Confirm,
                $"{rule.Table}.{rule.Column}: 시퀀스 {rule.Sequence} 를 볼 수 없어(없거나 SELECT 권한 없음) 보정하지 않습니다. 테스트 중 PK 충돌이 날 수 있습니다.")
            {
                Column = rule.Column,
            };

            var owner = CopyOwners.SequenceOwner(job, rule);
            var info = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.sequence_info"), ct,
                ("OWN", owner), ("SEQ", rule.Sequence)).ConfigureAwait(false);
            var held = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, catalog, codec, Target, _targetAccess, owner, rule.Sequence, "SEQUENCE", dialect.SupportsReadPrivilege, ct)
                .ConfigureAwait(false);
            if (info.Count == 0 || !held.Contains("SELECT"))
            {
                _items.Add(cannot);
                return;
            }

            var next = codec.ToInt64(info[0][0]) ?? 0;
            var increment = codec.ToInt64(info[0][1]) ?? 1;
            var count = Build(rule.Table, () => _builder.SourceCount(rule.Table));
            if (count is null || !count.Sql.StartsWith(CountPrefix, StringComparison.Ordinal))
            {
                return;
            }

            long max;
            try
            {
                var sql = $"SELECT MAX({SqlIdentifier.QuoteAlways(rule.Column)}){count.Sql[CountPrefix.Length..]}";
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, sql, count.Binds, ct).ConfigureAwait(false);
                max = sourceParts.Codec.ToInt64(rows.FirstOrDefault()?[0]) ?? long.MinValue;
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                _items.Add(new CheckItem(rule.Table, "SequenceGap", CheckLevel.Confirm,
                    $"{rule.Table}.{rule.Column}: 원본 최대값을 읽지 못해 시퀀스를 보정할 수 있는지 알 수 없습니다: {ex.Message}")
                {
                    Column = rule.Column,
                });
                return;
            }

            var gap = PrecheckRules.SequenceGap(next, max, increment);
            if (gap <= 0)
            {
                return;
            }

            var large = gap >= job.Options.SequenceGapConfirm;
            _items.Add(new CheckItem(rule.Table, "SequenceGap", CheckLevel.Confirm,
                large
                    ? $"{rule.Table}.{rule.Column}: 시퀀스 {rule.Sequence} 를 {Number(gap)}번 소비해야 해서 시간이 오래 걸릴 수 있습니다. 진행할지 정하세요."
                    : $"{rule.Table}.{rule.Column}: 복사 뒤 시퀀스 {rule.Sequence} 를 약 {Number(gap)}번 소비해 값을 앞당깁니다.")
            {
                Column = rule.Column,
                Choices = large ? [CheckChoice.ProceedSequenceGap] : [],
            });
        }

        // 작업에 시퀀스 규칙이 없는 숫자 키 열에 이름 규칙 후보를 알려 준다(통과 항목, 사용자가 작업에 연결하면 보정 대상이 된다).
        private async Task SuggestSequencesAsync(IReadOnlyList<CopyTablePlan> plans, CancellationToken ct)
        {
            var candidates = plans.Select(p => (Plan: p, Numeric: p.KeyColumns.Where(k => IsNumber(p.Target, k)).ToList())).Where(x => x.Numeric.Count > 0).ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            // 시퀀스 이름은 표의 대상 계정마다 한 번 읽는다(후보는 그 표의 대상 계정에 있는 시퀀스).
            var namesByOwner = new Dictionary<string, List<string>>(CopyOwners.OwnerComparer);
            foreach (var owner in candidates.Select(c => TargetOwner(c.Plan.Rule)).Distinct(CopyOwners.OwnerComparer))
            {
                try
                {
                    var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.sequence_names"), ct, ("OWN", owner)).ConfigureAwait(false);
                    namesByOwner[owner] = [.. rows.Select(r => codec.ToText(r[0])).OfType<string>()];
                }
                catch (Exception ex) when (CopyErrors.IsNoAccess(errors, ex))
                {
                    namesByOwner[owner] = [];
                }
            }

            foreach (var (plan, numeric) in candidates)
            {
                var key = Key(plan.Rule);
                foreach (var guess in SequenceGuess.Candidates(plan.Rule.Table, numeric, namesByOwner[TargetOwner(plan.Rule)]))
                {
                    if (job.Sequences.Any(s => s.Table == key && s.Column == guess.Column))
                    {
                        continue;
                    }

                    _items.Add(new CheckItem(key, "SequenceGap", CheckLevel.Pass,
                        $"{key}.{guess.Column}: 시퀀스 후보 {TargetOwner(plan.Rule)}.{guess.Sequence}. 작업에 연결하면 복사 뒤 값을 맞춥니다.")
                    {
                        Column = guess.Column,
                        Sequence = guess.Sequence,
                    });
                }
            }
        }

        // ---- 읽기 일관성 ----

        private async Task CheckReadConsistencyAsync(CancellationToken ct)
        {
            if (!job.ConsistentRead)
            {
                return;
            }

            // 같은 시점 읽기는 원본의 일이라 파라미터·안내 글 모두 원본 방언에서 꺼낸다(같은 DBMS 면 예전과 같다).
            var sourceDialect = sourceParts.PrecheckDialect;
            long? undo = null;
            if (sourceDialect.ReadConsistencyParameter is { } parameter)
            {
                try
                {
                    var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, SourceSql("copy.parameter_value"), ct,
                        ("PNAME", parameter)).ConfigureAwait(false);
                    undo = sourceParts.Codec.ToInt64(rows.FirstOrDefault()?[0]);
                }
                catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
                {
                    // V$PARAMETER 를 읽을 권한이 없으면 알 수 없음으로 둔다.
                }
            }

            // 못 읽었으면(null) 어림 시간은 글에 쓰이지 않으므로 계산하지 않는다(옮기기 전과 같음).
            var seconds = undo is null ? 0 : (long)Math.Ceiling(_estimated.Values.Sum() / AssumedRowsPerSecond);
            var (level, message) = sourceDialect.ReadConsistencyHint(seconds, undo);
            _items.Add(new CheckItem(null, "ReadConsistency", level, message));
        }

        // ---- 미리 보기 ----

        // 원본 SELECT, 대상 DELETE(자식 → 부모), 다시 만들 표의 DROP(자식 → 부모), 끌 수 있는 트리거·FK, 시퀀스 소비 순서. 정책은 문장을 보낼 세션의 프로필로 판단한다.
        private async Task<IReadOnlyList<PreviewStatement>> BuildPreviewAsync(IReadOnlyList<CopyTablePlan> order, CancellationToken ct)
        {
            var list = new List<PreviewStatement>();
            foreach (var plan in order)
            {
                var key = Key(plan.Rule);
                if (Build(key, () => _builder.SourceSelect(key, plan.Columns)) is { } select)
                {
                    list.Add(await PreviewAsync(Source, CopyCommonStatements.Query(select), PreviewSide.Source, ct).ConfigureAwait(false));
                }
            }

            // DELETE 는 표마다 그 대상 계정이 붙는다(다른 계정 표를 지우는지 미리 보기에서 보인다).
            foreach (var plan in order.Reverse())
            {
                var key = Key(plan.Rule);
                if (Build(key, () => _builder.TargetDelete(key)) is { } delete)
                {
                    list.Add(await PreviewAsync(Target, CopyCommonStatements.Delete(delete), PreviewSide.Target, ct).ConfigureAwait(false));
                }
            }

            // 다시 만들 표의 DROP(자식 → 부모). 개발 라벨 대상이 아니면 만들지 않는다. 화면이 대상 접속 이름을 한 번 다시 입력받으면 미리 받은 확인으로 넘긴다.
            if (CanRecreate)
            {
                foreach (var plan in order.Reverse().Where(p => IsRecreate(p.Rule)))
                {
                    list.Add(await PreviewAsync(Target, statements.DropTable(TargetOwner(plan.Rule), plan.Rule.Table), PreviewSide.Target, ct).ConfigureAwait(false));
                }
            }

            // 대상에 없어 만들 표(부모 → 자식): 만드는 문장 전부, 그리고 적재가 실패하면 만든 표를 지우는 DROP … PURGE(자식 → 부모).
            // 지우는 문장은 위험 명령이라 실행 중에 확인을 받을 수 없다: 여기 들어 있어야 화면이 미리 받은 확인으로 넘어간다.
            var created = order.Where(p => p.CreateMissing).ToList();
            foreach (var plan in created)
            {
                var owner = TargetOwner(plan.Rule);
                // 실행기(CopyRunner.CreateMissingTablesAsync)와 같은 함수·같은 입력이라 보여 주는 문장과 보내는 문장이 같다(교차면 대상 DBMS 로 옮긴 구조).
                foreach (var text in CrossTableMap.Build(sourceParts.Dbms, targetDbms, plan.Source, owner, new HashSet<string>(StringComparer.Ordinal),
                             name => sourceParts.PrecheckDialect.IsServerName(name, SourceOwner(plan.Rule)), sourceParts.Codec).Statements)
                {
                    list.Add(await PreviewAsync(Target, CopyCommonStatements.RecreateDdl(text), PreviewSide.Target, ct).ConfigureAwait(false));
                }
            }

            foreach (var plan in Enumerable.Reverse(created))
            {
                list.Add(await PreviewAsync(Target, statements.DropCreatedTable(TargetOwner(plan.Rule), plan.Rule.Table), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            foreach (var plan in order.Where(p => p.CanDisableTriggers))
            {
                foreach (var trigger in plan.Triggers)
                {
                    list.Add(await PreviewAsync(Target, statements.DisableTrigger(TargetOwner(plan.Rule), trigger), PreviewSide.Target, ct).ConfigureAwait(false));
                }
            }

            foreach (var fk in order.SelectMany(p => p.DisablableForeignKeys).Distinct())
            {
                list.Add(await PreviewAsync(Target, statements.DisableConstraint(fk), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            foreach (var rule in job.Sequences.Where(r => order.Any(p => string.Equals(Key(p.Rule), r.Table, StringComparison.Ordinal))))
            {
                list.Add(await PreviewAsync(Target, statements.NextValues(CopyOwners.SequenceOwner(job, rule), rule.Sequence), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            return list;
        }

        private async Task<PreviewStatement> PreviewAsync(ISession s, SqlStatement stmt, PreviewSide side, CancellationToken ct)
        {
            var decision = await policy.EvaluateAsync(new PolicyContext(s.Profile, s.Server, HistorySource.DataCopy), stmt, ct).ConfigureAwait(false);
            return new PreviewStatement(stmt.Text, side, decision.Kind, decision.Message, decision.RetypeText);
        }

        // ---- 도우미 ----

        private const string CountPrefix = "SELECT COUNT(*)";

        /// <summary>대상 세션 DBMS 의 카탈로그 SELECT 글(<c>copy.*</c>).</summary>
        private string TargetSql(string id) => CopyCatalogQueries.Sql(catalog, Target.Server, id);

        /// <summary>원본 세션 DBMS 의 카탈로그 SELECT 글(<c>copy.*</c>).</summary>
        private string SourceSql(string id) => CopyCatalogQueries.Sql(sourceParts.Catalog, Source.Server, id);

        /// <summary>대상 초기화 파라미터 값(<c>copy.parameter_value</c>)을 글자로. 행이 없거나 서버 오류면 null.</summary>
        private async Task<string?> TargetParameterAsync(string name, CancellationToken ct)
        {
            try
            {
                return codec.ToText((await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.parameter_value"), ct,
                    ("PNAME", name)).ConfigureAwait(false)).FirstOrDefault()?[0]);
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                return null; // V$PARAMETER 를 읽지 못하면 알 수 없음.
            }
        }

        private string Key(TableRule rule) => CopyOwners.Key(job, rule);

        private string SourceOwner(TableRule rule) => CopyOwners.Source(job, rule);

        private string TargetOwner(TableRule rule) => CopyOwners.Target(job, rule);

        // 규칙이 어긋나 문장을 못 만들면 그 표만 오류로 알리고 점검은 이어 간다.
        private T? Build<T>(string table, Func<T?> make)
            where T : class
        {
            try
            {
                return make();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                AddInvalid(table, ex.Message);
                return null;
            }
        }

        private void AddInvalid(string table, string message)
        {
            var text = $"{table}: {message}";
            if (!_items.Any(i => i.Code == "RuleInvalid" && i.Message == text))
            {
                _items.Add(new CheckItem(table, "RuleInvalid", CheckLevel.Error, text));
            }
        }

        private static bool IsNumber(TableInfo info, string column)
            => info.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.Ordinal))?.DataType
                is { } t && (t.StartsWith("NUMBER", StringComparison.OrdinalIgnoreCase) || t.StartsWith("FLOAT", StringComparison.OrdinalIgnoreCase));

        private static string Normalize(string condition) => Whitespace().Replace(condition, "").ToUpperInvariant();

        private static string ForeignKeySignature(ConstraintInfo c)
            => string.Join(',', c.Columns) + ">" + c.ReferencedTable?.Name;

        private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();
}
