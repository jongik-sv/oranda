using Oranda.App.Features.Update;

namespace Oranda.App.Tests.Features.Update;

/// <summary>피드 주소 해석: 실행 OS 와 무관하게 글자만 보고 종류를 가른다.</summary>
public class UpdateFeedParserTests
{
    [Theory]
    [InlineData("https://updates.example.com/oranda", UpdateFeedKind.Web, "https://updates.example.com/oranda")]
    [InlineData("  http://intranet/oranda/  ", UpdateFeedKind.Web, "http://intranet/oranda/")]
    [InlineData(@"\\fileserver\releases\oranda", UpdateFeedKind.Folder, @"\\fileserver\releases\oranda")]
    [InlineData(@"D:\releases\oranda", UpdateFeedKind.Folder, @"D:\releases\oranda")]
    [InlineData("D:/releases/oranda", UpdateFeedKind.Folder, "D:/releases/oranda")]
    [InlineData("/Users/me/feed", UpdateFeedKind.Folder, "/Users/me/feed")]
    [InlineData("https://github.com/acme/oranda", UpdateFeedKind.GitHub, "https://github.com/acme/oranda")]
    [InlineData("https://github.com/acme/oranda/", UpdateFeedKind.GitHub, "https://github.com/acme/oranda")]
    [InlineData("https://github.com/acme/oranda.git", UpdateFeedKind.GitHub, "https://github.com/acme/oranda")]
    public void Valid_values_are_classified(string value, UpdateFeedKind kind, string location)
    {
        var result = UpdateFeedParser.Parse(value);

        Assert.True(result.IsValid);
        Assert.Equal(kind, result.Feed!.Kind);
        Assert.Equal(location, result.Feed.Location);
    }

    [Theory]
    [InlineData(null, UpdateFeedError.Empty)]
    [InlineData("   ", UpdateFeedError.Empty)]
    [InlineData("ftp://example.com/feed", UpdateFeedError.UnsupportedScheme)]
    [InlineData("file:///tmp/feed", UpdateFeedError.UnsupportedScheme)]
    [InlineData("https://", UpdateFeedError.InvalidUrl)]
    [InlineData("https://github.com/acme", UpdateFeedError.InvalidGitHub)]
    [InlineData("https://github.com/acme/oranda/releases", UpdateFeedError.InvalidGitHub)]
    [InlineData("releases/oranda", UpdateFeedError.RelativePath)]
    [InlineData("oranda", UpdateFeedError.RelativePath)]
    public void Invalid_values_report_the_reason(string? value, UpdateFeedError error)
    {
        var result = UpdateFeedParser.Parse(value);

        Assert.False(result.IsValid);
        Assert.Equal(error, result.Error);
        Assert.StartsWith("Update.feed.error.", UpdateFeedParser.ErrorKey(error), StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_http_is_flagged_but_https_is_not()
    {
        Assert.True(UpdateFeedParser.Parse("http://intranet/feed").Feed!.IsPlainHttp);
        Assert.False(UpdateFeedParser.Parse("https://intranet/feed").Feed!.IsPlainHttp);
        Assert.False(UpdateFeedParser.Parse(@"\\server\share").Feed!.IsPlainHttp);
    }

    [Fact]
    public void Every_error_has_a_message_in_both_languages()
    {
        var ko = Oranda.App.Features.Localization.StringCatalog.Load("ko");
        var en = Oranda.App.Features.Localization.StringCatalog.Load("en");
        foreach (var error in Enum.GetValues<UpdateFeedError>().Where(e => e != UpdateFeedError.None))
        {
            var key = UpdateFeedParser.ErrorKey(error);
            Assert.True(ko.ContainsKey(key) && en.ContainsKey(key), key);
        }
    }
}
