using System.Globalization;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.MyBatisWorkspace.Parameters;

/// <summary>두 도구 창이 ViewModel 에서 만드는 화면 글(바인딩으로 들어오는 글은 LiteralTranslator 대상이 아니라 리소스 키로 읽는다).</summary>
internal static class MyBatisText
{
    /// <summary>키의 글에 값을 채운다. 리소스를 못 읽으면(시험·초기) <paramref name="fallback"/> 한국어 글을 쓴다.</summary>
    public static string Format(string key, string fallback, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, UiText.Get(key, fallback), args);
}
