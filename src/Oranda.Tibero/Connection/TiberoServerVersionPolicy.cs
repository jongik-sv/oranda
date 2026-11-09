using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;

namespace Oranda.Tibero.Connection;

/// <summary>
/// Tibero 지원 버전 관문(docs/design/tibero.md 머리말: 6 이상 전부, 버전으로 막지 않고 없는 뷰·기능만 판정으로 끈다).
/// 주 버전이 <see cref="MinimumMajor"/> 이상이면 허용한다. DB 없이 문자열만으로 판별하므로 단위 시험이 가능하다.
/// </summary>
[OrandaService(typeof(IServerVersionPolicy), Dbms = Dbms.Tibero)]
internal sealed partial class TiberoServerVersionPolicy : IServerVersionPolicy
{
    /// <summary>접속을 허용하는 가장 낮은 주 버전.</summary>
    public const int MinimumMajor = 6;

    public Dbms Dbms => Dbms.Tibero;

    public string MinimumText => MinimumMajor.ToString(CultureInfo.InvariantCulture);

    /// <summary>실제 서버로 시험한 버전. Tibero 7 시험 서버는 아직 없다.</summary>
    public IReadOnlyList<string> TestedVersions { get; } = ["6.2.15"];

    public bool IsSupported(string? serverVersion, out string reason)
    {
        if (string.IsNullOrWhiteSpace(serverVersion))
        {
            reason = "서버 버전을 확인할 수 없습니다.";
            return false;
        }

        if (!TryParseMajor(serverVersion, out var major))
        {
            reason = $"서버 버전 형식을 알 수 없습니다. Oranda 는 Tibero {MinimumText} 이상을 지원합니다.";
            return false;
        }

        if (major >= MinimumMajor)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"Oranda 는 Tibero {MinimumText} 이상을 지원합니다. 이 서버는 {major} 입니다.";
        return false;
    }

    /// <summary>
    /// 글 안의 첫 버전 번호(<c>6</c>, <c>6.2.15</c>, <c>Tibero 7 (DB 7.2.1 ...)</c> 의 <c>7</c>)에서 주 버전을 뽑는다.
    /// 점이 없는 한 자리 번호도 받는다(버전 글이 「Tibero 6」 만 담은 경우).
    /// </summary>
    internal static bool TryParseMajor(string text, out int major)
    {
        major = 0;
        var match = VersionPattern().Match(text);
        return match.Success && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out major);
    }

    /// <summary>
    /// 글에서 점 있는 버전 번호(<c>6.2.15</c>)를 뽑는다. 없으면 null. <c>V$VERSION</c> 의 한 줄 전체처럼 앞뒤에 글이 붙은 값에 쓴다.
    /// </summary>
    internal static string? ExtractDottedVersion(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = DottedPattern().Match(text);
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// <c>SELECT * FROM V$VERSION</c> 의 (NAME, VALUE) 행에서 서버 버전 글을 조립한다(실측 6.2.15 서버: <c>PRODUCT_MAJOR=6</c>, <c>PRODUCT_MINOR</c> 는 공백일 수 있음).
    /// 부 버전이 숫자면 <c>6.2</c>, 아니면 <c>6</c>. <c>PRODUCT_MAJOR</c> 가 없으면 행 값에서 점 있는 번호를 찾고, 그것도 없으면 null.
    /// </summary>
    internal static string? FromVersionRows(IEnumerable<(string? Name, string? Value)> rows)
    {
        string? major = null;
        string? minor = null;
        string? dotted = null;
        foreach (var (name, value) in rows)
        {
            var trimmed = value?.Trim();
            if (string.Equals(name?.Trim(), "PRODUCT_MAJOR", StringComparison.OrdinalIgnoreCase))
            {
                major = trimmed;
            }
            else if (string.Equals(name?.Trim(), "PRODUCT_MINOR", StringComparison.OrdinalIgnoreCase))
            {
                minor = trimmed;
            }
            else
            {
                dotted ??= ExtractDottedVersion(trimmed);
            }
        }

        if (!string.IsNullOrEmpty(major) && major.All(char.IsAsciiDigit))
        {
            return !string.IsNullOrEmpty(minor) && minor.All(char.IsAsciiDigit) ? major + "." + minor : major;
        }

        return dotted;
    }

    [GeneratedRegex(@"(?<![\d.])(\d{1,3})(?=[.\s)]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"(?<![\d.])\d{1,3}(?:\.\d{1,6}){1,3}(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex DottedPattern();
}
