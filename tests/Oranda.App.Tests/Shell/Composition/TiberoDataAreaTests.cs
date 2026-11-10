using Oranda.App.Features.DataCopy;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Features;
using Oranda.Core.Composition;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Shell.Composition;

// 소유: tb-appgate TU1, xdb-copy XC0. 실제 앱 조립(Oranda.Tibero 키 등록)으로 본 Tibero 의 데이터 영역: 내보내기·가져오기·복사가 모두 켜진다(XC0 에서 복사 켬).
[Collection("DbmsFeatureKeysSerial")]
public class TiberoDataAreaTests
{
    private static ServiceContainer Compose() =>
        AppComposition.Create(new AppCompositionOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-tibero-" + Guid.NewGuid().ToString("N")) });

    private static string EnablementOf(string id) =>
        ShellTestHelpers.AppContributions.Commands.Single(c => c.Definition.Id == id).Definition.Enablement ?? string.Empty;

    [Fact]
    public void Tibero_turns_export_import_and_the_two_copy_commands_on()
    {
        using var c = Compose();
        var keys = new ContextKeyService();
        keys.SetMany([("hasSession", true), ("schemaNodeSelected", true), ("gridHasResult", true)]);

        DbmsServiceKeys.Apply(keys, c, Dbms.Tibero);
        try
        {
            Assert.True(DbmsServiceKeys.HasImplementation(c, Dbms.Tibero, DbmsServiceArea.DataTransfer));
            Assert.True(DbmsServiceKeys.HasImplementation(c, Dbms.Tibero, DbmsServiceArea.DataCopy));

            foreach (var id in new[] { "oranda.grid.export", "oranda.grid.exportExcel", "oranda.tools.import", DataCopyCommands.OpenCommandId, DataCopyCommands.AddCommandId })
            {
                Assert.True(keys.Evaluate(EnablementOf(id)), id);
            }
        }
        finally
        {
            DbmsServiceKeys.Apply(keys, null, null);
        }
    }

    [Fact]
    public void Oracle_keeps_every_data_command_on()
    {
        using var c = Compose();
        var keys = new ContextKeyService();
        keys.SetMany([("hasSession", true), ("schemaNodeSelected", true), ("gridHasResult", true)]);

        DbmsServiceKeys.Apply(keys, c, Dbms.Oracle);
        try
        {
            foreach (var id in new[] { "oranda.grid.export", "oranda.grid.exportExcel", "oranda.tools.import", DataCopyCommands.OpenCommandId, DataCopyCommands.AddCommandId })
            {
                Assert.True(keys.Evaluate(EnablementOf(id)), id);
            }
        }
        finally
        {
            DbmsServiceKeys.Apply(keys, null, null);
        }
    }
}
