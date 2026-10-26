using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.ViewModels;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer;
using Oranda.Oracle.Transfer;

namespace Oranda.App.Features.DataTransfer;

/// <summary>열 매핑 한 줄: 원본 열 → 대상 열, 그리고 첫 데이터 행을 그 열 타입으로 바꾼 미리보기.</summary>
public sealed partial class ImportMappingRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSkipped))]
    private string? _target;

    [ObservableProperty]
    private string _preview = string.Empty;

    [ObservableProperty]
    private bool _previewIsError;

    public ImportMappingRow(string source, string? sample)
    {
        Source = source;
        Sample = sample;
    }

    public string Source { get; }

    /// <summary>원본 첫 데이터 행의 값(글자).</summary>
    public string? Sample { get; }

    public bool IsSkipped => string.IsNullOrEmpty(Target);
}

/// <summary>가져오기 대화상자(EXP-02): 파일 고르기 → 대상 표 → 열 매핑·타입 변환 미리보기 → 배치 크기 → 실행. 오류 행은 따로 보여 준다.</summary>
public sealed partial class ImportDialogViewModel : ViewModelBase, IDisposable
{
    /// <summary>미리보기에서 보여 줄 데이터 행 수(헤더 제외).</summary>
    public const int PreviewRowCount = 5;

    private readonly IDataTransferService _service;
    private readonly IMetadataProvider _metadata;
    private readonly ISession _session;
    private TableInfo? _table;
    private IReadOnlyList<object?[]> _previewRows = [];
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private TransferFormat _format = TransferFormat.Csv;

    [ObservableProperty]
    private bool _hasHeader = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private string _targetOwner;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private string _targetTable = string.Empty;

    [ObservableProperty]
    private int _batchSize = 500;

    [ObservableProperty]
    private bool _commitAtEnd = true;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _statusIsError;

    [ObservableProperty]
    private long _insertedRows;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isReady;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(LoadCommand), nameof(CancelCommand))]
    private bool _isRunning;

    public ImportDialogViewModel(IDataTransferService service, IMetadataProvider metadata, ISession session)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(session);
        _service = service;
        _metadata = metadata;
        _session = session;
        _targetOwner = session.Profile.User.ToUpperInvariant();
    }

    /// <summary>
    /// 가져오는 동안 세션을 「실행 중」 으로 둘 실행 상태(TSK-14-08: 세션 창의 커밋·롤백·닫기가 꺼지고, 같은 세션을 쓰는 다른 작업 공간은 바쁨 막대를 본다).
    /// null 이면 표시 없이 가져온다.
    /// </summary>
    public Oranda.App.Features.Execution.ExecutionState? State { get; init; }

    /// <summary>가져오기를 시작한 작업 공간(실행 중 표시의 주인). 모르면 null.</summary>
    public object? Owner { get; init; }

    public ObservableCollection<ImportMappingRow> Mappings { get; } = [];

    /// <summary>대상 열 이름 목록(「건너뛰기」는 목록에 없고 빈 값으로 둔다).</summary>
    public ObservableCollection<string> TargetColumns { get; } = [];

    /// <summary>원본 첫 몇 행(헤더 포함) 글자 표시.</summary>
    public ObservableCollection<string> PreviewLines { get; } = [];

    /// <summary>오류 행 보고(<c>행 N: 이유</c>).</summary>
    public ObservableCollection<string> ErrorRows { get; } = [];

    public bool HasErrors => ErrorRows.Count > 0;

    /// <summary>파일 확장자로 형식을 정한다. 알 수 없으면 false.</summary>
    public static bool TryDetectFormat(string path, out TransferFormat format)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".csv":
                format = TransferFormat.Csv;
                return true;
            case ".tsv" or ".txt":
                format = TransferFormat.Tsv;
                return true;
            case ".xlsx":
                format = TransferFormat.Xlsx;
                return true;
            default:
                format = TransferFormat.Csv;
                return false;
        }
    }

    private bool CanLoad() => !IsRunning && !string.IsNullOrWhiteSpace(FilePath) && !string.IsNullOrWhiteSpace(TargetTable) && !string.IsNullOrWhiteSpace(TargetOwner);

    /// <summary>파일 앞부분과 대상 표 열 정보를 읽어 매핑을 자동으로 맞추고 타입 변환 미리보기를 만든다.</summary>
    [RelayCommand(CanExecute = nameof(CanLoad))]
    public async Task LoadAsync()
    {
        IsReady = false;
        StatusIsError = false;
        try
        {
            if (TryDetectFormat(FilePath, out var detected))
            {
                Format = detected;
            }

            _table = await _metadata.GetTableAsync(_session, new DbObjectRef(TargetOwner.Trim().ToUpperInvariant(), TargetTable.Trim().ToUpperInvariant(), "TABLE"), CancellationToken.None).ConfigureAwait(true);
            await using (var fs = new FileStream(FilePath.Trim(), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            {
                _previewRows = TransferFileReader.Preview(Format, fs, PreviewRowCount + 1);
            }

            Rebuild();
            Status = Mappings.Count == 0 ? "파일에 읽을 행이 없다." : $"원본 열 {Mappings.Count}개를 읽었다. 대상 열을 확인하고 「가져오기」를 누른다.";
            IsReady = Mappings.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or InvalidOperationException or System.Xml.XmlException)
        {
            StatusIsError = true;
            Status = ex.Message;
        }
    }

    /// <summary>머리글 사용 여부를 바꾼 뒤 매핑을 다시 만든다.</summary>
    public void Rebuild()
    {
        Mappings.Clear();
        TargetColumns.Clear();
        PreviewLines.Clear();
        if (_table is null || _previewRows.Count == 0)
        {
            return;
        }

        foreach (var c in _table.Columns.Where(c => !c.IsVirtual))
        {
            TargetColumns.Add(c.Name);
        }

        var header = _previewRows[0];
        var data = HasHeader ? _previewRows.Skip(1).ToList() : _previewRows.ToList();
        var width = _previewRows.Max(r => r.Length);
        for (var i = 0; i < width; i++)
        {
            var name = HasHeader && i < header.Length && header[i] is string { Length: > 0 } h ? h.Trim() : (HasHeader ? "COL" + (i + 1).ToString(CultureInfo.InvariantCulture) : (i + 1).ToString(CultureInfo.InvariantCulture));
            var sample = data.Count > 0 && i < data[0].Length ? Text(data[0][i]) : null;
            var row = new ImportMappingRow(name, sample)
            {
                Target = TargetColumns.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)),
            };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ImportMappingRow.Target))
                {
                    UpdatePreview(row, i0: Mappings.IndexOf(row));
                    StartCommand.NotifyCanExecuteChanged();
                }
            };
            Mappings.Add(row);
            UpdatePreview(row, i);
        }

        foreach (var r in _previewRows.Take(PreviewRowCount + 1))
        {
            PreviewLines.Add(string.Join(" | ", r.Select(Text)));
        }

        IsReady = Mappings.Any(m => !m.IsSkipped);
    }

    partial void OnHasHeaderChanged(bool value) => Rebuild();

    private void UpdatePreview(ImportMappingRow row, int i0)
    {
        if (row.IsSkipped || _table is null)
        {
            row.Preview = "(건너뜀)";
            row.PreviewIsError = false;
            return;
        }

        var column = _table.Columns.First(c => string.Equals(c.Name, row.Target, StringComparison.OrdinalIgnoreCase));
        var data = HasHeader ? _previewRows.Skip(1) : _previewRows;
        var errors = 0;
        string? firstError = null;
        string? firstOk = null;
        foreach (var r in data.Take(PreviewRowCount))
        {
            var cell = i0 < r.Length ? r[i0] : null;
            if (ImportValueConverter.TryConvert(column, cell, out var literal, out var error))
            {
                firstOk ??= literal;
            }
            else
            {
                errors++;
                firstError ??= error;
            }
        }

        row.PreviewIsError = errors > 0;
        row.Preview = errors > 0 ? $"오류 {errors}건: {firstError}" : $"{column.DataType} → {firstOk ?? "NULL"}";
    }

    private static string Text(object? v) => v switch
    {
        null => string.Empty,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private bool CanStart() => IsReady && !IsRunning && Mappings.Any(m => !m.IsSkipped);

    /// <summary>가져온다. 정책 관문이 막으면(읽기 전용 접속 등) 오류로 보여 주고 아무것도 넣지 않는다.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        if (_table is null)
        {
            return;
        }

        IsRunning = true;
        StatusIsError = false;
        ErrorRows.Clear();
        OnPropertyChanged(nameof(HasErrors));
        InsertedRows = 0;
        Status = "가져오는 중…";
        _cts = new CancellationTokenSource();
        State?.BeginRun(_session.Id, Owner);
        try
        {
            var mappings = Mappings.Select(m => new ColumnMapping(m.Source, m.IsSkipped ? null : m.Target)).ToList();
            var options = new ImportOptions(Format, _table.Table, mappings)
            {
                HasHeader = HasHeader,
                BatchSize = Math.Max(1, BatchSize),
                CommitAtEnd = CommitAtEnd,
            };
            await using var fs = new FileStream(FilePath.Trim(), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var summary = await _service.ImportAsync(_session, options, fs, new Progress<long>(n => InsertedRows = n), _cts.Token).ConfigureAwait(true);
            InsertedRows = summary.Rows;
            foreach (var w in summary.Warnings)
            {
                ErrorRows.Add(w);
            }

            OnPropertyChanged(nameof(HasErrors));
            StatusIsError = summary.Warnings.Count > 0;
            Status = $"{summary.Rows:N0}행을 넣었다({summary.Elapsed.TotalSeconds:0.0}초)." + (summary.Warnings.Count > 0 ? $" 오류 행 {summary.Warnings.Count}건은 아래에 따로 적었다." : string.Empty)
                + (CommitAtEnd ? " 커밋했다." : " 배치마다 커밋했다.");
            DataTransferCommands.Notify?.Invoke(Status);
        }
        catch (OperationCanceledException)
        {
            Status = CommitAtEnd ? "취소했다. 아무것도 넣지 않았다(롤백)." : "취소했다. 이미 커밋한 배치는 남아 있다.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or NotSupportedException)
        {
            StatusIsError = true;
            Status = ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            State?.ApplyTransaction(_session.Id, _session.HasPendingTransaction);
            State?.EndRun(_session.Id);
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    public void Cancel() => _cts?.Cancel();

    /// <summary>창이 닫히면 진행 중인 작업을 취소하고 취소 토큰을 정리한다.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
