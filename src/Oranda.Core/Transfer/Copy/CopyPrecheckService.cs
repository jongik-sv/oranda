using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

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
        var report = await new Run(bulk, metadata, policy, source, target.Dbms, target.Catalog, CopyErrors.Combine(target.Errors, source.Errors), target.PrecheckDialect,
                             target.Dialect, target.Codec, sessions, job, period, progress)
            .ExecuteAsync(ct).ConfigureAwait(false);

        // 항목마다 그 자리에서 고칠 처리(PC1, 순수 함수 — DB 를 부르지 않는다).
        return CopyPrecheckFixes.Attach(job, report,
            new CopyFixContext(source.Dbms, target.Dbms, CopyTargetGuard.CanRecreate(sessions.Target.Profile)) { SourceCodec = source.Codec });
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
            order = WithAddColumnPlans(order); // 「대상에 열 추가」(PC1)
            var preview = await BuildPreviewAsync(order, ct).ConfigureAwait(false);
            var addColumns = await AddColumnPreviewAsync(order, ct).ConfigureAwait(false);
            return new PrecheckReport(_items, order, [.. addColumns, .. preview, .. ExternalLoadPreview(order)], _estimated) { Stages = _stages, ExternalTool = _externalTool };
        }
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();
}
