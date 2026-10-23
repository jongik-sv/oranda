namespace Oranda.App.Features.MyBatisWorkspace.Hooks;

/// <summary>배지 종류(색은 테마 리소스 키로 그린다).</summary>
public enum MyBatisBadgeKind
{
    Info,
    Warning,
    Error,
}

/// <summary>「문장」 창 항목 옆 배지 하나(예 「조합 오류 2」).</summary>
public sealed record MyBatisStatementBadge(string Text, MyBatisBadgeKind Kind, string? ToolTip = null);

/// <summary>
/// 「문장」 창 항목 배지(R5, design.md 9장). 계약(TSK-01-02) — 구현은 03-04(<c>Combos/StatementBadge*</c>).
/// 부르는 곳: 03-01 「문장」 창이 항목을 그릴 때. 03-01 파일은 고치지 않는다.
/// </summary>
public interface IMyBatisStatementBadgeProvider
{
    IReadOnlyList<MyBatisStatementBadge> BadgesFor(IMyBatisWorkspaceSession session, string statementId);

    /// <summary>배지가 바뀌었다(「문장」 창이 다시 그린다).</summary>
    event EventHandler? BadgesChanged;
}
