using Oranda.App.Features.ConnectionImport;

namespace Oranda.App.Tests.Features.ConnectionImport;

/// <summary>TSK-03-05 가져오기 명령 시험.</summary>
public class ConnectionImportTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.Equal("Oranda.App.Features.ConnectionImport", typeof(ConnectionImportCommands).Namespace);
    }

    [Fact]
    public void UniqueName_adds_counter_on_collision()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "A (2)" };

        Assert.Equal("B", ConnectionImportCommands.UniqueName("B", taken));
        Assert.Equal("a (3)", ConnectionImportCommands.UniqueName("a", taken));
    }

    [Fact]
    public void Import_command_is_declared()
    {
        Assert.Contains("oranda.conn.import", ConnectionImportCommands.Ids);
    }
}
