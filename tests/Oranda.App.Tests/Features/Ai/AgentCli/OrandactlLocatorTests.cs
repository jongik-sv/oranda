namespace Oranda.App.Tests.Features.Ai.AgentCli;

/// <summary>orandactl 찾기: 시험 출력의 구성·tfm 을 따른다(태그 CI = Release). 빌드는 하지 않고 경로 계산만 본다.</summary>
public class OrandactlLocatorTests
{
    [Theory]
    [InlineData("/r/tests/Oranda.App.Tests/bin/Release/net10.0/", "Release", "net10.0")]
    [InlineData("/r/tests/Oranda.App.Tests/bin/Debug/net10.0", "Debug", "net10.0")]
    [InlineData(@"C:\r\tests\Oranda.App.Tests\bin\Release\net10.0\", "Release", "net10.0")]
    [InlineData("/r/bin/tools/tests/Oranda.App.Tests/bin/Release/net11.0/osx-arm64/", "Release", "net11.0")] // 마지막 bin 을 본다
    public void Output_shape_reads_configuration_and_framework_after_the_last_bin(string baseDirectory, string configuration, string framework) =>
        Assert.Equal((configuration, framework), OrandactlLocator.OutputShape(baseDirectory));

    [Theory]
    [InlineData("/r/out/")]
    [InlineData("/r/bin/Release")]
    public void Unknown_output_shape_is_null(string baseDirectory) => Assert.Null(OrandactlLocator.OutputShape(baseDirectory));

    [Fact]
    public void Release_test_output_looks_for_the_release_cli_then_the_agent_folder()
    {
        var baseDirectory = Path.Combine("/r", "tests", "Oranda.App.Tests", "bin", "Release", "net10.0");
        var candidates = OrandactlLocator.Candidates(baseDirectory, "/r", windows: false);

        Assert.Equal(
            [
                Path.Combine("/r", "src", "Oranda.Agent.Cli", "bin", "Release", "net10.0", "orandactl"),
                Path.Combine(baseDirectory, "agent", "orandactl"),
            ],
            candidates);
        Assert.EndsWith("orandactl.exe", OrandactlLocator.Candidates(baseDirectory, "/r", windows: true)[0], StringComparison.Ordinal);
        Assert.Contains(Path.Combine("bin", "Debug", "net10.0"), OrandactlLocator.Candidates("/elsewhere", "/r", windows: false)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void This_test_run_finds_a_built_cli()
    {
        var path = OrandactlLocator.Find();
        Assert.True(File.Exists(path));
        var shape = OrandactlLocator.OutputShape(AppContext.BaseDirectory)!.Value;
        Assert.True(path.Contains(Path.Combine("bin", shape.Configuration, shape.Framework), StringComparison.OrdinalIgnoreCase)
            || path.Contains(Path.Combine(AppContext.BaseDirectory, "agent"), StringComparison.OrdinalIgnoreCase));
    }
}
