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
                    $"{x.Key}: 대상 테이블 {x.Table} 이(가) 같은 DB 에서 원본으로 읽는 표입니다. 대상 정리가 원본 행을 지울 수 있어 실행할 수 없습니다. 대상 계정을 바꾸세요."))],
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
    private sealed partial class Run(IBulkExecutionService bulk, IMetadataProvider metadata, IExecutionPolicy policy, CopyDbmsParts sourceParts, Dbms targetDbms, ISqlCatalog catalog,
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

        /// <summary>규칙 키 → 표를 만들기 전에 대상에 만드는 시퀀스(<see cref="PlanLinkedSequencesAsync"/>). 만드는 표만 들어 있다.</summary>
        private readonly Dictionary<string, IReadOnlyList<CopySequencePlan>> _sequencePlans = new(StringComparer.Ordinal);
        private SessionAccess _sourceAccess = null!;
        private SessionAccess _targetAccess = null!;
        private CopySqlBuilder _builder = null!;
        private IReadOnlyList<IReadOnlyList<string>> _stages = [];

        /// <summary>같은 시점 읽기로 동시에 복사할 때(PF2b) 원본의 시점 지정 읽기. 미리 보기 SELECT 에 AS OF 절을 붙이고 FLASHBACK 권한을 본다. 아니면 null.</summary>
        private CopyReadPointSupport? _readPoint;

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
            if (CopyConcurrency.Notice(job, _stages, sourceParts.Dbms) is { } concurrency)
            {
                _items.Add(concurrency); // 동시 테이블 수(PF2)
            }

            if (CopyConcurrency.Planned(job, _stages, sourceParts.Dbms).ReadsAtPoint)
            {
                await CheckReadPointAsync(order, ct).ConfigureAwait(false); // 같은 시점 읽기 + 동시(PF2b): 시작 SCN·FLASHBACK 권한
            }

            order = await CheckSequencesAsync(plans, order, ct).ConfigureAwait(false);
            order = await CheckExternalLoadAsync(order, ct).ConfigureAwait(false);
            await CheckReadConsistencyAsync(ct).ConfigureAwait(false);
            var preview = await BuildPreviewAsync(order, ct).ConfigureAwait(false);
            return new PrecheckReport(_items, order, [.. preview, .. ExternalLoadPreview(order)], _estimated) { Stages = _stages, ExternalTool = _externalTool };
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
                        ? " 시노님 이름이면 원본 접속을 연 뒤 테이블을 다시 추가하세요(실제 계정·테이블로 풀어 저장합니다)."
                        : string.Empty;
                    _items.Add(new CheckItem(key, "SourceNoSelect", CheckLevel.Error,
                        $"{key}: 원본에 테이블 {sourceOwner}.{rule.Table} 이(가) 없거나 SELECT 권한이 없습니다(권한이 없으면 테이블이 없는 것처럼 보입니다).{hint}"));
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
                        source = await PlanLinkedSequencesAsync(rule, key, source, creating: true, ct).ConfigureAwait(false);
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
                        : $"{key}: 대상에 테이블 {targetOwner}.{rule.Table} 이(가) 없거나 테이블이 아니거나(뷰·시노님·MV) 접근 권한이 없습니다(권한이 없으면 테이블이 없는 것처럼 보입니다)."
                          + (createRefused is null ? string.Empty : $" 「없으면 만들기」를 쓸 수 없습니다: {createRefused}")));
                }

                if (source is null || target is null)
                {
                    continue;
                }

                if (IsRecreate(rule) && CanRecreate)
                {
                    source = await PlanLinkedSequencesAsync(rule, key, source, creating: false, ct).ConfigureAwait(false);
                }

                _sourceTables[key] = source;
                // 다시 만들 표는 적재할 때 원본 구조다: 열·키·문장을 옛 대상 구조가 아니라 다시 만든 구조로 정한다.
                _targetTables[key] = IsRecreate(rule) && CanRecreate
                    ? PrecheckRules.AsRecreated(source, targetOwner)
                    : await AdjustIdentityAsync(target, targetOwner, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 표를 만들 때(없으면 만들기·다시 만들기, 같은 DBMS·교차 모두) 원본 테이블에 연결된 시퀀스를 정한다(조정 결정 76, 결정 50 을 대신함).
        /// 연결된 시퀀스 = 열 DEFAULT 의 <c>시퀀스.NEXTVAL</c> + (<paramref name="creating"/> 일 때) 그 테이블 트리거가 참조하는 시퀀스(ALL_DEPENDENCIES — 원본 트리거는 새 표에만 만든다).
        /// 원본 소유자(또는 소유자 없음)의 시퀀스가 대상 소유자에 없으면 테이블보다 먼저 원본 속성으로 만든다(<see cref="CopySequencePlan"/>, <see cref="CopyCheckCodes.SequenceCreated"/>).
        /// 만들 수 없으면(CREATE SEQUENCE 권한 없음·원본 속성 못 읽음) 그 열의 DEFAULT 만 빼고 만든다(<see cref="CopyCheckCodes.SequenceDefaultDropped"/>).
        /// 다른 소유자의 시퀀스는 만들지 않는다: 대상에 없으면 DEFAULT 를 뺀다. 대상에 이미 있으면 건드리지 않고, 다음 값이 원본 키 최댓값 이하면 알린다(<see cref="CopyCheckCodes.SequenceBehind"/>).
        /// Oracle 11g 대상은 시퀀스 기본값을 쓸 수 없어(12c 부터, ORA-00984) DEFAULT 는 빼되 시퀀스는 만든다(조정 결정 66). 시퀀스 목록을 읽지 못하면 있는 것으로 보고 그대로 둔다.
        /// 뺀 구조를 점검 계획(<see cref="CopyTablePlan.Source"/>)에 담으므로 미리 보기 DDL 과 실행 DDL 이 같다.
        /// </summary>
        private async Task<TableInfo> PlanLinkedSequencesAsync(TableRule rule, string key, TableInfo source, bool creating, CancellationToken ct)
        {
            var targetOwner = TargetOwner(rule);
            var sourceOwner = SourceOwner(rule);
            var oldOracleTarget = targetDbms == Dbms.Oracle && Target.Server.MajorVersion is > 0 and < 12;
            var canCreate = _targetAccess.Has("CREATE SEQUENCE") || _targetAccess.Has("CREATE ANY SEQUENCE");

            // (열 이름 또는 null = 트리거, 소유자, 시퀀스) — 나온 순서.
            var uses = new List<(string? Column, string? Owner, string Name)>();
            foreach (var c in source.Columns.Where(c => !c.IsVirtual && !c.IsIdentity))
            {
                uses.AddRange(PrecheckRules.SequenceReferences(c.DefaultValue).Select(r => ((string?)c.Name, r.Owner, r.Name)));
            }

            if (creating)
            {
                uses.AddRange((await TriggerSequencesAsync(sourceOwner, rule.Table, ct).ConfigureAwait(false)).Select(r => ((string?)null, (string?)r.Owner, r.Name)));
            }

            _sequencePlans[key] = [];
            if (uses.Count == 0)
            {
                return source;
            }

            var known = new Dictionary<string, HashSet<string>?>(CopyOwners.OwnerComparer);
            var drop = new HashSet<string>(StringComparer.Ordinal);
            var plans = new List<CopySequencePlan>();
            var linkedColumns = new HashSet<string>(StringComparer.Ordinal); // 대상 스키마에 있거나 만드는 시퀀스를 쓰는 기본값 열
            foreach (var group in uses.GroupBy(u => (Linked: IsLinkedSequenceOwner(u.Owner, sourceOwner), Owner: IsLinkedSequenceOwner(u.Owner, sourceOwner) ? targetOwner : u.Owner!, u.Name)))
            {
                var (linked, owner, name) = group.Key;
                var columns = group.Select(u => u.Column).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                if (!known.TryGetValue(owner, out var names))
                {
                    names = await ReadSequenceNamesAsync(owner, ct).ConfigureAwait(false);
                    known[owner] = names;
                }

                if (names is null)
                {
                    continue; // 목록을 읽지 못했다: 있는 것으로 보고 그대로 둔다(결정 50 과 같다)
                }

                var keyColumns = columns.Count > 0 ? columns : TriggerKeyColumn(source);
                if (names.Contains(name))
                {
                    linkedColumns.UnionWith(columns);
                    await CheckExistingSequenceAsync(rule, key, owner, name, keyColumns, ct).ConfigureAwait(false);
                    continue;
                }

                string? reason = null;
                SequenceSpec? spec = null;
                if (!linked)
                {
                    reason = $"대상 {owner} 에 시퀀스 {name} 이(가) 없어(다른 계정의 시퀀스는 만들지 않습니다)";
                }
                else if (!canCreate)
                {
                    reason = $"대상 {owner} 에 시퀀스 {name} 이(가) 없고 CREATE SEQUENCE 권한이 없어 만들 수 없으므로";
                }
                else
                {
                    spec = await ReadSourceSequenceAsync(sourceOwner, name, ct).ConfigureAwait(false);
                    reason = spec is null ? $"대상 {owner} 에 시퀀스 {name} 이(가) 없고 원본 시퀀스 {sourceOwner}.{name} 의 속성을 읽지 못해 만들 수 없으므로" : null;
                }

                if (spec is not null)
                {
                    plans.Add(new CopySequencePlan(owner, name, spec) { KeyColumns = keyColumns, DefaultColumns = columns });
                    linkedColumns.UnionWith(columns);
                    continue;
                }

                foreach (var column in columns.Where(drop.Add))
                {
                    var value = source.Columns.First(c => c.Name == column).DefaultValue!.Trim();
                    _items.Add(new CheckItem(key, CopyCheckCodes.SequenceDefaultDropped, CheckLevel.Confirm,
                        $"{key}: {reason} 열 {column} 의 기본값({value})을 빼고 만듭니다. 시퀀스를 만든 뒤 기본값을 다시 걸어야 합니다.")
                    {
                        Column = column,
                    });
                }
            }

            if (oldOracleTarget)
            {
                foreach (var column in linkedColumns.Where(c => !drop.Contains(c)).Order(StringComparer.Ordinal).ToList())
                {
                    drop.Add(column);
                    var value = source.Columns.First(c => c.Name == column).DefaultValue!.Trim();
                    var made = plans.Any(p => p.DefaultColumns.Contains(column, StringComparer.Ordinal)) ? " 시퀀스는 만듭니다." : string.Empty;
                    _items.Add(new CheckItem(key, CopyCheckCodes.SequenceDefaultDropped, CheckLevel.Confirm,
                        $"{key}: 대상 서버(Oracle {Target.Server.MajorVersion})는 시퀀스를 열 기본값으로 쓸 수 없어(12c 부터 가능) 열 {column} 의 기본값({value})을 빼고 만듭니다.{made} 값은 그대로 복사되며, 새 행에 값을 채우려면 트리거 등으로 따로 처리해야 합니다.")
                    {
                        Column = column,
                    });
                }
            }

            if (plans.Count > 0)
            {
                var list = string.Join(", ", plans.Select(p => $"{p.Owner}.{p.Name}(START WITH {p.Spec.StartWith})"));
                _items.Add(new CheckItem(key, CopyCheckCodes.SequenceCreated, CheckLevel.Confirm,
                    $"{key}: 원본 테이블에 연결된 시퀀스 {list} 이(가) 대상에 없어 테이블보다 먼저 원본 속성(증가·최소·최대·캐시·순환·순서)으로 만듭니다."
                    + " START WITH 는 원본 다음 값이고, 적재 뒤 키 최댓값이 그 이상이면 그 위로 맞춥니다. 테이블을 만들지 못하거나 적재를 끝내지 못해 지우면 이 시퀀스도 지웁니다(만드는 문장은 미리 보기에 있습니다)."));
            }

            _sequencePlans[key] = plans;
            return PrecheckRules.WithoutDefaults(source, drop);
        }

        /// <summary>시퀀스 소유자가 없거나 원본 소유자인가(대상 소유자의 같은 이름 시퀀스를 쓰게 되어 없으면 만든다).</summary>
        private static bool IsLinkedSequenceOwner(string? owner, string sourceOwner) => owner is null || CopyOwners.SameOwner(owner, sourceOwner);

        /// <summary>트리거만 쓰는 시퀀스의 맞추기 기준 열: 숫자 단일 PK 열(없으면 빈 목록 = 맞추지 않는다).</summary>
        private static IReadOnlyList<string> TriggerKeyColumn(TableInfo source)
        {
            var pk = source.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.PrimaryKey);
            return pk is { Columns.Count: 1 } && IsNumber(source, pk.Columns[0]) ? pk.Columns : [];
        }

        /// <summary>원본 테이블 트리거가 참조하는 시퀀스(<c>copy.trigger_sequences</c>, 원본 세션). 읽지 못하면 빈 목록.</summary>
        private async Task<IReadOnlyList<(string Owner, string Name)>> TriggerSequencesAsync(string owner, string table, CancellationToken ct)
        {
            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, SourceSql("copy.trigger_sequences"), ct, ("OWN", owner), ("TBL", table)).ConfigureAwait(false);
                var result = new List<(string Owner, string Name)>();
                foreach (var row in rows)
                {
                    if (sourceParts.Codec.ToText(row[0]) is { } o && sourceParts.Codec.ToText(row[1]) is { } n)
                    {
                        result.Add((o, n));
                    }
                }

                return result;
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                return [];
            }
        }

        /// <summary>원본 시퀀스 속성(<c>copy.sequence_attrs</c>, 원본 세션). 없거나 읽지 못하면 null.</summary>
        private async Task<SequenceSpec?> ReadSourceSequenceAsync(string owner, string name, CancellationToken ct)
        {
            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, SourceSql("copy.sequence_attrs"), ct, ("OWN", owner), ("SEQ", name)).ConfigureAwait(false);
                return rows.Count == 0 ? null : CopySequenceDdl.FromRow(rows[0], v => sourceParts.Codec.ToText(v));
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                return null;
            }
        }

        /// <summary>
        /// 대상에 이미 있는 시퀀스(건드리지 않는다): 다음 값(LAST_NUMBER)이 원본 키 열 최댓값 이하면 확인 항목을 낸다(새 행의 키가 겹칠 수 있다).
        /// 작업에 이 표·시퀀스가 연결돼 있으면 복사 뒤 맞추므로 내지 않는다. 읽지 못하면 아무것도 내지 않는다.
        /// </summary>
        private async Task CheckExistingSequenceAsync(TableRule rule, string key, string owner, string name, IReadOnlyList<string> keyColumns, CancellationToken ct)
        {
            if (keyColumns.Count == 0 || job.Sequences.Any(s => s.Table == key && string.Equals(s.Sequence, name, StringComparison.Ordinal)))
            {
                return;
            }

            try
            {
                var info = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.sequence_info"), ct, ("OWN", owner), ("SEQ", name)).ConfigureAwait(false);
                if (info.Count == 0 || codec.NumberText(info[0][0]) is not { } nextText || !CopySequenceDdl.TryNumber(nextText, out var next))
                {
                    return;
                }

                foreach (var column in keyColumns)
                {
                    var max = await CopyCatalogQueries.QueryAsync(bulk, Source,
                        $"SELECT MAX({SqlIdentifier.QuoteAlways(column)}) FROM {CopyCommonStatements.Qualified(SourceOwner(rule), rule.Table)}", ct).ConfigureAwait(false);
                    if (max.Count > 0 && sourceParts.Codec.NumberText(max[0][0]) is { } topText && CopySequenceDdl.TryNumber(topText, out var top) && next <= top)
                    {
                        _items.Add(new CheckItem(key, CopyCheckCodes.SequenceBehind, CheckLevel.Confirm,
                            $"{key}: 대상에 이미 있는 시퀀스 {owner}.{name} 은(는) 건드리지 않습니다. 그 다음 값({nextText})이 원본 {column} 최댓값({topText}) 이하라 복사 뒤 새 행의 키가 겹칠 수 있습니다. 작업에 이 시퀀스를 연결하면 복사 뒤 값을 맞춥니다.")
                        {
                            Column = column,
                            Sequence = name,
                        });
                        return;
                    }
                }
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                // 알 수 없으면 알리지 않는다(시퀀스는 건드리지 않는다).
            }
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
                return $"대상 접속 '{Target.Profile.Name}' 이 개발 접속이 아니라 테이블을 만들 수 없습니다. 개발 접속에서만 만들 수 있습니다.";
            }

            var objects = await TryCountAsync(TargetSql("copy.object_exists"), ct, ("OWN", targetOwner), ("NAME", table)).ConfigureAwait(false);
            return objects switch
            {
                null => "같은 이름의 객체가 있는지 확인하지 못했습니다(ALL_OBJECTS 조회 실패).",
                > 0 => $"{targetOwner} 에 같은 이름의 테이블이 아닌 객체가 있습니다.",
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
                ? $"{key}: 더할 때 적은 시노님 {rule.WrittenAs} 이(가) 지금은 {moved.Owner}.{moved.Table} 을(를) 가리킵니다. 작업은 저장된 {saved} 를 그대로 복사합니다. 바꾸려면 테이블을 다시 추가하세요."
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
            var noTargetTriggers = false;
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
                (canDisableTriggers, triggers, noTargetTriggers) = await CheckTriggersAsync(rule, ct).ConfigureAwait(false);
            }

            IReadOnlyList<DisabledObject> disablable = creating ? [] : await CheckChildForeignKeysAsync(rule, keys, ct).ConfigureAwait(false);
            var companions = creating || (IsRecreate(rule) && CanRecreate)
                ? await PlanCompanionsAsync(rule, key, source, creating, ct).ConfigureAwait(false)
                : CompanionDdl.Empty;

            if (readable)
            {
                await CountAsync(rule, ct).ConfigureAwait(false);
                await CheckStringDatesAsync(rule, ct).ConfigureAwait(false);
            }

            return new CopyTablePlan(rule, source, target, columns, keys)
            {
                CreateMissing = creating,
                CreateSequences = _sequencePlans.GetValueOrDefault(key) ?? [],
                Companions = companions,
                CanDisableTriggers = canDisableTriggers,
                Triggers = triggers,
                NoTargetTriggers = noTargetTriggers,
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

            _items.Add(new CheckItem(Key(rule), "SourceNoSelect", CheckLevel.Error, $"{Key(rule)}: 원본 테이블 {SourceOwner(rule)}.{rule.Table} 에 SELECT 권한이 없습니다."));
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
                    $"{Key(rule)}: 대상 테이블 {TargetOwner(rule)}.{rule.Table} 에 필요한 권한이 없습니다: {string.Join(", ", missing)}."));
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
                    $"{key}: 테이블을 지우고 다시 만들 권한이 없습니다: {string.Join(", ", missing)}."));
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
            var message = $"{key}: 대상 테이블 {owner}.{rule.Table} 을(를) 지우고 원본 구조로 다시 만든 뒤 넣습니다."
                          + dialect.RecycleBinNote(tablespace)
                          + " DROP·CREATE 는 DDL 이라 커밋 방식과 상관없이 되돌릴 수 없습니다."
                          + $" 대상의 트리거 {Number(triggerCount)}개·다른 계정에 준 권한·이 테이블을 참조하던 FK 는 적재 뒤 되살립니다"
                          + "(참조 FK 는 지우기 전에 떼어 내고, 대상 계정에 보이지 않는 FK 가 있으면 지우지 않고 멈춥니다).";
            message += " 원본의 함수 기반 인덱스·작업 밖 부모 FK 는 적재 뒤 함께 만듭니다(「함께 만드는 객체」 항목). 파티션·압축·테이블스페이스·저장 속성은 대상 기본값을 씁니다.";

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
                    $"{key}: 테이블을 만들 권한이 없습니다: CREATE TABLE."));
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

            var message = $"{key}: 대상에 테이블 {owner}.{rule.Table} 이(가) 없어 원본 구조로 만든 뒤 넣습니다(만드는 문장은 미리 보기에 있습니다). 새 표라 대상을 지우지 않습니다."
                          + " 넣기가 실패하면 만든 테이블을 휴지통 없이 지웁니다(DROP … PURGE)."
                          + " 원본의 트리거·권한·함수 기반 인덱스·작업 밖 부모 FK 는 적재 뒤 함께 만듭니다(「함께 만드는 객체」 항목)."
                          + " 만들지 않는 것: 시노님, 파티션·압축·테이블스페이스·저장 속성(대상 기본값을 씁니다).";

            message += CrossSkippedForeignKeyNote(rule, source, linked);
            message += dialect.RecreateDifferenceNote(source.Columns.Any(c => c.Domain is not null)) + CrossCreateNote(source);
            _items.Add(new CheckItem(key, CopyCheckCodes.CreateTable, CheckLevel.Confirm, message));
        }

        // ---- 새 표에 딸린 객체(SQ2, 조정 결정 78) ----

        /// <summary>
        /// 만드는 표에 적재 뒤 함께 만들 객체(<see cref="CompanionDdl"/>)를 원본 카탈로그로 정하고 「함께 만드는 객체」 항목 하나로 알린다(<see cref="CopyCheckCodes.CompanionObjects"/>).
        /// 함수 기반 인덱스·작업 밖 부모 FK 는 만드는 표 모두, 트리거·권한은 「없으면 만들기」 표만(<paramref name="creating"/>; 「다시 만들기」는 대상 쪽 받아 두기·되살리기가 우선한다).
        /// 시노님·파티션·압축·테이블스페이스·저장 속성은 만들지 않는다(대상 기본값, 항목 글에 적는다). 원본을 읽지 못한 것은 빼고 그 까닭을 적는다.
        /// </summary>
        private async Task<CompanionDdl> PlanCompanionsAsync(TableRule rule, string key, TableInfo source, bool creating, CancellationToken ct)
        {
            var sourceOwner = SourceOwner(rule);
            var targetOwner = TargetOwner(rule);
            var cross = sourceParts.Dbms != targetDbms;
            var made = new List<string>();
            var skipped = new List<string>();
            var mapped = CrossTableMap.MapTable(sourceParts.Dbms, targetDbms, source, sourceParts.Codec);
            var removed = source.Columns.Select(c => c.Name).Except(mapped.Target.Columns.Select(c => c.Name), StringComparer.Ordinal)
                .Concat(mapped.LobMappedColumns.Keys).ToHashSet(StringComparer.Ordinal);

            // 1. 함수 기반 인덱스: 식은 원본 세션의 인덱스 열 조회로 읽는다(TableInfo 에는 식이 없다).
            var indexes = new List<string>();
            foreach (var index in source.Indexes.Where(CopyCompanions.IsFunctionIndex))
            {
                IReadOnlyList<IndexColumnDetail>? columns = null;
                try
                {
                    columns = await sourceParts.Recreate.IndexColumnsAsync(Source, source.Table, index.Name, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
                {
                    // 읽지 못함: 아래에서 뺀다.
                }

                if (columns is not { Count: > 0 })
                {
                    skipped.Add($"함수 기반 인덱스 {index.Name}(식을 읽지 못함)");
                }
                else if (CopyCompanions.UsesColumn(columns, removed) is { } column)
                {
                    skipped.Add($"함수 기반 인덱스 {index.Name}(대상에 만들지 않거나 LOB 로 옮기는 열 {column} 을 씀)");
                }
                else if (CopyCompanions.FunctionIndex(targetOwner, rule.Table, index, columns, sourceOwner) is { } ddl)
                {
                    indexes.Add(ddl);
                    made.Add($"함수 기반 인덱스 {index.Name}");
                }
            }

            // 2. 트리거·권한(새 표만): 원본 세션으로 받아 두기(다시 만들기의 대상 받아 두기와 같은 조회)를 하고 대상 소유자 이름으로 바꾼다.
            var triggers = new List<string>();
            var triggerObjects = new List<DisabledObject>();
            var grants = new List<string>();
            if (creating)
            {
                PreservedDdl? kept = null;
                try
                {
                    kept = await sourceParts.Recreate.CaptureAsync(Source, sourceOwner, rule.Table, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (CopyErrors.IsServer(errors, ex) || ex is InvalidOperationException or BulkDeniedException)
                {
                    skipped.Add($"원본 트리거·권한(읽지 못함: {CopyRunner.SafeError(ex.Message)})");
                }

                if (kept is not null)
                {
                    var canTrigger = _targetAccess.Has("CREATE TRIGGER") || _targetAccess.Has("CREATE ANY TRIGGER");
                    for (var i = 0; i < kept.Triggers.Count && i < kept.TriggerObjects.Count; i++)
                    {
                        var obj = kept.TriggerObjects[i];
                        if (!CopyOwners.SameOwner(obj.Owner, sourceOwner))
                        {
                            if (i == 0 || kept.TriggerObjects[i - 1] != obj)
                            {
                                skipped.Add($"트리거 {obj.Owner}.{obj.Name}(다른 계정의 트리거)");
                            }

                            continue;
                        }

                        if (!canTrigger)
                        {
                            if (i == 0 || kept.TriggerObjects[i - 1] != obj)
                            {
                                skipped.Add($"트리거 {obj.Name}(CREATE TRIGGER 권한 없음)");
                            }

                            continue;
                        }

                        if (i == 0 || kept.TriggerObjects[i - 1] != obj)
                        {
                            made.Add($"트리거 {obj.Name}");
                        }

                        triggers.Add(CopyCompanions.Reowned(kept.Triggers[i], sourceOwner, targetOwner));
                        triggerObjects.Add(new DisabledObject(DisabledKind.Trigger, targetOwner, rule.Table, obj.Name));
                    }

                    foreach (var grant in kept.Grants)
                    {
                        var grantee = CopyCompanions.Grantee(grant);
                        if (grantee is null || CopyOwners.SameOwner(grantee, targetOwner))
                        {
                            skipped.Add($"권한 {grant}({(grantee is null ? "받는 쪽을 읽지 못함" : "대상 소유자 자신")})");
                            continue;
                        }

                        var text = CopyCompanions.Reowned(grant, sourceOwner, targetOwner);
                        if (grantee != "PUBLIC"
                            && await TryCountAsync(TargetSql("copy.user_exists"), ct, ("NAME", grantee)).ConfigureAwait(false) is not > 0)
                        {
                            made.Add($"권한 → {grantee}(대상에서 사용자로 확인되지 않음: 롤이면 붙고, 없으면 실행 때 알림)");
                        }
                        else
                        {
                            made.Add($"권한 → {grantee}");
                        }

                        grants.Add(text);
                    }
                }
            }

            // 3. 작업 밖 부모 FK: 부모가 같은 원본 계정의 표이고 대상 소유자에 그 표가 있으면 적재 뒤 붙여 본다(실패하면 뺀다, NOVALIDATE 로 바꾸지 않는다).
            var outer = new List<(string Parent, string Ddl, string? Name)>();
            var linked = BuiltTables(targetOwner, sourceOwner);
            var parents = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fk in source.Constraints.Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is { } p && !IsLinkedParent(p, rule, linked)))
            {
                var parent = fk.ReferencedTable!;
                if (!CopyOwners.SameOwner(parent.Owner, sourceOwner))
                {
                    skipped.Add($"FK {fk.Name}(→{parent.Owner}.{parent.Name}: 다른 계정의 부모)");
                }
                else if (await TryCountAsync(TargetSql("copy.target_is_table"), ct, ("OWN", targetOwner), ("TBL", parent.Name)).ConfigureAwait(false) is > 0)
                {
                    parents.Add(parent.Name);
                }
                else
                {
                    skipped.Add($"FK {fk.Name}(→{parent.Name}: 대상 {targetOwner} 에 부모 테이블 없음)");
                }
            }

            if (parents.Count > 0)
            {
                try
                {
                    var ddls = (await sourceParts.Recreate.SourceForeignKeysAsync(Source, sourceOwner, rule.Table, parents, targetOwner, ct).ConfigureAwait(false))
                        .Where(f => parents.Contains(f.Parent)).ToList();
                    var (keptFks, skippedFks) = CrossTableMap.FilterForeignKeys(sourceParts.Dbms, targetDbms, source, sourceParts.Codec, ddls, _ => null);
                    outer.AddRange(keptFks);
                    made.AddRange(keptFks.Select(f => $"FK {f.Name ?? "(서버 이름)"}(→{f.Parent}, 작업 밖 부모)"));
                    skipped.AddRange(skippedFks.Select(x => "FK " + x));
                }
                catch (Exception ex) when (CopyErrors.IsServer(errors, ex) || ex is InvalidOperationException or BulkDeniedException)
                {
                    skipped.Add($"작업 밖 부모 FK(원본에서 읽지 못함: {CopyRunner.SafeError(ex.Message)})");
                }
            }

            var companions = new CompanionDdl(triggers, triggerObjects, grants, indexes, outer);
            if (made.Count > 0 || skipped.Count > 0)
            {
                var message = $"{key}: 적재가 끝난 뒤 함께 만듭니다: {(made.Count == 0 ? "없음" : string.Join(", ", made))}.";
                if (skipped.Count > 0)
                {
                    message += $" 만들지 않는 것: {string.Join(", ", skipped)}.";
                }

                if (cross && (triggers.Count > 0 || indexes.Count > 0))
                {
                    message += $" {targetDbms} 로 옮기는 트리거 본문·인덱스 식은 대상에서 맞지 않을 수 있습니다: 컴파일 오류 트리거는 남겨 두되 끄고, 만들지 못한 인덱스는 빼고 알립니다.";
                }

                message += " 함께 만들지 못한 것이 있어도 테이블은 실패로 보지 않고 알림에 문장을 남깁니다(만드는 문장은 미리 보기에 있습니다).";
                _items.Add(new CheckItem(key, CopyCheckCodes.CompanionObjects, skipped.Count > 0 ? CheckLevel.Confirm : CheckLevel.Pass, message));
            }

            return companions;
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
            var creatingSequences = _sequencePlans.GetValueOrDefault(Key(rule)) ?? [];
            foreach (var name in referenced.Select(x => x.Object).Distinct(StringComparer.Ordinal)
                         .Where(n => !creatingSequences.Any(p => string.Equals(p.Name, n, StringComparison.Ordinal))))
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
                blocked.Add($"대상 소유자의 다른 테이블이 이미 쓰는 이름 {string.Join(", ", inUse.Distinct(StringComparer.Ordinal))}");
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

        private async Task<(bool CanDisable, IReadOnlyList<string> Triggers, bool None)> CheckTriggersAsync(TableRule rule, CancellationToken ct)
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
                    $"{key}: 대상 계정이 소유자가 아니라 트리거·이 테이블을 참조하는 FK 가 보이지 않을 수 있습니다. 있으면 복사한 행에도 트리거가 실행됩니다."));
            }

            // 「없음」은 소유자로 봤을 때만 믿는다(소유자가 아니면 남의 표 트리거가 안 보일 수 있다).
            return (canDisable, owned, all.Count == 0 && _targetAccess.IsOwner(owner));
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
                $"{Key(rule)}: 자식 테이블 {children} 의 행이 남아 있으면 지울 때 {dialect.ChildRecordFoundError} 가 날 수 있습니다. 대상 처리를 PK 기준 MERGE 로 바꾸세요.")
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
                _items.Add(new CheckItem(key, "SourceNoSelect", CheckLevel.Error, $"{key}: 원본 테이블(또는 부모 키 규칙의 부모 테이블)를 읽을 권한이 없습니다."));
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
                        $"{key}: 부모 테이블 {rule.Parent.ParentTable} 를 이번에 새로 만들어 비어 있어서, 부모 범위로 대상을 지우는 이 규칙은 아무것도 지우지 못합니다. 대상 처리를 바꾸거나 부모의 「없으면 만들기」를 끄세요."));
                }

                var parent = CopyOwners.Parent(job, rule);
                if (parent is not null && parent.Kind != RuleKind.Exclude)
                {
                    continue; // 부모 규칙이 자기 표의 권한을 본다.
                }

                var sourceParent = $"{CopyOwners.ParentSource(job, rule)}.{rule.Parent.ParentTable}";
                var targetParent = $"{CopyOwners.ParentTarget(job, rule)}.{rule.Parent.ParentTable}";
                _items.Add(new CheckItem(key, "ParentExcluded", CheckLevel.Confirm,
                    $"{key}: 부모 테이블 {sourceParent} 가 {(parent is null ? "작업에 없어" : "제외되어 있어")} 부모 전체를 기준으로 연결합니다(대상 쪽은 {targetParent})."));

                // 작업 밖 부모는 다른 규칙이 권한을 보지 않는다: 원본 조회·대상 범위 지우기·건수 비교가 부모 표를 읽으므로 양쪽 SELECT 를 본다.
                var sourceHeld = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, sourceParts.Catalog, sourceParts.Codec, Source, _sourceAccess,
                    CopyOwners.ParentSource(job, rule), rule.Parent.ParentTable, "TABLE", sourceParts.PrecheckDialect.SupportsReadPrivilege, ct).ConfigureAwait(false);
                if (!sourceHeld.Contains("SELECT"))
                {
                    _items.Add(new CheckItem(key, "SourceNoSelect", CheckLevel.Error,
                        $"{key}: 부모 키 규칙이 읽는 원본 부모 테이블 {sourceParent} 에 SELECT 권한이 없습니다."));
                }

                var targetHeld = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, catalog, codec, Target, _targetAccess,
                    CopyOwners.ParentTarget(job, rule), rule.Parent.ParentTable, "TABLE", dialect.SupportsReadPrivilege, ct).ConfigureAwait(false);
                if (!targetHeld.Contains("SELECT"))
                {
                    _items.Add(new CheckItem(key, "TargetNoWrite", CheckLevel.Error,
                        $"{key}: 부모 키 규칙이 대상 범위를 정할 때 읽는 대상 부모 테이블 {targetParent} 에 SELECT 권한이 없습니다."));
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
            _stages = CopyOrder.Stages(names, edges); // 동시 복사(PF2)의 FK 단계: 같은 간선
            foreach (var cycle in sorted.Cycles)
            {
                _items.Add(new CheckItem(null, "FkCycle", CheckLevel.Confirm,
                    $"테이블 {string.Join(", ", cycle)} 가 외래키로 서로 이어져 있어 부모 → 자식 순서를 정할 수 없습니다. 적재 순서에 따라 실패하는 행이 생길 수 있습니다."));
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

        /// <summary>
        /// 작업의 시퀀스 보정 연결을 점검하고, 대상에 없어 만들 시퀀스(SQ3, 결정 84)를 그 표 계획의 <see cref="CopyTablePlan.RuleSequences"/> 에 담은 순서를 돌려준다.
        /// </summary>
        private async Task<IReadOnlyList<CopyTablePlan>> CheckSequencesAsync(IReadOnlyList<CopyTablePlan> plans, IReadOnlyList<CopyTablePlan> order, CancellationToken ct)
        {
            var making = new Dictionary<string, List<CopySequencePlan>>(StringComparer.Ordinal);
            foreach (var rule in job.Sequences)
            {
                var plan = plans.FirstOrDefault(p => string.Equals(Key(p.Rule), rule.Table, StringComparison.Ordinal));
                if (plan is null)
                {
                    continue; // 제외 규칙이거나 점검에 실패한 표. 규칙이 아예 없는 연결은 앞의 RuleInvalid(CopyOwners.Problems)가 막았다.
                }

                progress?.Report($"시퀀스 {rule.Sequence}: 점검 중");
                if (await CheckSequenceAsync(rule, plan, making, ct).ConfigureAwait(false) is { } made)
                {
                    (making.TryGetValue(rule.Table, out var list) ? list : making[rule.Table] = []).Add(made);
                }
            }

            await SuggestSequencesAsync(plans, ct).ConfigureAwait(false);
            return making.Count == 0
                ? order
                : [.. order.Select(p => making.TryGetValue(Key(p.Rule), out var list) ? p with { RuleSequences = list } : p)];
        }

        // NEXTVAL 을 부르지 않는다(부작용, 메모 D16). LAST_NUMBER 와 원본 범위의 최대값으로 어림한다.
        // 대상에 시퀀스가 없으면 만들 계획을 돌려준다(SQ3). 그 밖에는 null.
        private async Task<CopySequencePlan?> CheckSequenceAsync(SequenceRule rule, CopyTablePlan plan, Dictionary<string, List<CopySequencePlan>> making, CancellationToken ct)
        {
            var cannot = new CheckItem(rule.Table, "SequenceGap", CheckLevel.Confirm,
                $"{rule.Table}.{rule.Column}: 시퀀스 {rule.Sequence} 를 볼 수 없어(없거나 SELECT 권한 없음) 보정하지 않습니다. 테스트 중 PK 충돌이 날 수 있습니다.")
            {
                Column = rule.Column,
            };

            var owner = CopyOwners.SequenceOwner(job, rule);
            var info = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.sequence_info"), ct,
                ("OWN", owner), ("SEQ", rule.Sequence)).ConfigureAwait(false);
            if (info.Count == 0)
            {
                // 표를 만들 때 함께 만드는 시퀀스(SQ1)이거나 이 작업의 다른 연결이 이미 만들기로 정한 시퀀스: 만든 뒤 지금처럼 보정한다.
                if (plan.CreateSequences.Any(p => SameSequence(p, owner, rule.Sequence))
                    || making.Values.SelectMany(l => l).Any(p => SameSequence(p, owner, rule.Sequence)))
                {
                    return null;
                }

                if (await PlanRuleSequenceAsync(rule, plan, owner, ct).ConfigureAwait(false) is { } made)
                {
                    return made;
                }

                _items.Add(cannot);
                return null;
            }

            var held = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, catalog, codec, Target, _targetAccess, owner, rule.Sequence, "SEQUENCE", dialect.SupportsReadPrivilege, ct)
                .ConfigureAwait(false);
            if (!held.Contains("SELECT"))
            {
                _items.Add(cannot);
                return null;
            }

            var next = codec.ToInt64(info[0][0]) ?? 0;
            var increment = codec.ToInt64(info[0][1]) ?? 1;
            var count = Build(rule.Table, () => _builder.SourceCount(rule.Table));
            if (count is null || !count.Sql.StartsWith(CountPrefix, StringComparison.Ordinal))
            {
                return null;
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
                return null;
            }

            var gap = PrecheckRules.SequenceGap(next, max, increment);
            if (gap <= 0)
            {
                return null;
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
            return null;
        }

        private static bool SameSequence(CopySequencePlan plan, string owner, string name)
            => CopyOwners.SameOwner(plan.Owner, owner) && string.Equals(plan.Name, name, StringComparison.Ordinal);

        /// <summary>
        /// 시퀀스 보정 연결의 시퀀스가 대상 계정에 없을 때 만들 계획(SQ3, 결정 84). 원본 표 계정에 같은 이름 시퀀스가 있으면 그 속성(START WITH 는 원본 다음 값,
        /// 원본 키 최댓값이 그 이상이면 최댓값 + 증가값 — 적재 뒤 보정이 NEXTVAL 을 덜 쓰게), 없거나 읽지 못하면 기본 속성(START WITH 원본 키 최댓값 + 1, INCREMENT BY 1, 캐시는 DBMS 기본).
        /// 대상이 읽기 전용이거나 CREATE SEQUENCE(다른 계정이면 CREATE ANY SEQUENCE) 권한이 없으면 null(지금처럼 SequenceGap 안내).
        /// 대상 시퀀스 목록에 이미 있으면(보이기만 하고 정보를 못 읽음) 만들지 않는다.
        /// </summary>
        private async Task<CopySequencePlan?> PlanRuleSequenceAsync(SequenceRule rule, CopyTablePlan plan, string owner, CancellationToken ct)
        {
            var canCreate = _targetAccess.IsOwner(owner)
                ? _targetAccess.Has("CREATE SEQUENCE") || _targetAccess.Has("CREATE ANY SEQUENCE")
                : _targetAccess.Has("CREATE ANY SEQUENCE");
            if (Target.Profile.ReadOnly || !canCreate)
            {
                return null;
            }

            if (await ReadSequenceNamesAsync(owner, ct).ConfigureAwait(false) is not { } names || names.Contains(rule.Sequence))
            {
                return null; // 목록을 읽지 못했거나 있다: 만들지 않는다(볼 수 없어 보정하지 않는다는 안내 그대로)
            }

            var sourceOwner = SourceOwner(plan.Rule);
            var max = await SourceKeyMaxAsync(rule, ct).ConfigureAwait(false);
            var fromSource = await ReadSourceSequenceAsync(sourceOwner, rule.Sequence, ct).ConfigureAwait(false);
            SequenceSpec spec;
            string origin;
            if (fromSource is not null)
            {
                spec = fromSource;
                if (max is { } top && CopySequenceDdl.TryNumber(spec.StartWith, out var start) && CopySequenceDdl.TryNumber(spec.Increment, out var step) && step > 0 && top >= start)
                {
                    spec = spec with { StartWith = (top + step).ToString(CultureInfo.InvariantCulture) };
                }

                origin = $"원본 시퀀스 {sourceOwner}.{rule.Sequence} 의 속성(증가·최소·최대·캐시·순환·순서)";
            }
            else
            {
                spec = SequenceSpec.Defaults(((max ?? 0) + 1).ToString(CultureInfo.InvariantCulture));
                origin = $"원본 {sourceOwner} 에도 없어 기본 속성(INCREMENT BY 1, 캐시는 DBMS 기본)";
            }

            var made = new CopySequencePlan(owner, rule.Sequence, spec) { KeyColumns = [rule.Column] };
            _items.Add(new CheckItem(rule.Table, CopyCheckCodes.SequenceCreated, CheckLevel.Confirm,
                $"{rule.Table}.{rule.Column}: 시퀀스 {owner}.{rule.Sequence} 이(가) 대상에 없어 적재 전에 {origin}으로 만들고(START WITH {spec.StartWith}) 복사 뒤 값을 맞춥니다."
                + " 만드는 문장은 미리 보기에 있습니다.")
            {
                Column = rule.Column,
                Sequence = rule.Sequence,
            });
            return made;
        }

        /// <summary>시퀀스 보정 연결 열의 원본 최댓값(작업 범위). 범위를 만들 수 없거나 읽지 못하거나 비었으면 null.</summary>
        private async Task<decimal?> SourceKeyMaxAsync(SequenceRule rule, CancellationToken ct)
        {
            var count = Build(rule.Table, () => _builder.SourceCount(rule.Table));
            if (count is null || !count.Sql.StartsWith(CountPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                var sql = $"SELECT MAX({SqlIdentifier.QuoteAlways(rule.Column)}){count.Sql[CountPrefix.Length..]}";
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, sql, count.Binds, ct).ConfigureAwait(false);
                return sourceParts.Codec.NumberText(rows.FirstOrDefault()?[0]) is { } text && CopySequenceDdl.TryNumber(text, out var value) ? value : null;
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                return null;
            }
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

        // ---- 같은 시점 읽기 + 동시 복사(PF2b) ----

        /// <summary>
        /// 같은 시점 읽기를 켠 채 동시에 복사한다: 시작 시점으로 읽는다는 항목(<see cref="CopyCheckCodes.ReadPoint"/>, 실행기가 이 항목을 보고 동시로 돈다)과,
        /// 다른 계정의 원본 테이블(부모 키 규칙의 부모·조부모 포함)마다 시점 지정 읽기 권한(Oracle FLASHBACK 객체 권한 또는 FLASHBACK ANY TABLE)을 본다.
        /// </summary>
        private async Task CheckReadPointAsync(IReadOnlyList<CopyTablePlan> order, CancellationToken ct)
        {
            if (sourceParts.Dialect.ReadPoint is not { } support)
            {
                // 판정(CopyReadPointDbms)과 원본 방언이 어긋남: 실행하면 시점을 잡지 못해 멈추므로 여기서 막는다.
                _items.Add(new CheckItem(null, CopyCheckCodes.ReadPoint, CheckLevel.Error,
                    $"원본 DBMS({sourceParts.Dbms})는 시점 지정 읽기를 쓰지 않아 같은 시점 읽기로 동시에 복사할 수 없습니다. 동시 테이블 수를 1로 하세요."));
                return;
            }

            _readPoint = support;
            _items.Add(new CheckItem(null, CopyCheckCodes.ReadPoint, CheckLevel.Pass,
                $"복사를 시작할 때 원본 {support.Name} 하나를 잡아 모든 테이블(부모 키 규칙의 부모 포함)을 그 시점({support.Clause})으로 읽습니다. 그 {support.Name} 은 작업 로그와 결과에 남습니다."
                + $" 복사가 오래 걸리면 그동안의 원본 UNDO 가 남아 있어야 합니다(UNDO_RETENTION). 모자라면 {support.SnapshotTooOldErrors ?? "오류"} 로 멈추고 지금 데이터로 바꿔 읽지 않습니다."
                + " 사용자 조건 안의 하위 질의는 지금 데이터를 읽습니다."));

            var seen = new HashSet<(string Owner, string Table)>();
            foreach (var plan in order)
            {
                var key = Key(plan.Rule);
                foreach (var (owner, table) in ReadPointTables(plan.Rule))
                {
                    if (!seen.Add((owner, table)) || _sourceAccess.IsOwner(owner) || _sourceAccess.Has(support.SystemPrivilege))
                    {
                        continue;
                    }

                    var held = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, sourceParts.Catalog, sourceParts.Codec, Source, _sourceAccess, owner, table, "TABLE",
                            sourceParts.PrecheckDialect.SupportsReadPrivilege, ct)
                        .ConfigureAwait(false);
                    if (!held.Contains(support.ObjectPrivilege))
                    {
                        _items.Add(new CheckItem(key, CopyCheckCodes.FlashbackNoPrivilege, CheckLevel.Error,
                            $"{key}: 같은 시점 읽기로 동시에 복사하면 원본 {owner}.{table} 을(를) 시작 {support.Name} 시점으로 읽는데, 이 계정에 그 테이블의 {support.ObjectPrivilege} 권한"
                            + $"({support.SystemPrivilege} 포함)이 없습니다. 권한을 받거나, 동시 테이블 수를 1로 하거나, 같은 시점 읽기를 끄세요."));
                    }
                }
            }
        }

        /// <summary>규칙의 원본 SELECT 가 시점 지정으로 읽는 표: 원본 표, 부모 키 규칙이면 원본 쪽 부모(작업 안의 부모가 또 부모 키 규칙이면 그 부모까지).</summary>
        private List<(string Owner, string Table)> ReadPointTables(TableRule rule)
        {
            var tables = new List<(string Owner, string Table)> { (SourceOwner(rule), rule.Table) };
            var visiting = new HashSet<string>(StringComparer.Ordinal) { Key(rule) };
            var current = rule;
            while (current.Kind == RuleKind.ParentKey && current.Parent is { } link)
            {
                tables.Add((CopyOwners.ParentSource(job, current), link.ParentTable));
                var parentKey = CopyOwners.ParentKey(job, current);
                if (!visiting.Add(parentKey) || CopyOwners.Find(job, parentKey) is not { } parent)
                {
                    break;
                }

                current = parent;
            }

            return tables;
        }

        // ---- 미리 보기 ----

        // 원본 SELECT, 대상 DELETE(자식 → 부모), 다시 만들 표의 DROP(자식 → 부모), 끌 수 있는 트리거·FK, 시퀀스 소비 순서. 정책은 문장을 보낼 세션의 프로필로 판단한다.
        private async Task<IReadOnlyList<PreviewStatement>> BuildPreviewAsync(IReadOnlyList<CopyTablePlan> order, CancellationToken ct)
        {
            var list = new List<PreviewStatement>();
            foreach (var plan in order)
            {
                var key = Key(plan.Rule);
                // 같은 시점 읽기 + 동시(PF2b)면 실행과 같은 글(AS OF 절 + 시점 바인드)로 보인다: 미리 받은 확인은 글을 그대로 비교한다.
                var point = _readPoint is null ? null : CopyReadPoint.Preview(_readPoint);
                if (Build(key, () => _builder.SourceSelect(key, plan.Columns, point)) is { } select)
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

            // 표보다 먼저 만드는 시퀀스(다시 만들기·없으면 만들기, 부모 → 자식, 같은 시퀀스는 한 번), 그리고 쓰는 표를 모두 지우게 되면 함께 지우는 DROP SEQUENCE(위험 명령).
            var sequences = order.Where(p => p.CreateMissing || (IsRecreate(p.Rule) && CanRecreate)).SelectMany(p => p.CreateSequences)
                .DistinctBy(p => (p.Owner, p.Name)).ToList();
            foreach (var sequence in sequences)
            {
                list.Add(await PreviewAsync(Target, CopyCommonStatements.RecreateDdl(sequence.CreateDdl), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            foreach (var sequence in Enumerable.Reverse(sequences))
            {
                list.Add(await PreviewAsync(Target, CopyCommonStatements.DropCreatedSequence(sequence.Owner, sequence.Name), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            // 시퀀스 보정 연결이 쓰는데 대상에 없어 적재 전에 만드는 시퀀스(SQ3). 쓰는 표가 모두 이번에 만드는 표면 그 표를 지울 때 함께 지운다(SQ1 과 같은 DROP SEQUENCE).
            var ruleSequences = order.SelectMany(p => p.RuleSequences.Select(s => (Plan: p, Sequence: s)))
                .Where(x => !sequences.Any(c => SameSequence(c, x.Sequence.Owner, x.Sequence.Name)))
                .GroupBy(x => (x.Sequence.Owner, x.Sequence.Name)).ToList();
            foreach (var group in ruleSequences)
            {
                list.Add(await PreviewAsync(Target, CopyCommonStatements.RecreateDdl(group.First().Sequence.CreateDdl), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            foreach (var group in Enumerable.Reverse(ruleSequences).Where(g => g.All(x => x.Plan.CreateMissing || (IsRecreate(x.Plan.Rule) && CanRecreate))))
            {
                list.Add(await PreviewAsync(Target, CopyCommonStatements.DropCreatedSequence(group.Key.Owner, group.Key.Name), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            // 대상에 없어 만들 표(부모 → 자식): 만드는 문장 전부, 그리고 적재가 실패하면 만든 표를 지우는 DROP … PURGE(자식 → 부모).
            // 지우는 문장은 위험 명령이라 실행 중에 확인을 받을 수 없다: 여기 들어 있어야 화면이 미리 받은 확인으로 넘어간다.
            var created = order.Where(p => p.CreateMissing).ToList();
            foreach (var plan in created)
            {
                var owner = TargetOwner(plan.Rule);
                // 실행기(CopyRunner.CreateMissingTablesAsync)와 같은 함수·같은 입력이라 보여 주는 문장과 보내는 문장이 같다(교차면 대상 DBMS 로 옮긴 구조).
                // 새 표는 PK·UK·인덱스를 적재 뒤에 만든다(PF4a): 그 문장은 CREATE 문장 뒤에 이어서 보인다.
                var built = CrossTableMap.Build(sourceParts.Dbms, targetDbms, plan.Source, owner, new HashSet<string>(StringComparer.Ordinal),
                    name => sourceParts.PrecheckDialect.IsServerName(name, SourceOwner(plan.Rule)), sourceParts.Codec, keysAfterLoad: true);
                foreach (var text in built.Statements.Concat(built.AfterLoad))
                {
                    list.Add(await PreviewAsync(Target, CopyCommonStatements.RecreateDdl(text), PreviewSide.Target, ct).ConfigureAwait(false));
                }
            }

            foreach (var plan in Enumerable.Reverse(created))
            {
                list.Add(await PreviewAsync(Target, statements.DropCreatedTable(TargetOwner(plan.Rule), plan.Rule.Table), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            // 만드는 표에 적재 뒤 함께 만드는 객체(SQ2): 실행기가 같은 글을 보낸다.
            foreach (var plan in order.Where(p => !p.Companions.IsEmpty))
            {
                foreach (var text in plan.Companions.Statements)
                {
                    list.Add(await PreviewAsync(Target, CopyCommonStatements.RecreateDdl(text), PreviewSide.Target, ct).ConfigureAwait(false));
                }
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
