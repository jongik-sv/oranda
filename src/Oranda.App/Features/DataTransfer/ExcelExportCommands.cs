using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell;
using Oranda.Core.Shell;
using Oranda.Core.Transfer;

namespace Oranda.App.Features.DataTransfer;

/// <summary>한 번의 「Excel 로 내보내기」가 쓸 입력: 범위, 서버에서 다시 읽을 질의, 그리드, 제목(파일·시트 이름의 기반).</summary>
internal sealed record ExcelExportJob(ExcelExportScope Scope, ExportSource? Source, ResultGridModel? Grid, string Title);

/// <summary>
/// 「Excel 로 내보내기」 빠른 흐름(<c>oranda.grid.exportExcel</c>): 범위 고르기 → 저장 위치 → xlsx 쓰기 → 「열기」.
/// 저장 위치 선택과 파일 열기는 시험에서 바꿀 수 있게 정적 연결 지점으로 둔다.
/// </summary>
public static class ExcelExportRunner
{
    /// <summary>저장 위치를 고르는 곳(시작 폴더, 기본 파일 이름 → 고른 경로, 취소하면 null). 비어 있으면 운영체제 저장 대화상자.</summary>
    internal static Func<IServiceProvider, string, string, Task<string?>>? PickSavePath { get; set; }

    /// <summary>파일을 기본 앱으로 여는 곳. 열기를 시작했으면 true.</summary>
    internal static Func<string, bool> OpenFile { get; set; } = FileLauncher.Open;

    internal static async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (TransferServices.Resolve(services) is not { } transfer || ResultGridCommands.Active is not { HasResult: true } grid)
        {
            DataTransferCommands.Notify?.Invoke("내보낼 결과가 없습니다.");
            return;
        }

        var source = DataTransferCommands.ActiveExportSource?.Invoke() ?? DataTransferCommandHandler.FromEditor(services);
        var (selRows, selCols) = ExcelExportPlan.SelectionSize(grid);
        var vm = new ExcelExportDialogViewModel(source is not null, ExcelExportPlan.LoadedRows(grid), selRows, selCols);
        await TransferServices.ShowAsync(services, new ExcelExportDialogWindow { DataContext = vm }).ConfigureAwait(true);
        if (!vm.Accepted)
        {
            return;
        }

        var title = ExcelExportPlan.TableNameOf(grid.Columns) ?? source?.SuggestedTableName ?? ExcelExportPlan.DefaultBaseName;
        var name = ExcelExportPlan.DefaultFileName(title, DateTime.Now);
        var path = await (PickSavePath ?? PickWithDialogAsync)(services, ExcelExportPlan.DefaultFolder(), name).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var job = new ExcelExportJob(vm.Scope, source, grid, title);
        if (ResultGridCommands.RunFetch is { } run)
        {
            // 실행 상태로 돌려 취소 명령(Ctrl+Break)이 켜지게 한다.
            await run(token => ExportToFileAsync(transfer, job, path, vm.OpenAfter, token), ct).ConfigureAwait(true);
        }
        else
        {
            await ExportToFileAsync(transfer, job, path, vm.OpenAfter, ct).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 파일에 쓰고 결과를 알린다. 실패·취소하면 만들다 만 파일을 지운다. 성공하면 폴더를 기억하고 <paramref name="openAfter"/> 면 기본 앱으로 연다.
    /// </summary>
    internal static async Task<TransferSummary?> ExportToFileAsync(IDataTransferService transfer, ExcelExportJob job, string path, bool openAfter, CancellationToken ct)
    {
        var done = false;
        try
        {
            TransferSummary summary;
            await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                summary = await WriteAsync(transfer, job, fs, ct).ConfigureAwait(true);
            }

            done = true;
            ExcelExportPlan.LastFolder = Path.GetDirectoryName(Path.GetFullPath(path));
            var message = $"{summary.Rows:N0} 행을 Excel 로 내보냄 ({path}) {string.Join(' ', summary.Warnings)}".TrimEnd();
            DataTransferCommands.Notify?.Invoke(message);
            GridEdit.SaveFeedback.ShowExported(summary.Rows, path);
            if (openAfter && !OpenFile(path))
            {
                DataTransferCommands.Notify?.Invoke($"파일을 기본 앱으로 열지 못했습니다: {path}");
            }

            return summary;
        }
        catch (OperationCanceledException)
        {
            DataTransferCommands.Notify?.Invoke("Excel 내보내기를 취소했습니다. 저장 중이던 파일은 지웠습니다.");
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DataTransferCommands.Notify?.Invoke($"Excel 내보내기에 실패했습니다: {ex.Message}");
            GridEdit.SaveFeedback.ShowExportFailed(ex.Message);
            return null;
        }
        finally
        {
            if (!done)
            {
                TryDelete(path);
            }
        }
    }

    /// <summary>범위에 따라 서버 재실행 또는 그리드 값으로 xlsx 를 <paramref name="destination"/> 에 쓴다.</summary>
    internal static Task<TransferSummary> WriteAsync(IDataTransferService transfer, ExcelExportJob job, Stream destination, CancellationToken ct)
    {
        var options = new ExportOptions(TransferFormat.Xlsx) { IncludeHeader = true, SheetName = job.Title };
        if (job.Scope == ExcelExportScope.AllRows)
        {
            var source = job.Source ?? throw new InvalidOperationException("다시 실행할 질의를 알 수 없습니다.");
            return transfer.ExportAsync(source.Session, source.Query, source.Binds, options, destination, null, ct);
        }

        var grid = job.Grid ?? throw new InvalidOperationException("내보낼 결과 그리드가 없습니다.");
        var data = ExcelExportPlan.FromGrid(grid, job.Scope);
        return transfer.ExportRowsAsync(data.Columns, data.Rows, options, destination, null, ct);
    }

    private static async Task<string?> PickWithDialogAsync(IServiceProvider services, string folder, string name)
    {
        var owner = (services.GetService(typeof(ShellController)) as ShellController)?.MainWindow;
        if (owner is null)
        {
            return null;
        }

        var storage = owner.StorageProvider;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Excel 로 내보내기",
            SuggestedFileName = name,
            DefaultExtension = "xlsx",
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(folder).ConfigureAwait(true),
            FileTypeChoices = [new FilePickerFileType("Excel(xlsx)") { Patterns = ["*.xlsx"] }],
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 지우지 못해도 원래 결과 알림이 우선이다.
        }
    }
}

/// <summary>
/// 결과 그리드 「Excel 로 내보내기」(<c>oranda.grid.exportExcel</c>). 결과 메뉴·그리드 오른쪽 클릭 메뉴·명령 팔레트에 놓인다.
/// 셸의 처리기 팩터리(DI)가 <see cref="IServiceProvider"/> 를 넣어 준다. 인자 없는 생성자는 아무것도 하지 않는다.
/// <para>
/// 배치 순서: 결과 메뉴는 오른쪽 클릭 배치를 거울처럼 옮긴 묶음(100 단위, 0부터)이라 이 명령만 맨 위 묶음이 되려면 음수 순서(-100)가 필요하다.
/// 오른쪽 클릭 메뉴는 기존 「내보내기」와 같은 묶음·같은 순서 값이고, 같은 값이면 제목순(「Excel…」이 「내보내기」 앞)으로 놓여 내보내기 묶음이 맨 아래에 남는다.
/// </para>
/// </summary>
[OrandaCommand("oranda.grid.exportExcel", CommandCategory.Result, Title = "Excel 로 내보내기…", When = "gridFocus", Enablement = "gridHasResult",
    Placements = ["menu:result/exportExcel@-100", "context:grid/export@10"])]
public sealed class ExcelExportCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public ExcelExportCommandHandler()
    {
    }

    public ExcelExportCommandHandler(IServiceProvider services) => _services = services;

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is not null)
        {
            await ExcelExportRunner.RunAsync(_services, cancellationToken).ConfigureAwait(true);
        }
    }
}
