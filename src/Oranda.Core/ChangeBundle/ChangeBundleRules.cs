using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;

namespace Oranda.Core.ChangeBundle;

/// <summary>자동 넣기 판정(설계 9.3).</summary>
public enum ChangeBundleDecision
{
    /// <summary>「지금 묶음」에 넣는다(같은 해시가 있으면 실행 표시만).</summary>
    Add,

    /// <summary>접속 DBMS 가 묶음 DBMS 와 다르다 — 넣지 않고 메시지 창에 한 줄 알린다.</summary>
    DbmsMismatch,

    /// <summary>대상 소유자가 묶음의 대상 소유자 목록에 없다 — 알리지 않고 넣지 않는다(TE-24).</summary>
    OwnerNotTarget,
}

/// <summary>
/// 반영 묶음 자동 넣기 조건(설계 9.3)의 순수 판정. 기록기(<c>ChangeBundleRecorder</c>)가 <c>StatementExecuted</c> 마다 부른다.
/// </summary>
public static class ChangeBundleRules
{
    /// <summary>
    /// 묶음에 넣을 후보인가: DDL 이고 성공했고, 출처가 SQL 편집기·스크립트·테이블 편집기다(TE-16). 묶음 실행·열 순서 바꾸기(스키마 브라우저)·
    /// 데이터 복사·에이전트·모니터는 넣지 않는다. 이력을 남기지 않는 내부 실행(<see cref="ExecutionOptions.SkipHistory"/>)과
    /// 객체를 바꾸지 않는 <c>ALTER SESSION</c>·<c>ALTER SYSTEM</c> 도 넣지 않는다.
    /// </summary>
    public static bool IsCandidate(SqlStatement statement, ExecutionResult result, ExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        return statement.Kind == StatementKind.Ddl
            && result.Status == ExecutionStatus.Succeeded
            && !options.SkipHistory
            && options.Source is HistorySource.Editor or HistorySource.Script or HistorySource.TableEditor
            && statement.Verb is not ("ALTER SESSION" or "ALTER SYSTEM");
    }

    /// <summary>후보 문장을 이 묶음에 넣을지. 빈 묶음(<see cref="ChangeBundleInfo.Dbms"/> = null)은 어떤 DBMS 든 받는다.</summary>
    /// <param name="targetOwner">문장이 바꾸는 객체의 소유자(소유자를 안 쓴 문장은 세션의 현재 스키마). 대문자 규칙 이름.</param>
    public static ChangeBundleDecision Decide(ChangeBundleInfo bundle, Dbms sessionDbms, string? targetOwner)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.Dbms is { } dbms && dbms != sessionDbms)
        {
            return ChangeBundleDecision.DbmsMismatch;
        }

        if (bundle.TargetOwners.Count > 0 && (targetOwner is null || !bundle.TargetOwners.Contains(targetOwner, StringComparer.Ordinal)))
        {
            return ChangeBundleDecision.OwnerNotTarget;
        }

        return ChangeBundleDecision.Add;
    }

    /// <summary>출처(<see cref="ExecutionOptions.Source"/>) → 묶음 문장 출처.</summary>
    public static ChangeBundleItemSource SourceOf(HistorySource source) =>
        source == HistorySource.TableEditor ? ChangeBundleItemSource.TableEditor : ChangeBundleItemSource.SqlEditor;
}
