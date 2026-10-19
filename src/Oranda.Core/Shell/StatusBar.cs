namespace Oranda.Core.Shell;

/// <summary>
/// 상태 표시줄 구간 하나 (ui-shell.md 1.2). 구간 ViewModel 이 구현하고 <see cref="OrandaStatusBarItemAttribute"/> 를 단다.
/// </summary>
public interface IStatusBarItem
{
    /// <summary>지금 보일 내용이 있는지. 거짓이면 셸이 자리를 접는다.</summary>
    bool IsVisible { get; }
}

/// <summary>상태 표시줄 구간 등록 정보.</summary>
/// <param name="Id"><c>oranda.status.&lt;camelCase&gt;</c>.</param>
/// <param name="Order">왼쪽부터의 순서(ui-shell.md 1.2 「순서」).</param>
/// <param name="CollapseRank">좁을 때 숨기는 순위. 0 = 숨기지 않음(안전 정보), 클수록 먼저 숨김.</param>
/// <param name="ItemType"><see cref="IStatusBarItem"/> 구현 형식.</param>
public sealed record StatusBarItemDefinition(string Id, int Order, int CollapseRank, Type ItemType);

/// <summary>상태 표시줄 구간 레지스트리. 탑재 순서 = <see cref="StatusBarItemDefinition.Order"/> → ID.</summary>
public interface IStatusBarRegistry
{
    IReadOnlyList<StatusBarItemDefinition> Items { get; }
}
