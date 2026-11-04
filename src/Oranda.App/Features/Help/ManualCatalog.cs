using Oranda.App.Shell.Keymaps;

namespace Oranda.App.Features.Help;

/// <summary>
/// 사용 설명서(GitHub Pages) 주소와 「화면 → 매뉴얼 절」 표. F1(<c>oranda.help.docs</c>)이 지금 포커스가 있는 화면에 맞는 절을 연다.
/// 경로는 <c>site/src/content/docs/</c> 아래 본문 경로(확장자 뺀 값)와 같아야 하며, 시험이 본문에 실제로 있는지 확인한다.
/// </summary>
public static class ManualCatalog
{
    /// <summary>매뉴얼 사이트 주소(<c>site/astro.config.mjs</c> 의 <c>site</c> + <c>base</c> 와 같다).</summary>
    public const string DefaultSite = "https://jongik-sv.github.io/oranda";

    /// <summary>포커스 자리를 알 수 없을 때 여는 절.</summary>
    public const string StartPath = "start/overview";

    /// <summary>포커스 자리(<see cref="FocusScopes"/> 의 자리 이름) → 매뉴얼 절 경로. 이 표 하나로 관리한다.</summary>
    public static IReadOnlyDictionary<string, string> ScreenPaths { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sqlText"] = "editor/sql-editor",
        ["sqlGrid"] = "run/result-grid",
        ["plsql"] = "schema/plsql",
        ["mybatisText"] = "editor/mybatis",
        ["schema"] = "schema/browser",
        ["monitor"] = "tools/monitor",
        ["lockMonitor"] = "tools/monitor",
    };

    /// <summary>포커스 자리에 해당하는 절 경로. 자리가 없거나 표에 없으면 <see cref="StartPath"/>.</summary>
    public static string PathFor(string? focusSlot) =>
        focusSlot is not null && ScreenPaths.TryGetValue(focusSlot, out var path) ? path : StartPath;

    /// <summary>절 경로 → 열 주소(끝에 <c>/</c>).</summary>
    public static string UrlFor(string path) => $"{DefaultSite}/{path.Trim('/')}/";
}
