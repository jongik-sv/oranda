using Oranda.App.Shell;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 고른 행이 둘 이상이면 행 명령 이름에 개수를 넣는다(「행 3개 삭제」, 하나면 「행 삭제」, 영어 「Delete 3 Rows」).
/// 메뉴를 열 때마다 만드는 제목이 이 함수를 거친다(<see cref="ShellText.TitleOverride"/>, 시작할 때 <c>ResultGridSettingsStartup</c> 이 연결한다). 문구는 <c>CommandTitles*.json</c> 의 <c>Cmd.&lt;id&gt;.many</c>.
/// </summary>
public static class GridRowTitles
{
    private static readonly HashSet<string> Ids =
    [
        "oranda.grid.edit.deleteRow", "oranda.grid.edit.resetRow", "oranda.grid.edit.duplicateRow",
        "oranda.grid.copyRows", "oranda.grid.copyRowsWithHeaders",
    ];

    /// <summary>개수가 들어간 제목. 대상이 아닌 명령이거나 고른 행이 하나 이하면 null(기본 제목).</summary>
    public static string? For(string commandId, IShellText text)
    {
        if (!Ids.Contains(commandId) || ResultGridCommands.Active is not { HasSelection: true } grid
            || text.TryLookup("Cmd." + commandId + ".many") is not { } format)
        {
            return null;
        }

        var count = grid.SelectedRowCount;
        return count < 2 ? null : string.Format(System.Globalization.CultureInfo.CurrentCulture, format, count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));
    }
}
