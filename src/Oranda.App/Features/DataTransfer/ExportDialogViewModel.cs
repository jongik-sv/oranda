using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.ViewModels;
using Oranda.Core.Transfer;

namespace Oranda.App.Features.DataTransfer;

/// <summary>내보내기 대화상자(EXP-01): 형식·옵션·저장 경로를 고르고 서버에서 다시 스트리밍해 저장한다.</summary>
public sealed partial class ExportDialogViewModel : ViewModelBase, IDisposable
{
    private readonly IDataTransferService _service;
    private readonly ExportSource _source;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInsert), nameof(IsXlsx), nameof(HasHeaderOption), nameof(SuggestedExtension))]
    private TransferFormat _format = TransferFormat.Csv;

    [ObservableProperty]
    private bool _includeHeader = true;

    [ObservableProperty]
    private string _insertTableName;

    [ObservableProperty]
    private int _maxRowsPerSheet = 1_048_575;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _statusIsError;

    [ObservableProperty]
    private long _rowCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    private bool _isRunning;

    public ExportDialogViewModel(IDataTransferService service, ExportSource source)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(source);
        _service = service;
        _source = source;
        _insertTableName = source.SuggestedTableName ?? "TABLE_NAME";
    }

    public IReadOnlyList<TransferFormat> Formats { get; } = [TransferFormat.Csv, TransferFormat.Tsv, TransferFormat.Xlsx, TransferFormat.Json, TransferFormat.Insert];

    public bool IsInsert => Format == TransferFormat.Insert;

    public bool IsXlsx => Format == TransferFormat.Xlsx;

    public bool HasHeaderOption => Format is TransferFormat.Csv or TransferFormat.Tsv or TransferFormat.Xlsx;

    public string SuggestedExtension => Extension(Format);

    public string SuggestedFileName => (_source.SuggestedFileName ?? "export") + SuggestedExtension;

    public string SuggestedFileNameWithoutExtension => _source.SuggestedFileName ?? "export";

    /// <summary>끝나면(성공) 발생. 창이 닫는다.</summary>
    public event EventHandler? Finished;

    public static string Extension(TransferFormat format) => format switch
    {
        TransferFormat.Csv => ".csv",
        TransferFormat.Tsv => ".tsv",
        TransferFormat.Xlsx => ".xlsx",
        TransferFormat.Json => ".json",
        _ => ".sql",
    };

    public static string Label(TransferFormat format) => format switch
    {
        TransferFormat.Csv => "CSV",
        TransferFormat.Tsv => "TSV",
        TransferFormat.Xlsx => "Excel(xlsx)",
        TransferFormat.Json => "JSON",
        _ => "INSERT 문(.sql)",
    };

    private bool CanStart() => !IsRunning && !string.IsNullOrWhiteSpace(FilePath);

    /// <summary>저장한다. 실패하거나 취소하면 만들다 만 파일은 지운다.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        IsRunning = true;
        StatusIsError = false;
        RowCount = 0;
        Status = "내보내는 중…";
        _cts = new CancellationTokenSource();
        var path = FilePath.Trim();
        var done = false;
        try
        {
            var options = new ExportOptions(Format)
            {
                IncludeHeader = IncludeHeader,
                InsertTableName = IsInsert ? InsertTableName : null,
                MaxRowsPerSheet = Math.Max(1, MaxRowsPerSheet),
            };
            TransferSummary summary;
            await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                summary = await _service.ExportAsync(_source.Session, _source.QueryInColumnOrder(), _source.Binds, options, fs,
                    new Progress<long>(n => RowCount = n), _cts.Token).ConfigureAwait(true);
            }

            done = true;
            RowCount = summary.Rows;
            Status = $"{summary.Rows:N0}행을 저장했다({summary.Elapsed.TotalSeconds:0.0}초). {string.Join(' ', summary.Warnings)}".TrimEnd();
            DataTransferCommands.Notify?.Invoke(Status);
            GridEdit.SaveFeedback.ShowExported(summary.Rows, path);
            Finished?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "취소했다. 저장 중이던 파일은 지웠다.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            StatusIsError = true;
            Status = ex.Message;
            GridEdit.SaveFeedback.ShowExportFailed(ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            if (!done)
            {
                TryDelete(path);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    public void Cancel() => _cts?.Cancel();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 지우지 못해도 원래 오류 표시가 우선이다.
        }
    }

    /// <summary>창이 닫히면 진행 중인 작업을 취소하고 취소 토큰을 정리한다.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
