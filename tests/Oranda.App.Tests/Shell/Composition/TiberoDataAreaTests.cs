using Oranda.App.Features.DataCopy;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Features;
using Oranda.Core.Composition;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Shell.Composition;

// 소유: tb-appgate TU1. 실제 앱 조립(Oranda.Tibero 키 등록)으로 본 Tibero 의 데이터 영역: 내보내기·가져오기는 켜지고 복사는 이유와 함께 꺼진다.
[Collection("DbmsFeatureKeysSerial")]
public class TiberoDataAreaTests
{
    private static ServiceContainer Compose() =>
        AppComposition.Create(new AppCompositionOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-tibero-" + Guid.NewGuid().ToString("N")) });

    private static string EnablementOf(string id) =>
        ShellTestHelpers.AppContributions.Commands.Single(c => c.Definition.Id == id).Definition.Enablement ?? string.Empty;

    [Fact]
    public void Tibero_keeps_export_and_import_on_and_turns_the_two_copy_commands_off_with_the_reason()
    {
        using var c = Compose();
        var keys = new ContextKeyService();
        keys.SetMany([("hasSession", true), ("schemaNodeSelected", true), ("gridHasResult", true)]);

        DbmsServiceKeys.Apply(keys, c, Dbms.Tibero);
        try
        {
            Assert.True(DbmsServiceKeys.HasImplementation(c, Dbms.Tibero, DbmsServiceArea.DataTransfer));
            Assert.False(DbmsServiceKeys.HasImplementation(c, Dbms.Tibero, DbmsServiceArea.DataCopy));

            foreach (var id in new[] { "oranda.grid.export", "oranda.grid.exportExcel", "oranda.tools.import" })
            {
                Assert.True(keys.Evaluate(EnablementOf(id)), id);
            }

            var reason = DbmsServiceKeys.WrapPhrase(_ => null, () => Dbms.Tibero);
            foreach (var id in new[] { DataCopyCommands.OpenCommandId, DataCopyCommands.AddCommandId })
            {
                var expression = EnablementOf(id);
                Assert.False(keys.Evaluate(expression), id);
                Assert.Equal("Tibero 에서는 아직 지원하지 않음", ContextKeyReasons.Explain(expression, keys.Evaluate, reason));
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
