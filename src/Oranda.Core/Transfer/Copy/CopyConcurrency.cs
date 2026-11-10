using System.Globalization;
using Oranda.Core.Connection;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 동시 테이블 수 판정(PF2, 조정 결정 71·72): 사전 점검·실행기·화면이 같이 쓴다. <paramref name="Tables"/> = 실제로 동시에 복사할 테이블 수(1 = 한 테이블씩),
/// <paramref name="Reason"/> = 사용자가 2 이상을 골랐는데 1 로 도는 이유(화면·사전 점검에 그대로 보인다). 2 이상이 그대로면 null.
/// </summary>
public readonly record struct CopyConcurrencyDecision(int Tables, string? Reason)
{
    /// <summary>
    /// 같은 시점 읽기를 켠 채 동시에 복사한다(PF2b): 실행을 시작할 때 원본 시점(SCN) 하나를 잡아 모든 테이블을 그 시점으로 읽는다(<see cref="CopyReadPoint"/>).
    /// <see cref="Tables"/> 가 2 이상이고 같은 시점 읽기를 켰을 때만 참.
    /// </summary>
    public bool ReadsAtPoint { get; init; }
}

/// <summary>
/// 동시 테이블 수(<see cref="CopyOptions.ConcurrentTables"/>)를 실제로 쓸 수 있는지 정한다.
/// <list type="bullet">
/// <item>「같은 시점 읽기」(<see cref="CopyJob.ConsistentRead"/>) 켬: 원본 DBMS 가 시점 지정 읽기를 쓰면(<see cref="CopyReadPointDbms.Supports"/>, Oracle <c>AS OF SCN</c>)
/// 동시로 돌고 모든 레인이 시작 SCN 하나로 읽는다(PF2b, <see cref="CopyConcurrencyDecision.ReadsAtPoint"/>). 쓰지 않으면(Tibero) 동시 끔 — 세션이 여럿이면 한 시점으로 읽을 수 없다.</item>
/// <item>「모두 끝난 뒤 한 번 커밋」 = 동시 끔: 세션마다 트랜잭션이 따로라 한꺼번에 되돌릴 수 없다(2PC 없음).</item>
/// <item>「실패 행만 다시 넣기」 = 늘 한 테이블씩.</item>
/// <item>점검 결과가 있으면 FK 단계(<see cref="PrecheckReport.Stages"/>) 안 테이블 수가 상한이다(모든 단계가 한 테이블이면 동시 끔).</item>
/// </list>
/// </summary>
public static class CopyConcurrency
{
    /// <summary>화면에서 고를 수 있는 가장 큰 동시 테이블 수.</summary>
    public const int Max = 4;

    /// <summary>같은 시점 읽기·「모두 끝난 뒤 한 번 커밋」 둘 다 켰을 때의 이유(시점 지정 읽기를 쓰지 않는 원본).</summary>
    public const string ConsistentReadAndAtEndReason = "같은 시점 읽기를 끄고 커밋을 「테이블마다」나 「배치마다」로 바꾸면 동시 복사가 켜집니다.";

    /// <summary>같은 시점 읽기를 켰을 때의 이유(시점 지정 읽기를 쓰지 않는 원본 — Tibero).</summary>
    public const string ConsistentReadReason = "같은 시점 읽기를 끄면 동시 복사가 켜집니다.";

    /// <summary>「모두 끝난 뒤 한 번 커밋」일 때의 이유.</summary>
    public const string AtEndReason = "커밋을 「테이블마다」나 「배치마다」로 바꾸면 동시 복사가 켜집니다(「모두 끝난 뒤 한 번」은 한꺼번에 되돌려야 해서 한 테이블씩 복사합니다).";

    /// <summary>「실패 행만 다시 넣기」의 이유.</summary>
    public const string RetryReason = "「실패 행만 다시 넣기」는 한 테이블씩 넣습니다.";

    /// <summary>점검 결과에 FK 단계가 없을 때의 이유.</summary>
    public const string NoStagesReason = "사전 점검 결과에 FK 단계가 없어 한 테이블씩 복사합니다. 사전 점검을 다시 하세요.";

    /// <summary>모든 FK 단계가 한 테이블일 때의 이유.</summary>
    public const string NoParallelStageReason = "FK 순서상 함께 복사할 수 있는 테이블이 없어 한 테이블씩 복사합니다.";

    /// <summary>사전 점검 확인 항목의 첫 문장(동시로 돌 때).</summary>
    public const string DeleteCommitWarning = "동시 복사는 지우기를 먼저 확정해서 중지해도 지운 행이 돌아오지 않습니다.";

    /// <summary>같은 시점 읽기를 켠 채 동시로 돌 때의 안내(화면·사전 점검 확인 항목, PF2b).</summary>
    public const string ReadPointNote =
        "같은 시점 읽기: 복사를 시작할 때 원본 SCN 하나를 잡아 모든 테이블을 그 시점(AS OF SCN)으로 읽습니다. 다른 계정의 테이블은 FLASHBACK 권한이 필요하고, 복사가 오래 걸리면 원본 UNDO 가 그동안 남아 있어야 합니다.";

    /// <summary>점검 뒤 같은 시점 읽기를 켜 시점 읽기 점검(<see cref="CopyCheckCodes.ReadPoint"/>)이 없을 때의 이유(실행기).</summary>
    public const string ReadPointUncheckedReason = "사전 점검 뒤 같은 시점 읽기 설정이 바뀌어 한 테이블씩 복사합니다. 동시에 복사하려면 사전 점검을 다시 하세요.";

    /// <summary>점검 뒤 동시 수를 바꿔 확인 항목이 없을 때의 이유(실행기).</summary>
    public const string UnconfirmedReason = "사전 점검 뒤 동시 복사 설정이 바뀌어 한 테이블씩 복사합니다. 동시에 복사하려면 사전 점검을 다시 하세요.";

    /// <summary>고른 동시 테이블 수(1~<see cref="Max"/>, 없거나 1 아래면 1).</summary>
    public static int Requested(CopyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Math.Clamp(options.ConcurrentTables ?? 1, 1, Max);
    }

    /// <summary>
    /// 실제 동시 테이블 수와 그 이유. <paramref name="report"/> 가 null 이면(화면: 점검 전) 작업 설정만 본다.
    /// <paramref name="source"/> = 원본 DBMS: 같은 시점 읽기를 켰을 때 시점 지정 읽기(<see cref="CopyReadPointDbms.Supports"/>)를 쓰면 동시로 돈다(PF2b).
    /// </summary>
    public static CopyConcurrencyDecision Effective(CopyJob job, PrecheckReport? report, Dbms source, bool retry = false)
    {
        ArgumentNullException.ThrowIfNull(job);
        var requested = Requested(job.Options);
        if (requested <= 1)
        {
            return new CopyConcurrencyDecision(1, null);
        }

        if (retry)
        {
            return new CopyConcurrencyDecision(1, RetryReason);
        }

        var atEnd = job.Options.Commit == CommitMode.AtEnd;
        var consistentBlocks = job.ConsistentRead && !CopyReadPointDbms.Supports(source);
        if (consistentBlocks || atEnd)
        {
            return new CopyConcurrencyDecision(1, consistentBlocks && atEnd ? ConsistentReadAndAtEndReason : consistentBlocks ? ConsistentReadReason : AtEndReason);
        }

        var decision = report is null ? new CopyConcurrencyDecision(requested, null) : ByStages(requested, report.Stages);
        return WithReadPoint(job, decision);
    }

    /// <summary>점검이 정한 FK 단계(<paramref name="stages"/>)로 본 판정(사전 점검이 점검 결과를 만들기 전). <see cref="Effective"/> 와 같은 규칙이다.</summary>
    public static CopyConcurrencyDecision Planned(CopyJob job, IReadOnlyList<IReadOnlyList<string>> stages, Dbms source)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(stages);
        var decision = Effective(job, report: null, source);
        return decision.Tables > 1 ? WithReadPoint(job, ByStages(Requested(job.Options), stages)) : decision;
    }

    /// <summary>
    /// 사전 점검 항목(작업 전체 한 항목, 코드 <see cref="CopyCheckCodes.ConcurrentCopy"/>). 1 을 골랐으면 null.
    /// 실제로 동시에 돌면 확인 필요(지우기를 먼저 확정한다, 같은 시점 읽기면 시작 SCN 으로 읽는다), 한 테이블씩 돌면 통과 안내(이유).
    /// </summary>
    public static CheckItem? Notice(CopyJob job, IReadOnlyList<IReadOnlyList<string>> stages, Dbms source)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(stages);
        var requested = Requested(job.Options);
        if (requested <= 1)
        {
            return null;
        }

        var decision = Planned(job, stages, source);
        if (decision.Tables > 1)
        {
            var n = decision.Tables.ToString(CultureInfo.InvariantCulture);
            return new CheckItem(null, CopyCheckCodes.ConcurrentCopy, CheckLevel.Confirm,
                $"테이블을 {n}개까지 동시에 복사합니다(FK 단계마다, 부모 단계가 모두 끝난 뒤 자식 단계). {DeleteCommitWarning}"
                + $" 원본에서 테이블 {n}개를 동시에 읽어 부하가 커지고, 끄지 않은 트리거가 같은 작업의 다른 테이블에 쓰면 서로 기다릴 수 있습니다."
                + (decision.ReadsAtPoint ? " " + ReadPointNote : string.Empty));
        }

        return new CheckItem(null, CopyCheckCodes.ConcurrentCopy, CheckLevel.Pass,
            $"동시 테이블 수를 {requested.ToString(CultureInfo.InvariantCulture)}개로 골랐지만 한 테이블씩 복사합니다: {decision.Reason}");
    }

    /// <summary>
    /// 실행기가 쓰는 레인 수(앱도 이 판정으로 레인 세션을 연다): 판정이 2 이상이어도 점검 결과에 확인 항목(<see cref="Notice"/>)이 없으면(점검 뒤 설정을 바꿈) 1 이다 —
    /// 사용자가 확인하지 않은 동작 변경을 하지 않는다. 같은 시점 읽기로 동시에 도는데 점검 결과에 시점 읽기 점검(<see cref="CopyCheckCodes.ReadPoint"/>,
    /// FLASHBACK 권한을 본 것)이 없어도 1 이다.
    /// </summary>
    public static CopyConcurrencyDecision ForRun(CopyJob job, PrecheckReport report, Dbms source)
    {
        ArgumentNullException.ThrowIfNull(report);
        var decision = Effective(job, report, source);
        if (decision.Tables > 1 && !report.Items.Any(i => i.Code == CopyCheckCodes.ConcurrentCopy && i.Level == CheckLevel.Confirm))
        {
            return new CopyConcurrencyDecision(1, UnconfirmedReason);
        }

        if (decision.ReadsAtPoint && !report.Items.Any(i => i.Code == CopyCheckCodes.ReadPoint))
        {
            return new CopyConcurrencyDecision(1, ReadPointUncheckedReason);
        }

        return decision;
    }

    private static CopyConcurrencyDecision WithReadPoint(CopyJob job, CopyConcurrencyDecision decision) =>
        decision with { ReadsAtPoint = decision.Tables > 1 && job.ConsistentRead };

    private static CopyConcurrencyDecision ByStages(int requested, IReadOnlyList<IReadOnlyList<string>>? stages)
    {
        if (stages is null)
        {
            return new CopyConcurrencyDecision(1, NoStagesReason);
        }

        var widest = stages.Count == 0 ? 0 : stages.Max(s => s.Count);
        return widest < 2 ? new CopyConcurrencyDecision(1, NoParallelStageReason) : new CopyConcurrencyDecision(Math.Min(requested, widest), null);
    }
}
