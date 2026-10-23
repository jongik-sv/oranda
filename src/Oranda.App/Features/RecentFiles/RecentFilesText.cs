using System.Globalization;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.RecentFiles;

/// <summary>최근 파일·최근 작업 공간 글(한/영). 키는 <c>RecentFiles.*</c>(Resources/strings-ko·en.json), 없으면 한국어 기본값을 쓴다(<see cref="UiText"/>).</summary>
internal static class RecentFilesText
{
    public static string NoFiles => T("noFiles", "최근 파일이 없습니다.");

    public static string NoWorkspaces => T("noWorkspaces", "최근 작업 공간이 없습니다.");

    public static string Missing(string name) => F("missing", "{0} 을(를) 찾을 수 없어 최근 목록에서 뺐습니다.", name);

    private static string T(string key, string ko) => UiText.Get("RecentFiles." + key, ko);

    private static string F(string key, string ko, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key, ko), args);
}
