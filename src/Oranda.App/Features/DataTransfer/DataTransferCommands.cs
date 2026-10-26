using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.SqlEditor;
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

    /// <summary>
    /// 그리드의 열 정보(TSK-13-17). <see cref="ColumnOrder"/> 와 함께 있으면 서버에서 다시 읽는 내보내기도 그리드의 화면 열 순서를 따른다.
    /// </summary>
    public IReadOnlyList<Oranda.Core.Grid.ResultColumn>? Columns { get; init; }

    /// <summary>그리드의 화면 열 순서(열 ordinal 나열). 없으면 질의 순서 그대로 내보낸다.</summary>
    public IReadOnlyList<int>? ColumnOrder { get; init; }

    /// <summary>
    /// 바인드 값을 정하지 못한 이유(값이 없거나 형식이 틀림). 있으면 서버에서 다시 읽는 내보내기(전체 행)는 서버를 부르기 전에
    /// 이 이유로 멈춘다 — 서버의 「바인드 안 됨」 오류 대신 무엇을 하면 되는지 보인다.
    /// </summary>
    public string? BindProblem { get; init; }

    /// <summary>
    /// 서버에서 읽을 질의. 열 순서를 옮겼고 감쌀 수 있으면 원래 문장을 인라인 뷰로 감싸 그 순서로 고른 문장, 아니면 원래 문장이다
    /// (열 이름이 겹치거나 FOR UPDATE 가 있으면 원래 순서로 저장한다).
    /// </summary>
    public SqlStatement QueryInColumnOrder() =>
        Columns is { } cols && ColumnOrder is { } order
        && ResultGrid.ResultQueryWrapper.Reorder(Query.Text, cols, order) is { } wrapped
            ? Query with { Text = wrapped, Range = default }
            : Query;
}

/// <summary>
/// TSK-06-07 내보내기·가져오기 — 명령 처리기와 화면 연결 지점(commands.md 「결과」·「도구」).
/// 서비스는 모두 처음 쓸 때 <see cref="IServiceProvider"/> 에서 꺼낸다(D-006: 첫 프레임 전에 Oracle 어셈블리를 올리지 않는다).
/// </summary>
public static class DataTransferCommands
{
    /// <summary>
    /// 결과 그리드 내보내기가 다시 실행할 질의를 알려 주는 연결 지점. 결과 영역(TSK-06-02·06-03)이 채운다.
    /// 비어 있거나 null 을 돌려주면 편집기에서 커서가 놓인 문장을 쓴다.
    /// </summary>
    public static Func<ExportSource?>? ActiveExportSource { get; set; }

    /// <summary>작업이 끝났을 때 알림을 보낼 곳(상태 줄 등). 비어 있으면 무시한다.</summary>
    public static Action<string>? Notify { get; set; }

    /// <summary>스키마 브라우저 등이 표·뷰 내보내기를 열 때 쓴다(질의만, 마지막에 쓴 형식).</summary>
    public static Task ShowExportAsync(IServiceProvider services, ExportSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ShowExportAsync(services, source, null, ExcelExportPlan.StartFormat(excel: false), ct);
    }

    /// <summary>
    /// 결과 내보내기 대화상자 하나(D-020)를 연다. <paramref name="excel"/> 이면 「Excel 로 내보내기」 빠른 길(Excel 골라짐),
    /// 아니면 「파일로 내보내기」(마지막에 쓴 형식, 처음 CSV). 질의도 결과 그리드도 없으면 상태 줄에 알리고 끝난다.
    /// </summary>
    internal static Task ShowResultExportAsync(IServiceProvider services, bool excel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        var source = ActiveExportSource?.Invoke() ?? DataTransferCommandHandler.FromEditor(services);
        var grid = ResultGrid.ResultGridCommands.Active is { HasResult: true } g ? g : null;
        if (source is null && grid is null)
        {
            Notify?.Invoke(ExportText.NoResult);
            return Task.CompletedTask;
        }

        return ShowExportAsync(services, source, grid, ExcelExportPlan.StartFormat(excel), ct);
    }

    private static async Task ShowExportAsync(IServiceProvider services, ExportSource? source, ResultGrid.ResultGridModel? grid, Oranda.Core.Transfer.TransferFormat format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (TransferServices.Resolve(services) is not { } transfer)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        var vm = new ExportDialogViewModel(transfer, source, grid, format);
        var window = new ExportDialogWindow { DataContext = vm };
        await TransferServices.ShowAsync(services, window).ConfigureAwait(true);
    }

    internal static ISession? ResolveSession(IServiceProvider services)
    {
        var manager = services.GetService(typeof(ISessionManager)) as ISessionManager;
        var snapshot = (services.GetService(typeof(IEditorContext)) as IEditorContext)?.GetActiveDocument();
        if (snapshot?.SessionId is { } sid
            && manager?.Sessions.FirstOrDefault(s => s.Purpose == SessionPurpose.Work && s.Id == sid) is { } active)
        {
            return active;
        }

        // 문서가 세션을 알리지 못하면 활성 SQL 탭의 세션(탭이 없으면 지금 접속). 마지막 작업 세션을 짐작하지 않는다.
        return SessionRouter.ForTab(services, SqlEditorHost.Current.Active);
    }
}

/// <summary>
/// 결과 그리드 「내보내기」(<c>oranda.grid.export</c>, 하나로 합친 내보내기 대화상자를 마지막에 쓴 형식으로 연다)와 도구 「데이터 가져오기」(<c>oranda.tools.import</c>).
/// 셸의 처리기 팩터리(DI)가 <see cref="IServiceProvider"/> 를 넣어 준다. 인자 없는 생성자는 아무것도 하지 않는다.
/// </summary>
[OrandaCommand("oranda.grid.export", CommandCategory.Result, Title = "파일로 내보내기…", When = "gridFocus", Enablement = "gridHasResult",
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
                await DataTransferCommands.ShowResultExportAsync(_services, excel: false, cancellationToken).ConfigureAwait(true);
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

    internal static ExportSource? FromEditor(IServiceProvider services)
    {
        var editor = services.GetService(typeof(IEditorContext)) as IEditorContext;
        var session = DataTransferCommands.ResolveSession(services);
        return Build(Oranda.App.Features.SqlEditor.SqlEditorHost.Current.Active, editor?.GetCurrentStatement(), session);
    }

    /// <summary>
    /// 활성 SQL 탭에서 내보내기 대상을 만든다. 결과를 내보낼 때는 지금 보고 있는 결과 탭의 문장을 쓰고(커서가 문장 밖에 있어도 된다),
    /// 없으면 <paramref name="caretStatement"/>(커서 위치 문장)를 쓴다. 문장이나 세션을 모르면 null.
    /// </summary>
    internal static ExportSource? Build(SqlEditorViewModel? tab, SqlStatement? caretStatement, ISession? session)
    {
        var shown = tab?.ResultPane is Oranda.App.Features.Execution.ResultTabsViewModel { Selected.Statement: { } st } ? st : null;
        var statement = shown ?? caretStatement;
        if (statement is null || session is null)
        {
            return null;
        }

        // 바인드: 보고 있는 결과 탭이 실행 때 쓴 값(+셀 값 바인드·따라가기 값) → 탭의 바인드 목록. 둘 다 없으면 이유를 남긴다.
        var pane = shown is not null && tab?.ResultPane is Oranda.App.Features.Execution.ResultTabsViewModel { Selected.Pane: { } p } ? p : null;
        var bound = ExportBinds.Resolve(statement, pane?.RerunBinds(), tab?.Binds);

        // 포커스 있는 결과 그리드가 있으면 그 화면 열 순서를 따른다(열 이름이 겹치는 결과는 감싸지 못해 원래 순서로 저장).
        var grid = ResultGrid.ResultGridCommands.Active;
        return new ExportSource(session, statement, bound.Binds)
        {
            BindProblem = bound.Problem,
            Columns = grid is { HasResult: true } ? grid.Columns : null,
            ColumnOrder = grid is { HasResult: true } ? grid.ColumnsInDisplayOrder() : null,
        };
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
