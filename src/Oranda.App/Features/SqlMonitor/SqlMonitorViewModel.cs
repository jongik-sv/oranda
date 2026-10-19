using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>Orange 식 사전 정의 구간(SQLM-01). 이름과 조건 값 묶음.</summary>
public sealed record SqlRangePreset(string Name, double? MinElapsedSeconds, long? MinBlocks, long? MinExecutions, TopSqlOrder Order)
{
    public override string ToString() => Name;
}

/// <summary>목록 한 줄.</summary>
public sealed class TopSqlItem(TopSqlRow row)
{
    public TopSqlRow Row { get; } = row;

    public string SqlId => Row.Cursor.SqlId;

    public string ChildText => Row.Cursor.ChildNumber?.ToString(CultureInfo.InvariantCulture) ?? "-";

    public string ElapsedText => Row.ElapsedSeconds.ToString("0.00", CultureInfo.InvariantCulture);

    public string CpuText => Row.CpuSeconds.ToString("0.00", CultureInfo.InvariantCulture);

    public string ExecutionsText => Row.Executions.ToString("N0", CultureInfo.InvariantCulture);

    public string BlocksText => Row.BufferGets.ToString("N0", CultureInfo.InvariantCulture);

    public string DiskText => Row.DiskReads.ToString("N0", CultureInfo.InvariantCulture);

    public string Schema => Row.ParsingSchema ?? string.Empty;

    public string Preview => Row.SqlTextPreview;
}

/// <summary>
/// <c>oranda.panel.sqlMonitor</c> — Top SQL 조건 검색(소요 시간·읽은 블록·실행 횟수·텍스트, 사전 정의 구간)과 선택 SQL 의 전문·실제 계획·바인드 캡처 (TSK-09-02, SQLM-01·02).
/// <para>전용 모니터 세션(CONN-07)에서만 조회한다. 팩이 필요한 ASH·AWR 는 쓰지 않는다. 서비스는 처음 쓸 때 꺼낸다(D-006).</para>
/// </summary>
[OrandaToolPanel("oranda.panel.sqlMonitor", ToolPanelKind.Document, DockLocation.Document)]
public sealed partial class SqlMonitorViewModel : ToolPanelViewModelBase, IDisposable
{
    /// <summary>사용자 지정 구간(값을 직접 넣음).</summary>
    public static readonly SqlRangePreset Custom = new("사용자 지정", null, null, null, TopSqlOrder.ElapsedTime);

    /// <summary>사전 정의 구간.</summary>
    public static readonly IReadOnlyList<SqlRangePreset> Presets =
    [
        Custom,
        new("느린 SQL (1 s 이상, 1만 블록 이상)", 1, 10_000, null, TopSqlOrder.ElapsedTime),
        new("Critical (10 s 이상)", 10, 100_000, null, TopSqlOrder.ElapsedTime),
        new("OLTP (100회 이상 실행, 실행당 느린 순)", 1, null, 100, TopSqlOrder.ElapsedPerExecution),
        new("Batch (60 s 이상, 읽은 블록 순)", 60, null, null, TopSqlOrder.BufferGets),
    ];

    /// <summary>정렬 선택지(표시 이름).</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, TopSqlOrder>> Orders =
    [
        new("소요 시간", TopSqlOrder.ElapsedTime),
        new("CPU 시간", TopSqlOrder.CpuTime),
        new("읽은 블록", TopSqlOrder.BufferGets),
        new("디스크 읽기", TopSqlOrder.DiskReads),
        new("실행 횟수", TopSqlOrder.Executions),
        new("실행당 소요 시간", TopSqlOrder.ElapsedPerExecution),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IServiceProvider? _services;
    private IMonitorService? _monitor;
    private IExecutionPlanService? _plans;
    private ISession? _session;
    private bool _disposed;
    private bool _applyingPreset;

    [ObservableProperty]
    private SqlRangePreset _selectedPreset = Custom;

    [ObservableProperty]
    private TopSqlOrder _orderBy = TopSqlOrder.ElapsedTime;

    [ObservableProperty]
    private int _top = 50;

    [ObservableProperty]
    private string? _minElapsedText;

    [ObservableProperty]
    private string? _minBlocksText;

    [ObservableProperty]
    private string? _minExecutionsText;

    [ObservableProperty]
    private string? _textFilter;

    [ObservableProperty]
    private string? _schemaFilter;

    [ObservableProperty]
    private string? _activeMinutesText;

    [ObservableProperty]
    private TopSqlItem? _selectedSql;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private string _planText = string.Empty;

    [ObservableProperty]
    private string _statusText = "SQL 모니터: 접속 후 열면 전용 세션으로 공유 풀(v$sql)을 조회합니다.";

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>시험·디자이너용: 서비스 없이 만든다.</summary>
    public SqlMonitorViewModel()
    {
    }

    /// <summary>셸(DI)이 쓰는 생성자. 서비스는 처음 쓸 때 꺼낸다.</summary>
    public SqlMonitorViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Keys = services.GetService(typeof(ContextKeyService)) as ContextKeyService;
    }

    /// <summary>시험용: 서비스와 세션 여는 방법을 직접 준다.</summary>
    public SqlMonitorViewModel(IMonitorService monitor, IExecutionPlanService? plans, Func<CancellationToken, Task<ISession?>> openSession, ContextKeyService? keys = null)
    {
        _monitor = monitor;
        _plans = plans;
        OpenSession = openSession;
        Keys = keys;
    }

    public ObservableCollection<TopSqlItem> Results { get; } = [];

    /// <summary>메뉴 활성 키(<c>monitorSqlOpen</c>·<c>monitorSqlSelected</c>)를 쓰는 곳. 없으면 쓰지 않는다.</summary>
    public ContextKeyService? Keys { get; set; }

    /// <summary>전용 모니터 세션을 여는 방법. 셸이 넣는다.</summary>
    public Func<CancellationToken, Task<ISession?>>? OpenSession { get; set; }

    /// <summary>UI 스레드로 넘기는 방법. 시험은 바로 실행하도록 바꾼다.</summary>
    public Action<Action> PostToUi { get; set; } = a =>
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            a();
        }
        else
        {
            Dispatcher.UIThread.Post(a);
        }
    };

    public ISession? MonitorSession => _session;

    /// <summary>입력칸 값으로 조건을 만든다. 숫자가 아니면 <see cref="FormatException"/>.</summary>
    public TopSqlCriteria BuildCriteria() => new()
    {
        OrderBy = OrderBy,
        Top = Math.Clamp(Top, 1, 1000),
        MinElapsedSeconds = ParseNumber(MinElapsedText, "소요 시간(초)"),
        MinBufferGets = ParseWhole(MinBlocksText, "읽은 블록"),
        MinExecutions = ParseWhole(MinExecutionsText, "실행 횟수"),
        TextContains = string.IsNullOrWhiteSpace(TextFilter) ? null : TextFilter.Trim(),
        ParsingSchema = string.IsNullOrWhiteSpace(SchemaFilter) ? null : SchemaFilter.Trim(),
        ActiveWithin = ParseNumber(ActiveMinutesText, "최근 실행(분)") is { } m && m > 0 ? TimeSpan.FromMinutes(m) : null,
    };

    /// <summary>모니터 세션을 열고 첫 검색을 한다. 이미 열려 있으면 다시 검색만 한다.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_session is not null)
        {
            await SearchAsync(ct);
            return;
        }

        if (OpenSession is null)
        {
            StatusText = "모니터 세션을 열 수 없습니다(접속 정보 없음).";
            return;
        }

        ISession? session;
        try
        {
            session = await OpenSession(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = "모니터 세션을 열지 못했습니다: " + FirstLine(ex.Message);
            return;
        }

        if (session is null)
        {
            StatusText = "모니터 세션을 열지 않았습니다.";
            return;
        }

        if (!session.Server.Capabilities.CanQueryPerformanceViews)
        {
            await session.DisposeAsync();
            StatusText = $"계정 '{session.Profile.User}' 은(는) v$sql 을 읽을 권한이 없어 SQL 모니터를 쓸 수 없습니다.";
            return;
        }

        _session = session;
        IsOpen = true;
        UpdateKeys();
        await SearchAsync(ct);
    }

    /// <summary>모니터 세션을 닫는다.</summary>
    public async Task StopAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        IsOpen = false;
        UpdateKeys();
        if (session is not null)
        {
            await _gate.WaitAsync();
            try
            {
                await session.DisposeAsync();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>조건으로 Top SQL 을 검색한다.</summary>
    public Task SearchAsync(CancellationToken ct = default)
    {
        TopSqlCriteria criteria;
        try
        {
            criteria = BuildCriteria();
        }
        catch (FormatException ex)
        {
            StatusText = ex.Message;
            return Task.CompletedTask;
        }

        return RunAsync(async (monitor, session) =>
        {
            var rows = await monitor.GetTopSqlAsync(session, criteria, ct);
            PostToUi(() => ApplyResults(rows));
        }, ct);
    }

    /// <summary>선택 SQL 의 전문과 바인드 캡처를 보인다.</summary>
    public Task ShowBindsAsync(CancellationToken ct = default)
    {
        if (SelectedSql is not { } item)
        {
            return Task.CompletedTask;
        }

        return RunAsync(async (monitor, session) =>
        {
            var detail = await monitor.GetSqlDetailAsync(session, item.Row.Cursor, ct);
            PostToUi(() =>
            {
                DetailText = FormatDetail(detail);
                StatusText = $"SQL {item.SqlId} 전문·바인드 {detail.Binds.Count}개";
            });
        }, ct);
    }

    /// <summary>선택 SQL 의 실제 계획(DISPLAY_CURSOR)을 보인다. 전문도 함께 읽는다.</summary>
    public Task ShowPlanAsync(CancellationToken ct = default)
    {
        if (SelectedSql is not { } item)
        {
            return Task.CompletedTask;
        }

        return RunAsync(async (monitor, session) =>
        {
            var plans = _plans ??= _services?.GetService(typeof(IExecutionPlanService)) as IExecutionPlanService
                ?? throw new InvalidOperationException("실행 계획 서비스가 없습니다.");
            var detail = await monitor.GetSqlDetailAsync(session, item.Row.Cursor, ct);
            var plan = await plans.GetCursorPlanAsync(session, item.Row.Cursor, "TYPICAL", ct);
            PostToUi(() =>
            {
                DetailText = FormatDetail(detail);
                PlanText = plan.Text;
                StatusText = $"SQL {item.SqlId} 실제 계획 {plan.Steps.Count}단계";
            });
        }, ct);
    }

    /// <summary>전문 + 바인드 캡처를 글자로.</summary>
    public static string FormatDetail(SqlDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var sb = new StringBuilder();
        sb.Append("SQL ID ").Append(detail.Cursor.SqlId).AppendLine();
        sb.AppendLine(detail.FullText);
        sb.AppendLine();
        sb.AppendLine("[바인드 캡처]");
        if (detail.Binds.Count == 0)
        {
            sb.AppendLine("(없음 — 바인드 변수가 없거나 서버가 캡처하지 않았습니다)");
        }

        foreach (var b in detail.Binds)
        {
            sb.Append(b.Name).Append(" (").Append(b.DataType ?? "?").Append(") = ").AppendLine(b.Value ?? "(캡처 안 됨)");
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Interlocked.Exchange(ref _session, null) is { } s)
        {
            _ = s.DisposeAsync().AsTask();
        }

        UpdateKeys();
        _gate.Dispose();
    }

    partial void OnSelectedPresetChanged(SqlRangePreset value)
    {
        if (ReferenceEquals(value, Custom))
        {
            return;
        }

        _applyingPreset = true;
        try
        {
            MinElapsedText = value.MinElapsedSeconds?.ToString(CultureInfo.InvariantCulture);
            MinBlocksText = value.MinBlocks?.ToString(CultureInfo.InvariantCulture);
            MinExecutionsText = value.MinExecutions?.ToString(CultureInfo.InvariantCulture);
            OrderBy = value.Order;
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    partial void OnMinElapsedTextChanged(string? value) => MarkCustom();

    partial void OnMinBlocksTextChanged(string? value) => MarkCustom();

    partial void OnMinExecutionsTextChanged(string? value) => MarkCustom();

    partial void OnSelectedSqlChanged(TopSqlItem? value)
    {
        Keys?.Set("monitorSqlSelected", value is not null && _session is not null);
        if (value is null)
        {
            DetailText = string.Empty;
            PlanText = string.Empty;
        }
    }

    private void MarkCustom()
    {
        if (!_applyingPreset && !ReferenceEquals(SelectedPreset, Custom))
        {
            SelectedPreset = Custom;
        }
    }

    private static double? ParseNumber(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0)
        {
            return v;
        }

        throw new FormatException($"{label}: 0 이상의 숫자를 넣으세요.");
    }

    private static long? ParseWhole(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (long.TryParse(text.Trim().Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }

        throw new FormatException($"{label}: 0 이상의 정수를 넣으세요.");
    }

    private void ApplyResults(IReadOnlyList<TopSqlRow> rows)
    {
        var selected = SelectedSql?.Row.Cursor;
        Results.Clear();
        foreach (var r in rows)
        {
            Results.Add(new TopSqlItem(r));
        }

        SelectedSql = selected is { } c ? Results.FirstOrDefault(i => i.Row.Cursor == c) : null;
        StatusText = string.Create(CultureInfo.InvariantCulture, $"Top SQL {rows.Count}건 · {DateTime.Now:HH:mm:ss} 조회");
    }

    /// <summary>모니터 세션에서 한 번에 하나씩 실행한다. 실패는 상태 줄에 보인다.</summary>
    private async Task RunAsync(Func<IMonitorService, ISession, Task> work, CancellationToken ct)
    {
        if (_session is not { } session)
        {
            StatusText = "모니터 세션이 열려 있지 않습니다.";
            return;
        }

        var monitor = _monitor ??= _services?.GetService(typeof(IMonitorService)) as IMonitorService;
        if (monitor is null)
        {
            StatusText = "모니터 서비스가 없습니다.";
            return;
        }

        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await work(monitor, session);
        }
        catch (OperationCanceledException)
        {
            PostToUi(() => StatusText = "취소했습니다.");
        }
        catch (NotImplementedException)
        {
            PostToUi(() => StatusText = "이 기능이 아직 준비되지 않았습니다.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PostToUi(() => StatusText = FirstLine(ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void UpdateKeys()
    {
        if (Keys is not { } keys)
        {
            return;
        }

        var open = _session is not null;
        keys.Set("monitorSqlOpen", open);
        keys.Set("monitorSqlSelected", open && SelectedSql is not null);
    }

    private static string FirstLine(string message)
    {
        var i = message.IndexOf('\n', StringComparison.Ordinal);
        return (i < 0 ? message : message[..i]).Trim();
    }
}
