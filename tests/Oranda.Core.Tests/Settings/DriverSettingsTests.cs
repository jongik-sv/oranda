using System.Text.Json;
using Oranda.Core.Connection;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Settings;

/// <summary>drivers.tiberoPath: 새 섹션이라 키가 없는 옛 파일은 「지정 안 함」으로 읽히고, 쓴 값은 camelCase 로 왕복한다(D-046 결정25).</summary>
public class DriverSettingsTests
{
    [Fact]
    public void Tibero_path_survives_a_save_and_load_round_trip_under_the_drivers_key()
    {
        var settings = new AppSettings { Drivers = new DriverSettings { TiberoPath = @"C:\drivers\Tibero.DataAccess.dll" } };

        var json = JsonSerializer.Serialize(settings, OrandaJsonContext.Default.AppSettings);
        var loaded = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!;

        Assert.Equal(@"C:\drivers\Tibero.DataAccess.dll", loaded.Drivers.TiberoPath);
        Assert.Contains("\"drivers\"", json, StringComparison.Ordinal);
        Assert.Contains("\"tiberoPath\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Old_files_without_the_section_or_with_null_read_as_not_specified()
    {
        var absent = JsonSerializer.Deserialize("""{ "schemaVersion": 1 }""", OrandaJsonContext.Default.AppSettings)!;
        var nullSection = JsonSerializer.Deserialize("""{ "schemaVersion": 1, "drivers": null }""", OrandaJsonContext.Default.AppSettings)!;

        Assert.Null(absent.Drivers.TiberoPath);
        Assert.Null(nullSection.Drivers.TiberoPath);
    }

    [Theory]
    [InlineData(DriverState.Ok, true)]
    [InlineData(DriverState.Unverified, true)]
    [InlineData(DriverState.Missing, false)]
    [InlineData(DriverState.NotFound, false)]
    [InlineData(DriverState.WrongAssembly, false)]
    [InlineData(DriverState.FrameworkBuild, false)]
    public void Only_ok_and_unverified_drivers_are_usable(DriverState state, bool usable)
    {
        Assert.Equal(usable, new DriverCheck(state).IsUsable);
    }
}
