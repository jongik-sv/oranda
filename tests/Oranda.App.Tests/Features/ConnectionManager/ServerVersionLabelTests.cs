using Oranda.App.Features.ConnectionManager;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 소유: TSK-03-07. 상태 표시줄·접속 메시지의 서버 버전 짧은 이름(11.2 추가, 19c·26ai 는 그대로).
public class ServerVersionLabelTests
{
    [Theory]
    [InlineData("11.2.0.2.0", "11.2")]
    [InlineData("11.2.0.4.0", "11.2")]
    [InlineData("11", "11g")]
    [InlineData("19.0.0.0.0", "19c")]
    [InlineData("19.25.0.0.0", "19c")]
    [InlineData("23.4.0.24.5", "23ai")]
    [InlineData("23.26.0.0.0", "26ai")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("abc", "")]
    public void Status_bar_short_version(string? version, string expected)
        => Assert.Equal(expected, ConnectionStatusViewModel.ShortVersion(version));

    // 접속 서버 표시: Oracle 은 위 표 그대로, Tibero 는 「Tibero 주.부」(6.2.15 → Tibero 6.2).
    [Theory]
    [InlineData(Dbms.Oracle, "19.25.0.0.0", "19c")]
    [InlineData(Dbms.Oracle, "11.2.0.4.0", "11.2")]
    [InlineData(Dbms.Oracle, "23.26.0.0.0", "26ai")]
    [InlineData(Dbms.Tibero, "6.2.15", "Tibero 6.2")]
    [InlineData(Dbms.Tibero, "7.1.0", "Tibero 7.1")]
    [InlineData(Dbms.Tibero, "7", "Tibero 7")]
    [InlineData(Dbms.Tibero, "", "")]
    [InlineData(Dbms.Tibero, "abc", "")]
    public void Status_bar_version_label_names_the_dbms(Dbms dbms, string version, string expected)
        => Assert.Equal(expected, ConnectionStatusViewModel.VersionLabel(dbms, version));

    [Fact]
    public void Status_bar_tooltip_keeps_oracle_text_and_renames_the_dbms_for_tibero()
    {
        const string format = "{0}@{1} · Oracle {2} · SID {3} — x";

        Assert.Equal("scott@db · Oracle 19.3.0.0.0 · SID 7 — x",
            ConnectionStatusViewModel.TipFormat(format, Dbms.Oracle, "scott", "db", "19.3.0.0.0", "7"));
        Assert.Equal("scott@db · Tibero 6.2.15 · SID 7 — x",
            ConnectionStatusViewModel.TipFormat(format, Dbms.Tibero, "scott", "db", "6.2.15", "7"));
    }

    [Theory]
    [InlineData("11.2.0.2.0", "11g")]
    [InlineData("19.0.0.0.0", "19c")]
    [InlineData("23.26.0.0.0", "26ai")]
    [InlineData("x", null)]
    [InlineData(null, null)]
    public void Connection_test_label(string? version, string? expected)
        => Assert.Equal(expected, ConnectionService.ServerLabel(version));
}
