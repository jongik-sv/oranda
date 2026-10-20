namespace Oranda.App.Features.Update;

public enum UpdateFeedKind
{
    /// <summary>http·https 주소(정적 파일 서버).</summary>
    Web,

    /// <summary>로컬·UNC 폴더 경로.</summary>
    Folder,

    /// <summary><c>https://github.com/&lt;owner&gt;/&lt;repo&gt;</c> (시험판 제외).</summary>
    GitHub,
}

/// <summary>해석한 피드. <see cref="Location"/> 은 종류에 맞게 정리한 값(GitHub 은 <c>https://github.com/owner/repo</c>).</summary>
public sealed record UpdateFeed(UpdateFeedKind Kind, string Location)
{
    /// <summary>암호화되지 않은 http 주소인지(설정 화면 경고용).</summary>
    public bool IsPlainHttp => Kind == UpdateFeedKind.Web && Location.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
}

/// <summary>피드 주소를 해석하지 못한 이유. 문구는 <c>Update.feed.error.*</c> 리소스.</summary>
public enum UpdateFeedError
{
    None,
    Empty,
    UnsupportedScheme,
    InvalidUrl,
    InvalidGitHub,
    RelativePath,
}

public readonly record struct UpdateFeedParseResult(UpdateFeed? Feed, UpdateFeedError Error)
{
    public bool IsValid => Feed is not null;
}

/// <summary>
/// 피드 주소 해석. 실행 중인 OS 와 무관하게 글자만 보고 종류를 가른다(시험이 macOS 에서 UNC·드라이브 경로를 그대로 검사한다).
/// </summary>
public static class UpdateFeedParser
{
    public static UpdateFeedParseResult Parse(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return Fail(UpdateFeedError.Empty);
        }

        if (IsFolderPath(text))
        {
            return new UpdateFeedParseResult(new UpdateFeed(UpdateFeedKind.Folder, text), UpdateFeedError.None);
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            return ParseUrl(text);
        }

        return Fail(UpdateFeedError.RelativePath);
    }

    private static UpdateFeedParseResult ParseUrl(string text)
    {
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(UpdateFeedError.UnsupportedScheme);
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return Fail(UpdateFeedError.InvalidUrl);
        }

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                return Fail(UpdateFeedError.InvalidGitHub);
            }

            var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
            return repo.Length == 0
                ? Fail(UpdateFeedError.InvalidGitHub)
                : new UpdateFeedParseResult(new UpdateFeed(UpdateFeedKind.GitHub, $"https://github.com/{parts[0]}/{repo}"), UpdateFeedError.None);
        }

        return new UpdateFeedParseResult(new UpdateFeed(UpdateFeedKind.Web, text), UpdateFeedError.None);
    }

    /// <summary>UNC(<c>\\서버\공유</c>), 드라이브 경로(<c>C:\</c>·<c>C:/</c>), 유닉스 절대 경로(<c>/</c>)면 폴더로 본다.</summary>
    private static bool IsFolderPath(string text) =>
        text.StartsWith(@"\\", StringComparison.Ordinal)
        || (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/'))
        || (text[0] == '/' && !text.StartsWith("//", StringComparison.Ordinal));

    private static UpdateFeedParseResult Fail(UpdateFeedError error) => new(null, error);

    /// <summary>해석 실패 이유의 리소스 키.</summary>
    public static string ErrorKey(UpdateFeedError error) => "Update.feed.error." + error switch
    {
        UpdateFeedError.Empty => "empty",
        UpdateFeedError.UnsupportedScheme => "scheme",
        UpdateFeedError.InvalidUrl => "url",
        UpdateFeedError.InvalidGitHub => "github",
        _ => "relative",
    };
}
