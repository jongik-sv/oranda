using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyRunner
{
    // ---- 시작 ----

    /// <summary>작업 로그 첫 줄: 작업·접속 이름(프로필 이름만, 비밀 없음)·커밋 단위·읽기 방식, 그리고 기간 경계.</summary>
    private static void LogStart(RunState run, string what, ResolvedPeriod? period)
    {
        var job = run.Job;
        var options = job.Options;
        var commit = options.Commit switch
        {
            CommitMode.PerBatch => "배치마다",
            CommitMode.AtEnd => "모두 끝난 뒤 한 번",
            _ => "테이블마다",
        };
        run.Enter(PhaseStart);
        run.Log($"{what}: 작업 「{job.Name}」 · 원본 {job.Source.Name} → 대상 {job.Target.Name} · 테이블 {run.Report.Order.Count.ToString(CultureInfo.InvariantCulture)}개"
                + $" · 커밋 {commit} · 배치 {options.BatchSize.ToString("N0", CultureInfo.InvariantCulture)}행 · 실패 행 상한 {options.MaxFailedRows.ToString("N0", CultureInfo.InvariantCulture)}"
                + (job.ConsistentRead ? " · 같은 시점 기준 읽기" : " · 테이블마다 따로 읽기")
                // 기본값(읽기·쓰기 겹치기, 대기 한도 없음)이면 덧붙이지 않는다(예전 첫 줄 그대로).
                + (options.SerialReadWrite ? " · 읽기·쓰기 번갈아(겹치지 않음)" : string.Empty)
                + (options.WaitLimitMinutes > 0 ? $" · 테이블당 대기 한도 {options.WaitLimitMinutes.ToString(CultureInfo.InvariantCulture)}분" : string.Empty)
                // 동시 복사(PF2)일 때만(한 테이블씩이면 예전 첫 줄 그대로).
                + (run.LaneCount > 1 ? $" · 동시 테이블 {run.LaneCount.ToString(CultureInfo.InvariantCulture)}개" : string.Empty));
        if (period is { } p && (p.TimeFrom is not null || p.TimeTo is not null))
        {
            static string At(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "(열림)";
            run.Log($"기간: {At(p.TimeFrom)} 이상 ~ {At(p.TimeTo)} 미만");
        }
    }

    /// <summary>
    /// 사전 점검 뒤 규칙이 바뀌었으면 실행하지 않는다. 문장(DELETE·SELECT)은 작업에서, MERGE 판정·마스킹·끄기는 보고서에서 만들므로
    /// 둘이 다르면 미리 받은 확인(PreConfirmedSql)이 다른 뜻의 문장에 쓰이거나 제외한 표를 읽을 수 있다.
    /// </summary>
    /// <remarks>
    /// 표마다의 계정(묶음 B)도 본다: 규칙의 원본·대상 계정이 점검한 표의 계정과 같아야 하고, 지금 작업으로 만든 대상 DELETE 가
    /// 점검 미리 보기의 DELETE 와 글자까지 같아야 한다(작업 밖·제외된 부모의 대상 계정이 바뀌어 지우는 범위가 달라지는 것도 막는다).
    /// DELETE 비교는 실행(<paramref name="period"/> 있음)에서만 한다. 「실패 행만 다시 넣기」는 지우지 않는다.
    /// </remarks>
    internal static void EnsureReportMatchesJob(CopyJob job, PrecheckReport report, ResolvedPeriod? period)
    {
        if (CopyOwners.Problems(job) is { Count: > 0 } problems)
        {
            throw new InvalidOperationException($"작업 규칙에 모순이 있습니다: {problems[0]} 사전 점검을 다시 하세요.");
        }

        var active = job.Tables.Where(r => r.Kind != RuleKind.Exclude).ToDictionary(r => CopyOwners.Key(job, r), StringComparer.Ordinal);
        var planned = report.Order.Select(p => CopyOwners.Key(job, p.Rule)).ToHashSet(StringComparer.Ordinal);
        var same = active.Count == planned.Count && planned.Count == report.Order.Count
                   && report.Order.All(p => active.TryGetValue(CopyOwners.Key(job, p.Rule), out var rule) && SameRule(rule, p.Rule));
        if (!same)
        {
            throw new InvalidOperationException("사전 점검 뒤 작업 규칙이 바뀌었습니다. 사전 점검을 다시 하세요.");
        }

        if (report.Order.Any(p => !string.Equals(p.Source.Table.Owner, CopyOwners.Source(job, p.Rule), StringComparison.Ordinal)
                                  || !string.Equals(p.Target.Table.Owner, CopyOwners.Target(job, p.Rule), StringComparison.Ordinal)
                                  || !string.Equals(p.Source.Table.Name, p.Rule.Table, StringComparison.Ordinal)
                                  || !string.Equals(p.Target.Table.Name, p.Rule.Table, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("사전 점검 뒤 원본·대상 소유자가 바뀌었습니다. 사전 점검을 다시 하세요.");
        }

        if (period is null)
        {
            return;
        }

        if (report.Order.Any(p => p.CreateMissing && (!p.Rule.CreateIfMissing || p.Rule.Action == TargetAction.Recreate)))
        {
            throw new InvalidOperationException("사전 점검 뒤 「없으면 만들기」 선택이 바뀌었습니다. 사전 점검을 다시 하세요.");
        }

        var builder = new CopySqlBuilder(job, period,
            report.Order.ToDictionary(p => CopyOwners.Key(job, p.Rule), p => p.Source, StringComparer.Ordinal),
            report.Order.ToDictionary(p => CopyOwners.Key(job, p.Rule), p => p.Target, StringComparer.Ordinal),
            FreshKeys(job, report));
        var previewed = report.Preview.Where(x => x.Side == PreviewSide.Target).Select(x => x.Sql).ToHashSet(StringComparer.Ordinal);
        foreach (var plan in report.Order)
        {
            if (DeleteText(builder, CopyOwners.Key(job, plan.Rule)) is { } text && !previewed.Contains(text))
            {
                throw new InvalidOperationException("사전 점검 뒤 대상 지우기 문장이 바뀌었습니다(계정·부모 테이블). 사전 점검을 다시 하세요.");
            }
        }
    }

    /// <summary>대상 DELETE 글(지우지 않는 처리면 null). 문장을 만들 수 없는 규칙은 사전 점검이 이미 오류로 냈으므로 비교하지 않는다(null).</summary>
    private static string? DeleteText(CopySqlBuilder builder, string key)
    {
        try
        {
            return builder.TargetDelete(key)?.Sql;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>규칙 값 비교(목록은 내용으로 비교한다. record 의 기본 비교는 목록을 참조로 비교한다). 표마다의 계정도 비교한다.</summary>
    internal static bool SameRule(TableRule a, TableRule b) =>
        a.Table == b.Table && a.Kind == b.Kind && a.Action == b.Action && a.CreateIfMissing == b.CreateIfMissing
        && string.Equals(a.SourceOwner, b.SourceOwner, StringComparison.Ordinal)
        && string.Equals(a.TargetOwner, b.TargetOwner, StringComparison.Ordinal)
        && string.Equals(a.CustomWhere, b.CustomWhere, StringComparison.Ordinal)
        && string.Equals(a.ExtraWhere, b.ExtraWhere, StringComparison.Ordinal)
        && a.PeriodColumns.SequenceEqual(b.PeriodColumns)
        && a.Masks.SequenceEqual(b.Masks)
        && a.SkipColumns.SequenceEqual(b.SkipColumns, StringComparer.Ordinal)
        && (a.AddColumns ?? []).SequenceEqual(b.AddColumns ?? [], StringComparer.Ordinal)
        && (a.Parent is null) == (b.Parent is null)
        && (a.Parent is null || (a.Parent.ParentTable == b.Parent!.ParentTable
                                 && a.Parent.ChildColumns.SequenceEqual(b.Parent.ChildColumns, StringComparer.Ordinal)
                                 && a.Parent.ParentColumns.SequenceEqual(b.Parent.ParentColumns, StringComparer.Ordinal)
                                 && a.Parent.ForeignKeyName == b.Parent.ForeignKeyName
                                 && string.Equals(a.Parent.ParentOwner, b.Parent.ParentOwner, StringComparison.Ordinal)));

    /// <summary>
    /// 원본과 대상이 같은 DB·같은 소유자면 거부한다. 다른 프로필 둘이 같은 곳을 가리키면 대상 DELETE·커밋이 원본 행을 지운다.
    /// <see cref="ISession.DbKey"/> 는 <c>{DB_UNIQUE_NAME}/{CON_NAME}/{SESSION_USER}</c> 다(앞 두 마디가 같으면 같은 DB).
    /// </summary>
    /// <remarks>
    /// 이름만으로는 복제 DB·기본 이름 컨테이너(FREE/FREEPDB1 등)를 같은 DB 로 오판한다. 그래서 호스트·인스턴스 이름도 본다.
    /// RAC 는 노드마다 호스트·인스턴스가 달라 DB 이름만 본다. 호스트·인스턴스를 모르면 같다고 본다(막는 쪽).
    /// </remarks>
    internal static bool IsSameDatabaseAndOwner(string sourceDbKey, ServerInfo source, string targetDbKey, ServerInfo target,
                                                string sourceOwner, string targetOwner)
        => CopyOwners.SameOwner(sourceOwner, targetOwner)
           && IsSameDatabase(sourceDbKey, source, targetDbKey, target);

    /// <summary>원본과 대상이 같은 DB 인가(<see cref="IsSameDatabaseAndOwner"/> 의 DB 판정). 모르면 같다고 본다(막는 쪽).</summary>
    internal static bool IsSameDatabase(string sourceDbKey, ServerInfo source, string targetDbKey, ServerInfo target)
        => CopySameDatabase.IsSameDatabase(sourceDbKey, source, targetDbKey, target);

    /// <summary>
    /// 같은 DB 일 때 대상 정리가 원본 행을 지우는 규칙(규칙 사이 비교, <see cref="CopyOwners.TargetsReadAsSource"/>). 다른 DB 면 빈 목록.
    /// 사전 점검과 실행기가 같은 판정(<see cref="CopySameDatabase.SameSourceTargets"/>)을 쓴다.
    /// </summary>
    internal static IReadOnlyList<(string Key, string Table)> SameSourceTargets(CopySessions sessions, CopyJob job)
        => CopySameDatabase.SameSourceTargets(sessions, job);

    /// <summary>DROP·CREATE 는 개발 라벨 대상에서만 실행한다(사전 점검과 따로 한 번 더 막는다, Global Constraints).</summary>
    private void EnsureRecreateAllowed(CopySessions sessions, CopyJob job)
    {
        var recreate = job.Tables.Where(r => r.Kind != RuleKind.Exclude && r.Action == TargetAction.Recreate).ToList();
        if (recreate.Count > 0 && !CopyTargetGuard.CanRecreate(sessions.Target.Profile))
        {
            throw new InvalidOperationException($"대상 접속 '{sessions.Target.Profile.Name}' 은 개발 접속이 아니라 「테이블 다시 만들기」를 실행할 수 없습니다.");
        }

        // 대상 DBMS 가 다시 만들기를 막으면 오류 없는 점검 결과를 받았어도 DROP 전에 멈춘다(지금 막는 DBMS 는 없다 — Tibero 는 TR1 에서 풀었다).
        if (recreate.Count > 0 && _targetPrecheck.RecreateBlockedReason(sessions.Target) is { } blocked)
        {
            throw new InvalidOperationException($"대상 DBMS({_targetDbms}) 에서는 「테이블 다시 만들기」를 실행할 수 없습니다: {blocked}");
        }

        // 다시 만들기는 대상 소유자 계정으로만 한다(표마다, 사전 점검 RecreateNoPrivilege 와 같은 판정). DbKey 의 끝 마디가 SESSION_USER 다.
        var user = SessionUser(sessions.Target.DbKey);
        if (user is not null && recreate.FirstOrDefault(r => !CopyOwners.SameOwner(CopyOwners.Target(job, r), user)) is { } other)
        {
            throw new InvalidOperationException($"{CopyOwners.Key(job, other)}: 「테이블 다시 만들기」는 대상 소유자({CopyOwners.Target(job, other)}) 계정으로 접속해야 합니다(지금 {user}).");
        }
    }

    /// <summary>
    /// 「없으면 만들기」 표(<see cref="CopyTablePlan.CreateMissing"/>)도 개발 접속·대상 소유자 계정으로만 만든다(사전 점검과 따로 한 번 더 막는다).
    /// </summary>
    private void EnsureCreateAllowed(CopySessions sessions, CopyJob job, PrecheckReport report)
    {
        var creating = report.Order.Where(p => p.CreateMissing).ToList();
        if (creating.Count == 0)
        {
            return;
        }

        if (!CopyTargetGuard.CanRecreate(sessions.Target.Profile))
        {
            throw new InvalidOperationException($"대상 접속 '{sessions.Target.Profile.Name}' 은 개발 접속이 아니라 「없으면 만들기」로 테이블을 만들 수 없습니다.");
        }

        // 대상 DBMS 가 표 만들기를 막으면 오류 없는 점검 결과를 받았어도 CREATE 전에 멈춘다.
        if (_targetPrecheck.CreateBlockedReason(sessions.Target) is { } blocked)
        {
            throw new InvalidOperationException($"대상 DBMS({_targetDbms}) 에서는 「없으면 만들기」로 테이블을 만들 수 없습니다: {blocked}");
        }

        var user = SessionUser(sessions.Target.DbKey);
        if (user is not null && creating.FirstOrDefault(p => !CopyOwners.SameOwner(CopyOwners.Target(job, p.Rule), user)) is { } other)
        {
            throw new InvalidOperationException($"{CopyOwners.Key(job, other.Rule)}: 「없으면 만들기」는 대상 소유자({CopyOwners.Target(job, other.Rule)}) 계정으로 접속해야 합니다(지금 {user}).");
        }
    }

    /// <summary>이번 실행이 만드는 표(「없으면 만들기」)의 규칙 키. 새 표라 대상 DELETE 를 만들지 않는다(<see cref="CopySqlBuilder"/>).</summary>
    private static HashSet<string> FreshKeys(CopyJob job, PrecheckReport report)
        => report.Order.Where(p => p.CreateMissing).Select(p => CopyOwners.Key(job, p.Rule)).ToHashSet(StringComparer.Ordinal);

    /// <summary><c>{DB_UNIQUE_NAME}/{CON_NAME}/{SESSION_USER}</c> 의 끝 마디. 모양이 다르면 null.</summary>
    private static string? SessionUser(string dbKey)
    {
        var last = dbKey.LastIndexOf('/');
        return last < 0 || last == dbKey.Length - 1 ? null : dbKey[(last + 1)..];
    }

    /// <summary>같은 DB 에서 어떤 규칙의 대상 표가 어떤 규칙이 읽는 원본 표면 거부한다(사전 점검과 따로 한 번 더, 표 단위·규칙 사이).</summary>
    private static void EnsureDistinct(CopySessions sessions, CopyJob job)
    {
        if (CopyOwners.Problems(job) is { Count: > 0 } problems)
        {
            throw new InvalidOperationException(problems[0]);
        }

        if (SameSourceTargets(sessions, job) is { Count: > 0 } same)
        {
            throw new InvalidOperationException($"원본과 대상이 같은 DB 이고 대상 테이블 {same[0].Table} 을(를) 원본으로도 읽습니다. 원본 행을 지울 수 있어 실행하지 않습니다.");
        }
    }

    private void EnsureSessions(CopySessions sessions)
    {
        // 부품 벌(방언·코덱)은 자기 DBMS 의 것이다: 세션 DBMS 와 다르면 다른 DBMS 의 문장·값 코덱으로 실행하게 된다.
        if (sessions.Source.Profile.Dbms != _sourceDbms)
        {
            throw new InvalidOperationException($"원본 세션의 DBMS({sessions.Source.Profile.Dbms})가 원본 복사 부품의 DBMS({_sourceDbms})와 다릅니다. 세션에 맞는 복사 실행기를 쓰세요.");
        }

        if (sessions.Target.Profile.Dbms != _targetDbms)
        {
            throw new InvalidOperationException($"대상 세션의 DBMS({sessions.Target.Profile.Dbms})가 대상 복사 부품의 DBMS({_targetDbms})와 다릅니다. 세션에 맞는 복사 실행기를 쓰세요.");
        }

        if (sessions.Target.HasPendingTransaction)
        {
            // 첫 DDL(끄기)이나 COMMIT 이 남의 미커밋 작업을 확정하지 않게 한다. 복사는 전용 세션을 새로 연다(설계 8.1).
            throw new InvalidOperationException("대상 세션에 커밋하지 않은 작업이 있습니다. 복사용 전용 세션을 새로 여세요.");
        }

        CopyTargetGuard.EnsureAllowedTarget(sessions.Target.Profile);
        if (!sessions.Source.Profile.ReadOnly)
        {
            throw new InvalidOperationException("원본 세션은 읽기 전용 프로필(profile with { ReadOnly = true })로 열어야 합니다.");
        }
    }

    /// <summary>원본 앞 트랜잭션을 닫고 읽기 전용 트랜잭션을 연다. 그 시각이 <c>ReadStartedAt</c> 이다.</summary>
    private async Task OpenSourceAsync(RunState run, CancellationToken ct)
    {
        run.Enter(PhaseSource);
        var rollback = await ControlLoggedAsync(run, run.Source, CopyCommonStatements.Rollback(), ct, "앞 트랜잭션 닫기");
        if (rollback.Status != ExecutionStatus.Succeeded)
        {
            run.Abort("원본 세션의 트랜잭션을 정리하지 못했습니다: " + Describe(rollback));
            return;
        }

        // 「같은 시점 기준 읽기」를 꺼도 원본에는 DB 수준 READ ONLY 를 건다(그때는 표마다 새로 연다, BeginTableReadAsync).
        run.ReadStartedAt = DateTimeOffset.Now;
        // 같은 시점 읽기 + 동시 복사(PF2b)면 시점은 이 트랜잭션이 아니라 곧 잡을 원본 SCN 이다(CaptureReadPointAsync).
        var set = await ControlLoggedAsync(run, run.Source, _sourceDialect.SetTransactionReadOnly(), ct,
            UsesReadPoint(run) ? "읽기 전용(시점은 아래에서 잡는 원본 SCN)"
            : run.Job.ConsistentRead ? "모든 테이블을 이 시점 기준으로 읽기" : "읽기 전용(테이블마다 새로 엽니다)");
        if (set.Status != ExecutionStatus.Succeeded)
        {
            run.Abort("원본에서 읽기 전용 트랜잭션을 시작하지 못했습니다: " + Describe(set));
        }
    }
}
