using Oranda.Core.Connection;

namespace Oranda.Core.Tests.Connection;

// 소유: TSK-03-07. 서버 버전으로 정해지는 기능 켜짐(11g 에서 12c 이후 기능 끄기).
public class ServerFeaturesTests
{
    private static ServerInfo Server(string version) =>
        new(version, null, null, null, null, null, false, 1, new ServerCapabilities(true, true, true, true, true));

    [Fact]
    public void Release_11_2_turns_off_12c_and_23ai_features()
    {
        var f = Server("11.2.0.2.0").Features;

        Assert.False(f.Release12Columns);
        Assert.False(f.Multitenant);
        Assert.False(f.RowLimiting);
        Assert.False(f.Release23Types);
    }

    [Theory]
    [InlineData("19.0.0.0.0", false)]
    [InlineData("21.3.0.0.0", false)]
    [InlineData("23.4.0.24.5", true)]
    [InlineData("23.26.0.0.0", true)]
    public void Release_19_and_later_keep_12c_features(string version, bool release23)
    {
        var f = Server(version).Features;

        Assert.True(f.Release12Columns);
        Assert.True(f.Multitenant);
        Assert.True(f.RowLimiting);
        Assert.Equal(release23, f.Release23Types);
    }

    [Fact]
    public void Unknown_version_is_treated_as_latest()
    {
        // 관문이 이미 지원 버전만 들여보낸다. 버전을 못 읽었다고 기능을 끄지 않는다.
        Assert.Equal(new ServerFeatures(true, true, true, true), Server("?").Features);
        Assert.Equal(new ServerFeatures(true, true, true, true), ServerFeatures.For(0));
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(18, true)]
    public void Twelve_is_the_boundary(int major, bool expected)
        => Assert.Equal(expected, ServerFeatures.For(major).Release12Columns);

    [Fact]
    public void Unavailable_text_is_the_agreed_wording()
        => Assert.Equal("이 서버 버전에서는 쓸 수 없음", ServerFeatures.UnavailableText);
}
