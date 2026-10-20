using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Catalog;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Oracle.Schema;

namespace Oranda.App.Features.ColumnReorder;

/// <summary>열 순서 목록의 항목 하나(이름·타입·NULL 여부).</summary>
public sealed class ColumnReorderItem
{
    public ColumnReorderItem(ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        Name = column.Name;
        TypeText = FormatType(column);
        NullableText = column.IsNullable ? "NULL" : "NOT NULL";
        IsVirtual = column.IsVirtual;
    }

    public string Name { get; }

    public string TypeText { get; }

    public string NullableText { get; }

    public bool IsVirtual { get; }

    internal static string FormatType(ColumnInfo c) => c.DataType switch
    {
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "RAW" when c.Length is { } n => $"{c.DataType}({n.ToString(CultureInfo.InvariantCulture)})",
        "NUMBER" when c.Precision is { } p => c.Scale is { } s and not 0
            ? $"NUMBER({p.ToString(CultureInfo.InvariantCulture)},{s.ToString(CultureInfo.InvariantCulture)})"
            : $"NUMBER({p.ToString(CultureInfo.InvariantCulture)})",
        _ => c.DataType,
    };
}

/// <summary>경고 목록 한 줄(표를 참조하는 소스의 위험 후보).</summary>
public sealed record ColumnReorderWarning(string Text);

/// <summary>
/// 열 순서 바꾸기 대화상자의 상태(TSK-13-01). 열을 끌거나 단추로 옮겨 새 순서를 정하고,
/// 방식 ①(INVISIBLE→VISIBLE)은 미리보기를 본 뒤 실행 엔진(정책 관문·운영 확인)으로 돌리며,
/// 방식 ②(재생성 스크립트)는 새 편집기 탭에 스크립트만 연다. 서버에 보조 객체를 설치하지 않는다.
/// </summary>
public sealed partial class ColumnReorderViewModel : ObservableObject, IDisposable
{
    /// <summary>소스 경고 목록에 보일 최대 줄 수.</summary>
    internal const int MaxWarnings = 200;

    private readonly IMetadataProvider _meta;
    private readonly IColumnReorderProvider? _reorder;
    private readonly IExecutionService? _exec;
    private ISession? _session;
    private IReadOnlyList<ColumnInfo> _columns = [];
    private IReadOnlyList<string> _original = [];
    private ColumnReorderTraits _traits = new(false, false, null);
    private bool _atLeast12c;
    private bool _enterprise;
    private bool _usePrimaryKey;
    private string? _dependentDdl;
    private bool _dependentDdlLoaded;
    private CancellationTokenSource? _scan;

    public ColumnReorderViewModel(IMetadataProvider meta, IColumnReorderProvider? reorder = null, IExecutionService? exec = null)
    {
        ArgumentNullException.ThrowIfNull(meta);
        _meta = meta;
        _reorder = reorder;
        _exec = exec;
    }

    public DbObjectRef? Table { get; private set; }

    /// <summary>현재 순서의 보이는 열(INVISIBLE 열은 순서를 정하지 않는다).</summary>
    public ObservableCollection<ColumnReorderItem> Items { get; } = [];

    /// <summary>고른 방식을 쓸 수 없는 이유(비어 있으면 쓸 수 있다).</summary>
    public ObservableCollection<string> BlockReasons { get; } = [];

    /// <summary>표를 참조하는 소스의 <c>SELECT *</c>·열 목록 없는 <c>INSERT</c> 후보.</summary>
    public ObservableCollection<ColumnReorderWarning> Warnings { get; } = [];

    /// <summary>시험용: 새 편집기 탭을 여는 방법(제목, 본문). 기본 = 열려 있는 SQL 편집기 작업 공간의 새 문서.</summary>
    internal Action<string, string>? OpenEditor { get; set; }

    /// <summary>시험용·조립용: 서버 DDL 이 끝난 뒤 표 메타데이터 캐시를 비운다(비우지 않으면 예전 열 순서가 캐시에서 나온다).</summary>
    internal Func<ISession, DbObjectRef, CancellationToken, Task>? InvalidateCache { get; set; }

    /// <summary>방식 ① 이 끝났을 때(표 상세를 새로 읽는다).</summary>
    public event EventHandler? Applied;

    [ObservableProperty]
    private ColumnReorderMethod _method = ColumnReorderMethod.InvisibleVisible;

    /// <summary>true 면 방식 ① 선택지가 보인다(11.2 서버에서는 숨긴다).</summary>
    [ObservableProperty]
    private bool _canUseInvisible = true;

    [ObservableProperty]
    private bool _showPreview;

    [ObservableProperty]
    private string _previewText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _warningNote = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>서버가 이 기능을 지원하지 않는다(카탈로그 변형이 없는 서버 버전). 이유는 <see cref="StatusText"/>.</summary>
    [ObservableProperty]
    private bool _isUnsupported;

    public bool IsInvisibleMethod => Method == ColumnReorderMethod.InvisibleVisible;

    public bool IsRebuildMethod => Method == ColumnReorderMethod.Rebuild;

    public bool HasBlockReasons => BlockReasons.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>현재 순서가 처음과 다른가.</summary>
    public bool IsChanged => Items.Select(i => i.Name).SequenceEqual(_original, StringComparer.Ordinal) is false;

    public bool CanPreview => !IsUnsupported && !IsBusy && HasBlockReasons is false && IsChanged;

    /// <summary>방식 ① 만 실행한다. 미리보기를 본 뒤에만 켠다.</summary>
    public bool CanRun => CanPreview && ShowPreview && Method == ColumnReorderMethod.InvisibleVisible && _exec is not null;

    /// <summary>방식 ② 는 스크립트를 새 편집기 탭에 연다.</summary>
    public bool CanOpenScript => CanPreview && Method == ColumnReorderMethod.Rebuild;

    partial void OnMethodChanged(ColumnReorderMethod value)
    {
        OnPropertyChanged(nameof(IsInvisibleMethod));
        OnPropertyChanged(nameof(IsRebuildMethod));
        _ = RefreshAsync();
    }

    partial void OnShowPreviewChanged(bool value) => NotifyActions();

    partial void OnIsBusyChanged(bool value) => NotifyActions();

    partial void OnIsUnsupportedChanged(bool value) => NotifyActions();

    /// <summary>표의 열·표 종류를 읽고, 참조 소스 경고를 한 번 찾는다(취소 가능).</summary>
    public async Task LoadAsync(ISession session, DbObjectRef table, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(table);

        _session = session;
        Table = table;
        IsBusy = true;
        try
        {
            var info = await _meta.GetTableAsync(session, table, ct).ConfigureAwait(true);
            _columns = ColumnOrdering.ByColumnId(info.Columns);
            _usePrimaryKey = info.Constraints.Any(c => c.Kind == ConstraintKind.PrimaryKey);
            _original = [.. _columns.Where(c => !c.IsInvisible).Select(c => c.Name)];
            Items.Clear();
            foreach (var c in _columns.Where(c => !c.IsInvisible))
            {
                Items.Add(new ColumnReorderItem(c));
            }

            _atLeast12c = SafeServer(session)?.IsAtLeast12c ?? false;
            _enterprise = string.Equals(SafeServer(session)?.Edition, "Enterprise", StringComparison.OrdinalIgnoreCase);
            CanUseInvisible = _atLeast12c;
            if (!_atLeast12c)
            {
                Method = ColumnReorderMethod.Rebuild;
            }

            if (_reorder is not null)
            {
                try
                {
                    _traits = await _reorder.GetTraitsAsync(session, table, ct).ConfigureAwait(true);
                }
                catch (CatalogQueryNotFoundException)
                {
                    // 이 서버 버전용 카탈로그가 없다: 앱을 멈추지 않고 이 기능만 쓸 수 없다고 알린다.
                    IsUnsupported = true;
                    StatusText = "이 서버 버전에서는 열 순서 바꾸기를 쓸 수 없다";
                    return;
                }
            }

            await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusText = "표를 읽지 못했다: " + FirstLine(ex.Message);
            IsUnsupported = true;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        _ = ScanSourcesAsync(session, table);
    }

    /// <summary>위로 한 칸. 이미 맨 위면 false.</summary>
    public bool MoveUp(ColumnReorderItem item) => Move(item, -1);

    /// <summary>아래로 한 칸. 이미 맨 아래면 false.</summary>
    public bool MoveDown(ColumnReorderItem item) => Move(item, +1);

    /// <summary>끌어 놓기: <paramref name="item"/> 을 <paramref name="toIndex"/> 위치로 옮긴다.</summary>
    public bool MoveTo(ColumnReorderItem item, int toIndex)
    {
        ArgumentNullException.ThrowIfNull(item);
        var from = Items.IndexOf(item);
        var to = Math.Clamp(toIndex, 0, Items.Count - 1);
        if (from < 0 || from == to)
        {
            return false;
        }

        Items.Move(from, to);
        _ = RefreshAsync();
        return true;
    }

    private bool Move(ColumnReorderItem item, int delta)
    {
        ArgumentNullException.ThrowIfNull(item);
        var from = Items.IndexOf(item);
        return from >= 0 && from + delta >= 0 && from + delta < Items.Count && MoveTo(item, from + delta);
    }

    /// <summary>스크립트 미리보기를 보인다(이후 순서를 바꾸면 따라 바뀐다).</summary>
    public async Task ShowPreviewAsync()
    {
        ShowPreview = true;
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>방식 ① 을 실행 엔진으로 돌린다. 정책 관문(운영 확인 포함)이 막으면 거기서 멈춘다.</summary>
    public async Task<bool> RunAsync(CancellationToken ct = default)
    {
        if (!CanRun || _session is null || Table is null || _exec is null)
        {
            return false;
        }

        var statements = ColumnReorderScripts.InvisibleVisible(Table, _original, CurrentOrder());
        IsBusy = true;
        try
        {
            var done = 0;
            await foreach (var result in _exec.ExecuteScriptAsync(
                _session, statements, ScriptErrorMode.Stop, new ExecutionOptions { Source = HistorySource.SchemaBrowser }, ct).ConfigureAwait(true))
            {
                if (result.ResultSet is { } rs)
                {
                    await rs.DisposeAsync().ConfigureAwait(true);
                }

                if (result.Status != ExecutionStatus.Succeeded)
                {
                    StatusText = $"{done}/{statements.Count} 문장까지 실행하고 멈췄다: " + Describe(result);
                    NotifyActions();
                    return false;
                }

                done++;
                StatusText = $"실행 중 {done}/{statements.Count}";
            }

            if (done < statements.Count)
            {
                StatusText = $"{done}/{statements.Count} 문장까지 실행하고 멈췄다(취소했다)";
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "취소했다";
            return false;
        }
        finally
        {
            IsBusy = false;
        }

        // 서버의 열 순서가 바뀌었으니 처음 순서를 새로 읽는다.
        StatusText = $"열 순서를 바꿨다({statements.Count}문장)";
        ShowPreview = false;
        PreviewText = string.Empty;
        try
        {
            if (InvalidateCache is { } invalidate)
            {
                await invalidate(_session, Table, ct).ConfigureAwait(true);
            }

            await LoadAsync(_session, Table, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }

        StatusText = $"열 순서를 바꿨다({statements.Count}문장)";
        Applied?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>방식 ② 스크립트를 새 편집기 탭에 연다(실행하지 않는다).</summary>
    public async Task<bool> OpenScriptAsync(CancellationToken ct = default)
    {
        if (!CanOpenScript || Table is null)
        {
            return false;
        }

        var script = await BuildRebuildAsync(ct).ConfigureAwait(true);
        var title = $"{Table.Name} 열 순서";
        if (OpenEditor is { } open)
        {
            open(title, script);
        }
        else
        {
            Oranda.App.Features.SqlEditor.SqlEditorHost.Current.NewDocument(script).Title = title;
        }

        StatusText = "스크립트를 새 편집기 탭에 열었다. 검토한 뒤 직접 실행한다";
        return true;
    }

    internal IReadOnlyList<string> CurrentOrder() => [.. Items.Select(i => i.Name)];

    private async Task RefreshAsync()
    {
        BlockReasons.Clear();
        foreach (var r in ColumnReorderScripts.BlockReasons(Method, _columns, _traits, _atLeast12c))
        {
            BlockReasons.Add(r);
        }

        OnPropertyChanged(nameof(HasBlockReasons));
        NotifyActions();
        if (!ShowPreview && Method == ColumnReorderMethod.InvisibleVisible)
        {
            return;
        }

        if (BlockReasons.Count > 0 || !IsChanged || Table is null)
        {
            PreviewText = string.Empty;
            return;
        }

        PreviewText = Method == ColumnReorderMethod.InvisibleVisible
            ? ColumnReorderScripts.ToText(ColumnReorderScripts.InvisibleVisible(Table, _original, CurrentOrder()))
            : await BuildRebuildAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task<string> BuildRebuildAsync(CancellationToken ct)
    {
        await EnsureDependentDdlAsync(ct).ConfigureAwait(true);
        return ColumnReorderScripts.Rebuild(Table!, _columns, CurrentOrder(), _enterprise, _usePrimaryKey, _dependentDdl ?? string.Empty);
    }

    /// <summary>표 DDL 에서 제약·인덱스·트리거·권한·주석 DDL 부분을 한 번만 읽는다.</summary>
    private async Task EnsureDependentDdlAsync(CancellationToken ct)
    {
        if (_dependentDdlLoaded || _session is null || Table is null)
        {
            return;
        }

        try
        {
            var plain = new DdlOptions { SqlTerminator = true };
            var table = await _meta.GetDdlAsync(_session, Table, plain, ct).ConfigureAwait(true);
            var full = await _meta.GetDdlAsync(_session, Table, new DdlOptions { SqlTerminator = true, IncludeDependentObjects = true }, ct)
                .ConfigureAwait(true);
            _dependentDdl = ColumnReorderScripts.DependentPart(table, full);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _dependentDdl = null;
            StatusText = "의존 객체 DDL 을 읽지 못했다: " + FirstLine(ex.Message);
        }

        _dependentDdlLoaded = true;
    }

    private async Task ScanSourcesAsync(ISession session, DbObjectRef table)
    {
        if (_reorder is null)
        {
            return;
        }

        _scan?.Cancel();
        _scan?.Dispose();
        _scan = new CancellationTokenSource();
        var ct = _scan.Token;
        try
        {
            var lines = await _reorder.FindReferencingSourceLinesAsync(session, table, ct).ConfigureAwait(true);
            var hits = ColumnReorderSourceScan.Filter(lines, table);
            Warnings.Clear();
            foreach (var h in hits.Take(MaxWarnings))
            {
                var kind = h.Risk == ColumnReorderRisk.SelectStar ? "SELECT *" : "INSERT(열 목록 없음)";
                Warnings.Add(new ColumnReorderWarning($"{h.Ref.Owner}.{h.Ref.Name} ({h.Ref.ObjectType}) 줄 {h.Line}  [{kind}]  {h.Text}"));
            }

            WarningNote = (hits.Count > MaxWarnings ? $"후보 {hits.Count}줄 중 앞 {MaxWarnings}줄만 보인다. " : string.Empty)
                + (hits.Count == 0 ? "열 순서에 민감한 소스 줄을 찾지 못했다. " : string.Empty)
                + "한 줄 단위 검색이라 여러 줄에 걸친 문장과 뷰·동적 SQL 은 찾지 못한다.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            WarningNote = "소스 경고를 찾지 못했다: " + FirstLine(ex.Message);
        }

        OnPropertyChanged(nameof(HasWarnings));
    }

    /// <summary>대화상자를 닫을 때 진행 중인 소스 검색을 취소한다.</summary>
    public void Dispose()
    {
        _scan?.Cancel();
        _scan?.Dispose();
        _scan = null;
    }

    private void NotifyActions()
    {
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanOpenScript));
    }

    private static Oranda.Core.Connection.ServerInfo? SafeServer(ISession s)
    {
        try
        {
            return s.Server;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string Describe(ExecutionResult r) => r.Status switch
    {
        ExecutionStatus.Denied => r.Policy?.Message ?? "정책 관문이 막았다",
        ExecutionStatus.Cancelled => "취소했다",
        _ => r.Error is { } e ? $"{e.Code} {e.Message}".Trim().ReplaceLineEndings(" ") : "실행하지 못했다",
    };

    private static string FirstLine(string text) => text.ReplaceLineEndings("\n").Split('\n')[0];
}
