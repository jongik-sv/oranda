using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Transfer;

namespace Oranda.App.Features.DataTransfer;

/// <summary>한 번의 내보내기가 쓸 입력: 범위, 서버에서 다시 읽을 질의, 그리드, 형식별 옵션.</summary>
internal sealed record ExportJob(ExcelExportScope Scope, ExportSource? Source, ResultGridModel? Grid, ExportOptions Options);

/// <summary>내보내기 한 번의 결과. 성공이면 <see cref="Summary"/> 가 있다.</summary>
internal sealed record ExportOutcome(TransferSummary? Summary, string Message, bool IsError);

/// <summary>
/// 내보내기 대화상자의 파일 쓰기(모든 형식 공통): 범위에 따라 서버 재실행 또는 그리드 값을 쓰고, 상태 줄·저장 알림(열기·폴더 열기)으로 알린다.
/// 형식별 쓰기는 <see cref="IDataTransferService"/> 그대로다. 파일 열기는 시험에서 바꿀 수 있게 정적 연결 지점으로 둔다.
/// </summary>
internal static class ExportRunner
{
    /// <summary>파일을 기본 앱으로 여는 곳. 열기를 시작했으면 true.</summary>
    internal static Func<string, bool> OpenFile { get; set; } = FileLauncher.Open;

    /// <summary>
    /// 파일에 쓰고 결과를 알린다. 실패·취소하면 만들다 만 파일을 지운다. 성공하면 폴더·형식·열기 값을 기억하고 <paramref name="openAfter"/> 면 기본 앱으로 연다.
    /// </summary>
    internal static async Task<ExportOutcome> ExportToFileAsync(IDataTransferService transfer, ExportJob job, string path, bool openAfter,
        IProgress<long>? progress, CancellationToken ct)
    {
        var done = false;
        try
        {
            TransferSummary summary;
            await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                summary = await WriteAsync(transfer, job, fs, progress, ct).ConfigureAwait(true);
            }

            done = true;
            ExcelExportPlan.Remember(path, job.Options.Format, openAfter);
            var message = $"{ExportText.Done(summary.Rows, summary.Elapsed.TotalSeconds, path)} {string.Join(' ', summary.Warnings)}".TrimEnd();
            DataTransferCommands.Notify?.Invoke(message);
            GridEdit.SaveFeedback.ShowExported(summary.Rows, path);
            if (openAfter && !OpenFile(path))
            {
                DataTransferCommands.Notify?.Invoke(ExportText.OpenFailed(path));
            }

            return new ExportOutcome(summary, message, false);
        }
        catch (OperationCanceledException)
        {
            DataTransferCommands.Notify?.Invoke(ExportText.Canceled);
            return new ExportOutcome(null, ExportText.Canceled, false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            var message = ExportText.Failed(ex.Message);
            DataTransferCommands.Notify?.Invoke(message);
            GridEdit.SaveFeedback.ShowExportFailed(ex.Message);
            return new ExportOutcome(null, message, true);
        }
        finally
        {
            if (!done)
            {
                TryDelete(path);
            }
        }
    }

    /// <summary>범위에 따라 서버 재실행 또는 그리드 값으로 <paramref name="destination"/> 에 쓴다.</summary>
    internal static Task<TransferSummary> WriteAsync(IDataTransferService transfer, ExportJob job, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        if (job.Scope == ExcelExportScope.AllRows)
        {
            var source = job.Source ?? throw new InvalidOperationException(ExportText.NoQuery);
            return transfer.ExportAsync(source.Session, source.QueryInColumnOrder(), source.Binds, job.Options, destination, progress, ct);
        }

        var grid = job.Grid ?? throw new InvalidOperationException(ExportText.NoGrid);
        var data = ExcelExportPlan.FromGrid(grid, job.Scope);
        return transfer.ExportRowsAsync(data.Columns, data.Rows, job.Options, destination, progress, ct);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 지우지 못해도 원래 결과 알림이 우선이다.
        }
    }
}
