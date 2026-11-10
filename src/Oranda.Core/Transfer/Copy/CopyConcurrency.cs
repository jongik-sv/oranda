using System.Globalization;
using Oranda.Core.Connection;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 동시 테이블 수 판정(PF2, 조정 결정 71·72): 사전 점검·실행기·화면이 같이 쓴다. <paramref name="Tables"/> = 실제로 동시에 복사할 테이블 수(1 = 한 테이블씩),
/// <paramref name="Reason"/> = 사용자가 2 이상을 골랐는데 1 로 도는 이유(화면·사전 점검에 그대로 보인다). 2 이상이 그대로면 null.
/// </summary>
public readonly record struct CopyConcurrencyDecision(int Tables, string? Reason);

/// <summary>
/// 동시 테이블 수(<see cref="CopyOptions.ConcurrentTables"/>)를 실제로 쓸 수 있는지 정한다.
/// <list type="bullet">
/// <item>「같은 시점 읽기」(<see cref="CopyJob.ConsistentRead"/>) 켬 = 동시 끔: 세션이 여럿이면 한 시점으로 읽을 수 없다(AS OF SCN 은 쓰지 않는다, 설계 8.1 의 사용자 결정).</item>
/// <item>「모두 끝난 뒤 한 번 커밋」 = 동시 끔: 세션마다 트랜잭션이 따로라 한꺼번에 되돌릴 수 없다(2PC 없음).</item>
/// <item>「실패 행만 다시 넣기」 = 늘 한 테이블씩.</item>
/// <item>점검 결과가 있으면 FK 단계(<see cref="PrecheckReport.Stages"/>) 안 테이블 수가 상한이다(모든 단계가 한 테이블이면 동시 끔).</item>
/// </list>
/// </summary>
public static class CopyConcurrency
{
    /// <summary>화면에서 고를 수 있는 가장 큰 동시 테이블 수.</summary>
    public const int Max = 4;

    /// <summary>같은 시점 읽기·「모두 끝난 뒤 한 번 커밋」 둘 다 켰을 때의 이유.</summary>
    public const string ConsistentReadAndAtEndReason = "같은 시점 읽기를 끄고 커밋을 「테이블마다」나 「배치마다」로 바꾸면 동시 복사가 켜집니다.";

    /// <summary>같은 시점 읽기를 켰을 때의 이유.</summary>
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

    /// <summary>고른 동시 테이블 수(1~<see cref="Max"/>, 없거나 1 아래면 1).</summary>
    public static int Requested(CopyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Math.Clamp(options.ConcurrentTables ?? 1, 1, Max);
    }

    /// <summary>
    /// 실제 동시 테이블 수와 그 이유. <paramref name="report"/> 가 null 이면(화면: 점검 전) 작업 설정만 본다.
    /// <paramref name="source"/> 는 원본 DBMS 다: 지금은 판정에 쓰지 않는다(같은 시점 읽기를 SCN 으로 맞추는 PF2b 가 DBMS 마다 다를 때의 자리).
    /// </summary>
    public static CopyConcurrencyDecision Effective(CopyJob job, PrecheckReport? report, Dbms source, bool retry = false)
    {
        ArgumentNullException.ThrowIfNull(job);
        _ = source; // PF2b(AS OF SCN) 자리. 지금은 DBMS 와 상관없이 같은 판정이다.
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
        if (job.ConsistentRead || atEnd)
        {
            return new CopyConcurrencyDecision(1, job.ConsistentRead && atEnd ? ConsistentReadAndAtEndReason : job.ConsistentRead ? ConsistentReadReason : AtEndReason);
        }

        if (report is null)
        {
            return new CopyConcurrencyDecision(requested, null);
        }

        return ByStages(requested, report.Stages);
    }

    /// <summary>
    /// 사전 점검 항목(작업 전체 한 항목, 코드 <see cref="CopyCheckCodes.ConcurrentCopy"/>). 1 을 골랐으면 null.
    /// 실제로 동시에 돌면 확인 필요(지우기를 먼저 확정한다), 한 테이블씩 돌면 통과 안내(이유).
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

        var decision = Effective(job, report: null, source);
        if (decision.Tables > 1)
        {
            decision = ByStages(requested, stages);
        }

        if (decision.Tables > 1)
        {
            var n = decision.Tables.ToString(CultureInfo.InvariantCulture);
            return new CheckItem(null, CopyCheckCodes.ConcurrentCopy, CheckLevel.Confirm,
                $"테이블을 {n}개까지 동시에 복사합니다(FK 단계마다, 부모 단계가 모두 끝난 뒤 자식 단계). {DeleteCommitWarning}"
                + $" 원본에서 테이블 {n}개를 동시에 읽어 부하가 커지고, 끄지 않은 트리거가 같은 작업의 다른 테이블에 쓰면 서로 기다릴 수 있습니다.");
        }

        return new CheckItem(null, CopyCheckCodes.ConcurrentCopy, CheckLevel.Pass,
            $"동시 테이블 수를 {requested.ToString(CultureInfo.InvariantCulture)}개로 골랐지만 한 테이블씩 복사합니다: {decision.Reason}");
    }

    /// <summary>
    /// 실행기가 쓰는 레인 수: 판정이 2 이상이어도 점검 결과에 확인 항목(<see cref="Notice"/>)이 없으면(점검 뒤 설정을 바꿈) 1 이다 — 사용자가 확인하지 않은 동작 변경을 하지 않는다.
    /// </summary>
    internal static CopyConcurrencyDecision ForRun(CopyJob job, PrecheckReport report, Dbms source)
    {
        var decision = Effective(job, report, source);
        if (decision.Tables > 1 && !report.Items.Any(i => i.Code == CopyCheckCodes.ConcurrentCopy && i.Level == CheckLevel.Confirm))
        {
            return new CopyConcurrencyDecision(1, "사전 점검 뒤 동시 복사 설정이 바뀌어 한 테이블씩 복사합니다. 동시에 복사하려면 사전 점검을 다시 하세요.");
        }

        return decision;
    }

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
