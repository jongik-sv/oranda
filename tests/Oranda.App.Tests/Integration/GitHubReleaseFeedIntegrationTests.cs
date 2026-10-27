using Oranda.App.Features.Update;
using Velopack;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// 실제 네트워크: 빌드 기본 피드(GitHub)를 앱과 같은 방식(<c>releases/latest/download/</c>, API 아님)으로 읽어 버전을 해석한다.
/// GitHub API 요청 한도(IP 당 시간 60회)를 쓰지 않으므로 회사망에서도 통과해야 한다.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GitHubReleaseFeedIntegrationTests
{
    [Fact]
    public async Task Latest_download_serves_the_windows_channel_feed_without_the_api()
    {
        var feed = UpdateFeedParser.Parse("https://github.com/jongik-sv/oranda").Feed!;
        var url = VelopackAppUpdater.SourceLocation(feed) + "releases.win.json";

        using var http = new HttpClient();
        var json = await http.GetStringAsync(new Uri(url));
        var assets = VelopackAssetFeed.FromJson(json).Assets;

        var full = Assert.Single(assets, a => a.Type == VelopackAssetType.Full);
        Assert.True(full.Version > SemanticVersion.Parse("0.1.2"), full.Version.ToString());
        Assert.EndsWith("-full.nupkg", full.FileName, StringComparison.Ordinal);
    }
}
