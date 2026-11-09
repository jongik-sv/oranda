using Avalonia.Controls;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Shell;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;

namespace Oranda.App.Features.DataCopy;

/// <summary>
/// 데이터 복사(EXP-04) 창 하나를 연다. 창은 모달이 아니므로 실행하는 동안에도 Oranda 의 다른 일을 할 수 있다.
/// 서비스는 모두 창이 처음 쓸 때 <see cref="IServiceProvider"/> 에서 꺼낸다(D-006).
/// </summary>
public static class DataCopyCommands
{
    public const string OpenCommandId = "oranda.tools.dataCopy";
    public const string AddCommandId = "oranda.schema.addToDataCopy";

    private static DataCopyWindow? _window;

    /// <summary>이미 열린 창이 있으면 앞으로 가져오고, 없으면 새로 연다.</summary>
    public static DataCopyWindow Show(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (_window is { } open)
        {
            open.Activate();
            return open;
        }

        var window = new DataCopyWindow { DataContext = new DataCopyViewModel(new DataCopyServices(services)) };
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
        return window;
    }

    /// <summary>스키마 브라우저에서 고른 표를 열려 있는(없으면 새로 여는) 창의 작업에 규칙으로 더한다.</summary>
    public static async Task AddTableAsync(IServiceProvider services, DbObjectRef table, Oranda.Core.Connection.ConnectionProfile? source, CancellationToken ct)
    {
        var window = Show(services);
        if (window.DataContext is DataCopyViewModel vm)
        {
            if (vm.Jobs.Items.Count == 0)
            {
                await vm.InitializeAsync(ct).ConfigureAwait(true);
            }

            await vm.AddTableAsync(table, source, ct).ConfigureAwait(true);
        }
    }
}

/// <summary>
/// 도구 「데이터 복사…」와 스키마 브라우저 표 오른쪽 클릭 「데이터 복사에 추가」.
/// 셸의 처리기 팩터리(DI)가 <see cref="IServiceProvider"/> 를 넣어 준다. 인자 없는 생성자는 아무것도 하지 않는다.
/// </summary>
[OrandaCommand(DataCopyCommands.OpenCommandId, CommandCategory.Tools, Title = "데이터 복사…", Enablement = "!noImplDataTransfer", Placements = ["menu:tools/dataCopy@41"])]
[OrandaCommand(DataCopyCommands.AddCommandId, CommandCategory.Database, Title = "데이터 복사에 추가", When = "schemaFocus", Enablement = "schemaNodeSelected && !noImplDataTransfer",
    Placements = ["context:schema/dataCopy@46"])]
public sealed class DataCopyCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public DataCopyCommandHandler()
    {
    }

    public DataCopyCommandHandler(IServiceProvider services) => _services = services;

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is null)
        {
            return;
        }

        if (context.CommandId == DataCopyCommands.OpenCommandId)
        {
            DataCopyCommands.Show(_services);
            return;
        }

        if (context.CommandId != DataCopyCommands.AddCommandId)
        {
            return;
        }

        var schemaVm = ObjectDetailCommandHandler.FindSchemaBrowser(_services);
        var target = context.Parameter switch
        {
            SchemaNode n => n.Ref,
            DbObjectRef r => r,
            _ => schemaVm?.SelectedNode?.Ref,
        };
        // 표와 시노님(원본 세션으로 실제 계정·표로 풀어 더한다, 묶음 B 설계 3장).
        if (target is null || target.ObjectType is not ("TABLE" or "SYNONYM"))
        {
            return;
        }

        await DataCopyCommands.AddTableAsync(_services, target, schemaVm?.CurrentSession?.Profile, cancellationToken).ConfigureAwait(true);
    }
}
