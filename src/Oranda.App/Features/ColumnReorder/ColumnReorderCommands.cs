using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Features.ColumnReorder;

/// <summary>
/// TSK-13-01 열 순서 바꾸기 명령 처리기 (commands.md 7장 「데이터베이스」). 스키마 브라우저 표 문맥 메뉴와 표 상세 「열」 탭 단추가 같은 대화상자를 연다.
/// 대상 표는 명령 인자(<see cref="SchemaNode"/> 또는 <see cref="DbObjectRef"/>), 없으면 스키마 브라우저의 선택 노드.
/// 서비스는 실행할 때 꺼낸다(시작 경로에서 Oracle 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaCommand("oranda.schema.reorderColumns", CommandCategory.Database, When = "schemaFocus", Enablement = "schemaNodeSelected && !readOnlySession",
    Placements = ["context:schema/detail@42"])]
public sealed class ColumnReorderCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public ColumnReorderCommandHandler()
    {
    }

    public ColumnReorderCommandHandler(IServiceProvider services) => _services = services;

    /// <summary>시험용: 지금 세션을 바꾼다.</summary>
    internal Func<ISession?>? SessionProvider { get; set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is null)
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
        var session = SessionProvider?.Invoke() ?? schemaVm?.CurrentSession
            ?? ConnectionManager.SessionRouter.CurrentOf(_services);
        if (target is null || session is null || !string.Equals(target.ObjectType, "TABLE", StringComparison.Ordinal))
        {
            return;
        }

        await ColumnReorderLauncher.OpenAsync(_services, session, target, applied: null, cancellationToken).ConfigureAwait(true);
    }
}

/// <summary>열 순서 바꾸기 대화상자를 만들어 연다(명령과 표 상세 단추가 함께 쓴다).</summary>
internal static class ColumnReorderLauncher
{
    /// <summary>시험용: 창을 실제로 열지 않고 만든 ViewModel 만 받는다.</summary>
    internal static Func<ColumnReorderViewModel, Task>? ShowOverride { get; set; }

    internal static ColumnReorderViewModel? CreateViewModel(IServiceProvider services, EventHandler? applied)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService(typeof(IMetadataProvider)) is not IMetadataProvider meta)
        {
            return null;
        }

        var cache = services.GetService(typeof(IMetadataCache)) as IMetadataCache;
        var vm = new ColumnReorderViewModel(
            meta,
            services.GetService(typeof(IColumnReorderProvider)) as IColumnReorderProvider,
            services.GetService(typeof(IExecutionService)) as IExecutionService)
        {
            InvalidateCache = (s, table, ct) => cache?.InvalidateAsync(s.DbKey, [table], ct) ?? Task.CompletedTask,
        };
        if (applied is not null)
        {
            vm.Applied += applied;
        }

        return vm;
    }

    /// <summary>표를 읽어 대화상자를 연다(닫힐 때까지 기다린다).</summary>
    internal static async Task OpenAsync(IServiceProvider services, ISession session, DbObjectRef table, EventHandler? applied, CancellationToken ct)
    {
        var vm = CreateViewModel(services, applied);
        if (vm is null)
        {
            return;
        }

        try
        {
            await vm.LoadAsync(session, table, ct).ConfigureAwait(true);
            if (ShowOverride is { } show)
            {
                await show(vm).ConfigureAwait(true);
                return;
            }

            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            var window = new ColumnReorderWindow { DataContext = vm };
            window.RefreshHeader();
            if (owner is not null)
            {
                await window.ShowDialog(owner).ConfigureAwait(true);
            }
            else
            {
                window.Show();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ShowOverride is not null)
            {
                vm.Dispose();
            }
        }
    }
}

/// <summary>자리 표시 클래스(ui-contract 소유 파일 규약). 실제 처리기는 <see cref="ColumnReorderCommandHandler"/>.</summary>
public static class ColumnReorderCommands
{
}
