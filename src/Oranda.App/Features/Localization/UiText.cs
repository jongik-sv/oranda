using System;

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
}
