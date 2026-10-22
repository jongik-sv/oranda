using System.Reflection;
using Oranda.App.Features.Update;

namespace Oranda.App.Tests.Features.Update;

/// <summary>TSK-13-21: 빌드 기본 피드(GitHub Releases) 판정과 설정 주소 우선순위.</summary>
public class UpdateDefaultFeedTests
{
    private const string Repo = "https://github.com/jongik-sv/oranda";

    [Fact]
    public void The_repository_address_resolves_to_a_github_source()
    {
        var feed = UpdateFeedParser.Parse(Repo).Feed!;

        Assert.Equal(UpdateFeedKind.GitHub, feed.Kind);
        Assert.Equal(Repo, feed.Location);
    }

    [Fact]
    public void Build_metadata_gives_the_default_feed_only_when_it_has_a_value()
    {
        Assert.Null(AppVersion.ReadDefaultFeed([]));
        Assert.Null(AppVersion.ReadDefaultFeed([new AssemblyMetadataAttribute("OrandaUpdateFeed", string.Empty)]));
        Assert.Null(AppVersion.ReadDefaultFeed([new AssemblyMetadataAttribute("Other", Repo)]));
        Assert.Equal(Repo, AppVersion.ReadDefaultFeed([new AssemblyMetadataAttribute("Other", "x"), new AssemblyMetadataAttribute("OrandaUpdateFeed", Repo)]));
    }

    [Fact]
    public async Task Empty_setting_falls_back_to_the_build_default_and_a_configured_address_wins()
    {
        var h = new UpdateHarness();
        h.Service.DefaultFeed = Repo;

        Assert.Equal(UpdateFeedKind.GitHub, h.Service.ResolveFeed().Feed!.Kind);

        await h.Configure(u => u with { FeedUrl = @"\\files\oranda" });
        Assert.Equal(UpdateFeedKind.Folder, h.Service.ResolveFeed().Feed!.Kind);

        await h.Configure(u => u with { FeedUrl = "  " });
        Assert.Equal(UpdateFeedKind.GitHub, h.Service.ResolveFeed().Feed!.Kind);
    }
}
