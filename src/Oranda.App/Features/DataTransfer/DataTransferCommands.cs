using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.DataTransfer;

/// <summary>내보내기 대상 한 건: 다시 실행할 질의와 바인드, 세션, INSERT 형식에 쓸 표 이름 제안.</summary>
public sealed record ExportSource(ISession Session, SqlStatement Query, IReadOnlyDictionary<string, BindValue> Binds)
{
    /// <summary>INSERT 내보내기의 기본 표 이름(알 수 있으면). 없으면 <c>TABLE_NAME</c>.</summary>
    public string? SuggestedTableName { get; init; }

    /// <summary>저장 대화상자의 기본 파일 이름(확장자 제외).</summary>
    public string? SuggestedFileName { get; init; }
}

/// <summary>
/// TSK-06-07 내보내기·가져오기 — 명령 처리기와 화면 연결 지점(commands.md 「결과」·「도구」).
/// 서비스는 모두 처음 쓸 때 <see cref="IServiceProvider"/> 에서 꺼낸다(D-006: 첫 프레임 전에 Oracle 어셈블리를 올리지 않는다).
/// </summary>
public static class DataTransferCommands
{
    /// <summary>
    /// 결과 그리드 「내보내기」가 다시 실행할 질의를 알려 주는 연결 지점. 결과 영역(TSK-06-02·06-03)이 채운다.
    /// 비어 있거나 null 을 돌려주면 편집기에서 커서가 놓인 문장을 쓴다.
    /// </summary>
    public static Func<ExportSource?>? ActiveExportSource { get; set; }

    /// <summary>작업이 끝났을 때 알림을 보낼 곳(상태 줄 등). 비어 있으면 무시한다.</summary>
    public static Action<string>? Notify { get; set; }

    /// <summary>스키마 브라우저 등이 표·뷰 내보내기를 열 때 쓴다.</summary>
    public static async Task ShowExportAsync(IServiceProvider services, ExportSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(source);
        if (TransferServices.Resolve(services) is not { } transfer)
        {
            return;
        }

        var vm = new ExportDialogViewModel(transfer, source);
        var window = new ExportDialogWindow { DataContext = vm };
        await TransferServices.ShowAsync(services, window).ConfigureAwait(true);
    }

    internal static ISession? ResolveSession(IServiceProvider services)
    {
        var manager = services.GetService(typeof(ISessionManager)) as ISessionManager;
        var work = manager?.Sessions.Where(s => s.Purpose == SessionPurpose.Work).ToList() ?? [];
        if (work.Count == 0)
        {
            return null;
        }

        var snapshot = (services.GetService(typeof(IEditorContext)) as IEditorContext)?.GetActiveDocument();
        if (snapshot?.SessionId is { } sid && work.FirstOrDefault(s => s.Id == sid) is { } active)
        {
            return active;
        }

        return work[^1];
    }
}

/// <summary>
/// 결과 그리드 「내보내기」(<c>oranda.grid.export</c>)와 도구 「데이터 가져오기」(<c>oranda.tools.import</c>).
/// 셸의 처리기 팩터리(DI)가 <see cref="IServiceProvider"/> 를 넣어 준다. 인자 없는 생성자는 아무것도 하지 않는다.
/// </summary>
[OrandaCommand("oranda.grid.export", CommandCategory.Result, Title = "내보내기", When = "gridFocus", Enablement = "gridHasResult",
    Placements = ["context:grid/export@10"])]
[OrandaCommand("oranda.tools.import", CommandCategory.Tools, Title = "데이터 가져오기(CSV·xlsx)", Enablement = "hasSession && !readOnlySession",
    Placements = ["menu:tools/import@40"])]
public sealed class DataTransferCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public DataTransferCommandHandler()
    {
    }

    public DataTransferCommandHandler(IServiceProvider services) => _services = services;

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is null)
        {
            return;
        }

        switch (context.CommandId)
        {
            case "oranda.grid.export":
                var source = DataTransferCommands.ActiveExportSource?.Invoke() ?? FromEditor(_services);
                if (source is not null)
                {
                    await DataTransferCommands.ShowExportAsync(_services, source, cancellationToken).ConfigureAwait(true);
                }

                break;
            case "oranda.tools.import":
                if (DataTransferCommands.ResolveSession(_services) is { } session && TransferServices.Resolve(_services) is { } transfer)
                {
                    var vm = new ImportDialogViewModel(transfer, TransferServices.ResolveMetadata(_services), session);
                    await TransferServices.ShowAsync(_services, new ImportDialogWindow { DataContext = vm }).ConfigureAwait(true);
                }

                break;
            default:
                break;
        }
    }

    private static ExportSource? FromEditor(IServiceProvider services)
    {
        var editor = services.GetService(typeof(IEditorContext)) as IEditorContext;
        var statement = editor?.GetCurrentStatement();
        var session = DataTransferCommands.ResolveSession(services);
        if (statement is null || session is null)
        {
            return null;
        }

        return new ExportSource(session, statement, new Dictionary<string, BindValue>());
    }
}

/// <summary>서비스 꺼내기와 대화상자 열기(전부 호출 시점에).</summary>
internal static class TransferServices
{
    internal static Oranda.Core.Transfer.IDataTransferService? Resolve(IServiceProvider services) =>
        services.GetService(typeof(Oranda.Core.Transfer.IDataTransferService)) as Oranda.Core.Transfer.IDataTransferService;

    internal static Oranda.Core.Metadata.IMetadataProvider ResolveMetadata(IServiceProvider services) =>
        (Oranda.Core.Metadata.IMetadataProvider)(services.GetService(typeof(Oranda.Core.Metadata.IMetadataProvider))
            ?? throw new InvalidOperationException("메타데이터 서비스를 찾을 수 없다."));

    internal static async Task ShowAsync(IServiceProvider services, Window window)
    {
        var owner = (services.GetService(typeof(ShellController)) as ShellController)?.MainWindow;
        if (owner is { IsVisible: true })
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }
}
