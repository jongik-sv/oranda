using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.ResultGrid;
using Oranda.App.ViewModels;
using Oranda.Core.Transfer;

namespace Oranda.App.Features.DataTransfer;

/// <summary>형식 고르기 목록의 한 줄.</summary>
public sealed record ExportFormatOption(TransferFormat Format, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 내보내기 대화상자(EXP-01, D-020): 형식(CSV·TSV·Excel·JSON·INSERT)·범위(전체·불러온 행·가져온 전체·선택)·저장 위치·「내보낸 뒤 열기」를 한 화면에서 고르고 저장한다.
/// 「파일로 내보내기」는 마지막에 쓴 형식으로, 「Excel 로 내보내기」 빠른 길은 Excel 이 골라진 채로 같은 창을 연다.
/// 저장 위치는 처음부터 채워 두므로(마지막 폴더 + 표 이름_시각) 바로 「내보내기」 를 누를 수 있다.
/// </summary>
public sealed partial class ExportDialogViewModel : ViewModelBase, IDisposable
{
    private readonly IDataTransferService _service;
    private readonly ExportSource? _source;
    private readonly ResultGridModel? _grid;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInsert), nameof(IsXlsx), nameof(HasHeaderOption), nameof(SuggestedExtension), nameof(SelectedFormatOption))]
    private TransferFormat _format = TransferFormat.Csv;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllRows), nameof(IsLoadedRows), nameof(IsFetchedAll), nameof(IsSelection), nameof(ScopeHint))]
    private ExcelExportScope _scope;

    [ObservableProperty]
    private bool _includeHeader = true;

    [ObservableProperty]
    private string _insertTableName;

    [ObservableProperty]
    private string _sheetName;

    [ObservableProperty]
    private int _maxRowsPerSheet = 1_048_575;

    [ObservableProperty]
    private bool _openAfter;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(PathError), nameof(HasPathError), nameof(PathWarning), nameof(HasPathWarning))]
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

    /// <summary>서버에서 다시 읽을 질의만으로 연다(그리드 범위는 고를 수 없다). 형식은 CSV, 내보낸 뒤 열지 않는다.</summary>
    public ExportDialogViewModel(IDataTransferService service, ExportSource source)
        : this(service, source ?? throw new ArgumentNullException(nameof(source)), null, TransferFormat.Csv, openAfter: false)
    {
    }

    /// <param name="service">형식별 쓰기.</param>
    /// <param name="source">서버에서 다시 실행할 질의(모르면 null → 「전체 행」 을 고를 수 없다).</param>
    /// <param name="grid">결과 그리드(없으면 범위는 「전체 행」 뿐).</param>
    /// <param name="format">처음 고를 형식(<see cref="ExcelExportPlan.StartFormat"/>).</param>
    /// <param name="openAfter">「내보낸 뒤 열기」 처음 값. null 이면 <see cref="ExcelExportPlan.StartOpenAfter"/>.</param>
    /// <param name="now">기본 파일 이름의 시각(시험용). null 이면 지금.</param>
    public ExportDialogViewModel(IDataTransferService service, ExportSource? source, ResultGridModel? grid, TransferFormat format,
        bool? openAfter = null, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (source is null && grid is not { HasResult: true })
        {
            throw new ArgumentException("내보낼 질의나 결과 그리드가 있어야 한다.", nameof(source));
        }

        _service = service;
        _source = source;
        _grid = grid is { HasResult: true } ? grid : null;

        FormatOptions =
        [
            new(TransferFormat.Csv, "CSV"),
            new(TransferFormat.Tsv, "TSV"),
            new(TransferFormat.Xlsx, "Excel(xlsx)"),
            new(TransferFormat.Json, "JSON"),
            new(TransferFormat.Insert, ExportText.InsertFormat),
        ];

        // 범위: 질의를 알면 전체 행, 앱 안 거르기가 걸려 있으면 보이는 행(눈에 보이는 것이 기본), 질의를 모르면 불러온 행.
        CanAllRows = source is not null;
        HasGrid = _grid is not null;
        var loaded = _grid is null ? 0 : ExcelExportPlan.LoadedRows(_grid);
        var (selRows, selCols) = _grid is null ? (0L, 0) : ExcelExportPlan.SelectionSize(_grid);
        HasFetchedAll = _grid is { IsLocalFiltered: true };
        CanSelection = selRows > 0 && selCols > 0;
        LoadedLabel = HasFetchedAll ? ExportText.ScopeVisible(loaded) : ExportText.ScopeLoaded(loaded);
        FetchedLabel = HasFetchedAll ? ExportText.ScopeFetched(ExcelExportPlan.FetchedRows(_grid!)) : string.Empty;
        SelectionLabel = CanSelection ? ExportText.ScopeSelection(selRows, selCols) : ExportText.ScopeNoSelection;
        AllRowsTip = CanAllRows ? ExportText.HintServer : ExportText.AllRowsUnavailable;
        _scope = !CanAllRows || HasFetchedAll ? ExcelExportScope.LoadedRows : ExcelExportScope.AllRows;

        // 이름: 그리드 열의 기준 표 → 질의가 알려 준 표 → 「조회결과」. 시트 이름도 같다(「Excel 로 내보내기」 와 같은 규칙).
        Title = (_grid is null ? null : ExcelExportPlan.TableNameOf(_grid.Columns)) ?? source?.SuggestedTableName ?? ExportText.DefaultBaseName;
        _sheetName = Title;
        _insertTableName = source?.SuggestedTableName ?? (_grid is null ? null : ExcelExportPlan.TableNameOf(_grid.Columns)) ?? "TABLE_NAME";
        _format = format;
        _openAfter = openAfter ?? ExcelExportPlan.StartOpenAfter(format);
        var baseName = source?.SuggestedFileName ?? Title;
        _filePath = Path.Combine(ExcelExportPlan.DefaultFolder(), ExcelExportPlan.DefaultFileName(baseName, now ?? DateTime.Now, Extension(format)));
    }

    public IReadOnlyList<ExportFormatOption> FormatOptions { get; }

    /// <summary>형식 고르기 칸에 묶는 값.</summary>
    public ExportFormatOption SelectedFormatOption
    {
        get => FormatOptions.First(o => o.Format == Format);
        set
        {
            if (value is not null)
            {
                Format = value.Format;
            }
        }
    }

    /// <summary>파일·시트 이름의 기반(표 이름 또는 「조회결과」).</summary>
    public string Title { get; }

    public bool IsInsert => Format == TransferFormat.Insert;

    public bool IsXlsx => Format == TransferFormat.Xlsx;

    public bool HasHeaderOption => Format is TransferFormat.Csv or TransferFormat.Tsv or TransferFormat.Xlsx;

    public string SuggestedExtension => Extension(Format);

    // ---- 범위 ----

    /// <summary>결과 그리드가 있어 그리드 범위(불러온 행·선택)를 고를 수 있으면 true.</summary>
    public bool HasGrid { get; }

    public bool CanAllRows { get; }

    public bool CanSelection { get; }

    /// <summary>앱 안 거르기가 걸려 「가져온 전체」를 고를 수 있으면 true(그때만 화면에 보인다).</summary>
    public bool HasFetchedAll { get; }

    public string LoadedLabel { get; }

    public string FetchedLabel { get; }

    public string SelectionLabel { get; }

    /// <summary>「전체 행」 단추의 도움말(쓸 수 없으면 그 이유).</summary>
    public string AllRowsTip { get; }

    /// <summary>고른 범위가 무엇을 읽는지 한 줄 설명.</summary>
    public string ScopeHint => Scope == ExcelExportScope.AllRows ? ExportText.HintServer : ExportText.HintGrid;

    public bool IsAllRows
    {
        get => Scope == ExcelExportScope.AllRows;
        set => Pick(value, ExcelExportScope.AllRows);
    }

    public bool IsLoadedRows
    {
        get => Scope == ExcelExportScope.LoadedRows;
        set => Pick(value, ExcelExportScope.LoadedRows);
    }

    public bool IsFetchedAll
    {
        get => Scope == ExcelExportScope.FetchedAll;
        set => Pick(value, ExcelExportScope.FetchedAll);
    }

    public bool IsSelection
    {
        get => Scope == ExcelExportScope.Selection;
        set => Pick(value, ExcelExportScope.Selection);
    }

    // ---- 저장 위치 ----

    /// <summary>저장할 수 없는 경로면 그 이유, 괜찮으면 null.</summary>
    public string? PathError => ValidatePath(FilePath, Directory.Exists).Error;

    public bool HasPathError => PathError is not null;

    /// <summary>저장은 되지만 알려야 할 것(같은 이름의 파일을 덮어씀).</summary>
    public string? PathWarning => PathError is null && File.Exists(FilePath.Trim()) ? ExportText.PathExists : null;

    public bool HasPathWarning => PathWarning is not null;

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
        _ => ExportText.InsertFormat,
    };

    /// <summary>
    /// 저장 경로 검사: 비었는지, 폴더부터 적은 전체 경로인지, 파일 이름에 못 쓰는 글자가 없는지, 폴더가 있는지, 폴더 자체가 아닌지.
    /// </summary>
    public static (string? Error, string? Folder) ValidatePath(string? path, Func<string, bool> folderExists)
    {
        ArgumentNullException.ThrowIfNull(folderExists);
        var p = path?.Trim() ?? string.Empty;
        if (p.Length == 0)
        {
            return (ExportText.PathEmpty, null);
        }

        if (!Path.IsPathFullyQualified(p))
        {
            return (ExportText.PathNotRooted, null);
        }

        var name = Path.GetFileName(p);
        if (name.Length == 0 || folderExists(p))
        {
            return (ExportText.PathIsFolder, null);
        }

        // 다른 운영체제(Windows)에서 열 파일일 수 있으므로 어디서나 못 쓰는 글자를 모두 막는다.
        if (name.IndexOfAny([.. Path.GetInvalidFileNameChars(), .. "<>:\"/\\|?*"]) >= 0 || p.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return (ExportText.PathInvalid, null);
        }

        var folder = Path.GetDirectoryName(p);
        if (string.IsNullOrEmpty(folder) || !folderExists(folder))
        {
            return (ExportText.PathNoFolder(folder ?? p), folder);
        }

        return (null, folder);
    }

    /// <summary>형식을 바꾸면 경로 끝의 확장자도 그 형식으로 바꾼다(사용자가 다른 확장자를 적었으면 그대로).</summary>
    partial void OnFormatChanged(TransferFormat oldValue, TransferFormat newValue)
    {
        var path = FilePath;
        var old = Extension(oldValue);
        if (path.EndsWith(old, StringComparison.OrdinalIgnoreCase))
        {
            FilePath = path[..^old.Length] + Extension(newValue);
        }
    }

    private bool CanStart() => !IsRunning && PathError is null;

    /// <summary>저장한다. 실패하거나 취소하면 만들다 만 파일은 지운다.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        IsRunning = true;
        StatusIsError = false;
        RowCount = 0;
        Status = ExportText.Running(0);
        _cts = new CancellationTokenSource();
        try
        {
            var options = new ExportOptions(Format)
            {
                IncludeHeader = IncludeHeader,
                InsertTableName = IsInsert ? InsertTableName : null,
                MaxRowsPerSheet = Math.Max(1, MaxRowsPerSheet),
                SheetName = IsXlsx && !string.IsNullOrWhiteSpace(SheetName) ? SheetName.Trim() : null,
            };
            var job = new ExportJob(Scope, _source, _grid, options);
            var progress = new Progress<long>(n =>
            {
                RowCount = n;
                if (IsRunning)
                {
                    Status = ExportText.Running(n);
                }
            });
            var outcome = await ExportRunner.ExportToFileAsync(_service, job, FilePath.Trim(), OpenAfter, progress, _cts.Token).ConfigureAwait(true);
            StatusIsError = outcome.IsError;
            Status = outcome.Message;
            if (outcome.Summary is { } summary)
            {
                RowCount = summary.Rows;
                Finished?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    public void Cancel() => _cts?.Cancel();

    private void Pick(bool selected, ExcelExportScope scope)
    {
        if (selected)
        {
            Scope = scope;
        }
    }

    /// <summary>창이 닫히면 진행 중인 작업을 취소하고 취소 토큰을 정리한다.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
