using System;
using System.Globalization;

namespace Oranda.App.Features.Localization;

/// <summary>
/// 설정의 언어(<c>system</c> | <c>ko</c> | <c>en</c>)를 실제로 쓸 언어(<c>ko</c> | <c>en</c>)로 바꾼다.
/// <c>system</c> 은 OS 언어를 따라가고, 한국어가 아니면 영어다.
/// </summary>
public static class UiLanguage
{
    public const string System = "system";

    /// <summary>OS 화면 언어(두 글자). 시험이 바꿔 끼운다.</summary>
    public static Func<string> SystemLanguageProvider { get; set; } = () => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary>저장된 값을 <c>system</c>·<c>ko</c>·<c>en</c> 중 하나로 정리한다(비었거나 모르는 값은 <c>system</c>).</summary>
    public static string Normalize(string? setting) => setting?.Trim().ToLowerInvariant() switch
    {
        "ko" => "ko",
        "en" => "en",
        _ => System,
    };

    /// <summary>실제로 쓸 언어(OS 언어는 <see cref="SystemLanguageProvider"/> 로 안다).</summary>
    public static string Resolve(string? setting) => Resolve(setting, SystemLanguageProvider());

    /// <summary>실제로 쓸 언어. <paramref name="systemLanguage"/> 는 OS 화면 언어 두 글자(<c>ko</c>, <c>en</c>, <c>ja</c> …).</summary>
    public static string Resolve(string? setting, string? systemLanguage) => Normalize(setting) switch
    {
        "ko" => "ko",
        "en" => "en",
        _ => string.Equals(systemLanguage?.Trim(), "ko", StringComparison.OrdinalIgnoreCase) ? "ko" : "en",
    };
}
