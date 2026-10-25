using System;
using System.Globalization;

namespace Oranda.App.Features.Localization;

/// <summary>
/// 코드로 만드는 작은 대화상자·파일 선택창처럼 <see cref="Oranda.App.Shell.IShellText"/> 를 주입받기 어려운 곳이
/// 지금 언어의 글자를 얻는 입구. 앱에서는 셸이 시작할 때 <see cref="Current"/> 를 채운다(앱당 하나).
/// 아직 안 채워졌거나 키가 없으면 <paramref name="fallback"/> 을 쓴다.
/// </summary>
public static class UiText
{
    public static Oranda.App.Shell.IShellText? Current { get; set; }

    public static string Get(string key, string fallback)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Current?.TryLookup(key) ?? fallback;
    }

    /// <summary>키의 글자(없으면 <paramref name="fallback"/>)에 인수를 끼운다(문화권 무관).</summary>
    public static string Format(string key, string fallback, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, Get(key, fallback), args);

    /// <summary>
    /// 지금 언어가 바뀔 때 <paramref name="handler"/> 를 부른다(상태 표시줄처럼 글자를 미리 만들어 두는 곳이 다시 그리는 데 쓴다).
    /// 아직 글자 입구가 안 채워졌으면 아무 일도 하지 않고 false.
    /// </summary>
    public static bool WatchLanguage(EventHandler handler)
    {
        if (Current is not { } text)
        {
            return false;
        }

        text.LanguageChanged += handler;
        return true;
    }

    public static void UnwatchLanguage(EventHandler handler)
    {
        if (Current is { } text)
        {
            text.LanguageChanged -= handler;
        }
    }
}
