using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.Core.Events;
using Oranda.Core.History;
using Oranda.Core.Shell;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;

namespace Oranda.App.Features.HistoryScripts;

/// <summary>이력 목록의 한 줄(화면용).</summary>
public sealed record HistoryItem(HistoryEntry Entry)
{
    public long Id => Entry.Id;

    public string TimeText => Entry.ExecutedAt.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string ProfileName => Entry.ProfileName;

    public string StatusText => Entry.Status switch
    {
        HistoryStatus.Succeeded => "성공",
        HistoryStatus.Failed => "실패",
        HistoryStatus.Cancelled => "취소",
        _ => "거부",
    };

    public string ElapsedText => Entry.ElapsedMs is { } ms ? (ms < 1000 ? $"{ms} ms" : (ms / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " s") : string.Empty;

    public string RowsText => Entry.RowsAffected?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>목록에 보일 한 줄(첫 비어 있지 않은 줄, 120자).</summary>
    public string SqlPreview
    {
        get
        {
            var line = Entry.SqlText.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            return line.Length > 120 ? line[..120] + "…" : line;
        }
    }

    public string FullSql => Entry.SqlText;

    public string? ErrorText => Entry.ErrorCode is null ? null : Entry.ErrorCode + (Entry.ErrorMessage is null ? string.Empty : ": " + Entry.ErrorMessage);
}

/// <summary>상태 필터 목록 항목.</summary>
public sealed record HistoryStatusFilter(string Title, HistoryStatus? Status);

/// <summary>
/// <c>oranda.panel.history</c> — 접속별 실행 이력 검색·다시 실행(EXEC-07). 저장소는 <see cref="IHistoryStore"/> 만 쓴다.
/// 서비스는 처음 쓸 때 꺼낸다(D-006). 새 실행이 끝나면(<c>StatementExecuted</c>) 목록을 잠시 뒤 다시 읽는다.
/// </summary>
[OrandaToolPanel("oranda.panel.history", ToolPanelKind.Tool, DockLocation.Bottom, Order = 40)]
public sealed partial class HistoryViewModel : ToolPanelViewModelBase, IDisposable
{
    /// <summary>한 번에 읽는 개수.</summary>
    public const int PageSize = 200;

    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);

    private readonly IServiceProvider? _services;
    private readonly List<IDisposable> _subscriptions = [];
    private IHistoryStore? _store;
    private CancellationTokenSource? _debounce;
    private int _generation;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private HistoryStatusFilter _statusFilter;

    [ObservableProperty]
    private bool _currentConnectionOnly = true;

    [ObservableProperty]
    private HistoryItem? _selected;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _canLoadMore;

    /// <summary>시험·디자이너용: 서비스 없이 만든다(빈 목록).</summary>
    public HistoryViewModel()
    {
        StatusFilters = DefaultFilters;
        _statusFilter = StatusFilters[0];
    }

    /// <summary>셸(DI)이 쓰는 생성자. 이벤트 버스만 바로 꺼내 새 실행을 기다린다.</summary>
    public HistoryViewModel(IServiceProvider services)
        : this()
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        if (services.GetService(typeof(IEventBus)) is IEventBus bus)
        {
            _subscriptions.Add(bus.Subscribe<StatementExecuted>(this, e =>
            {
                if (!e.Options.SkipHistory)
                {
                    PostToUi(() => ScheduleReload());
                }
            }));
        }
    }

    /// <summary>시험용: 저장소와 현재 접속을 직접 넣는다.</summary>
    internal HistoryViewModel(IHistoryStore store, Func<Guid?> currentProfileId)
        : this()
    {
        _store = store;
        CurrentProfileProvider = currentProfileId;
        PostToUi = a => a();
    }

    public static IReadOnlyList<HistoryStatusFilter> DefaultFilters { get; } =
    [
        new("모두", null),
        new("성공", HistoryStatus.Succeeded),
        new("실패", HistoryStatus.Failed),
        new("취소", HistoryStatus.Cancelled),
        new("거부", HistoryStatus.Denied),
    ];

    public IReadOnlyList<HistoryStatusFilter> StatusFilters { get; }

    public ObservableCollection<HistoryItem> Items { get; } = [];

    /// <summary>이벤트를 UI 스레드로 넘기는 방법. 시험은 바로 실행하도록 바꾼다.</summary>
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

    /// <summary>지금 접속(작업 세션)의 프로필 Id. 없으면 null — 「이 접속만」 을 켜도 전체를 보인다.</summary>
    internal Func<Guid?>? CurrentProfileProvider { get; set; }

    /// <summary>화면이 바뀐 뒤(목록을 새로 채운 뒤) 알린다(시험용).</summary>
    public event EventHandler? Reloaded;

    public void Dispose()
    {
        foreach (var s in _subscriptions)
        {
            s.Dispose();
        }

        _debounce?.Cancel();
        _debounce?.Dispose();
    }

    partial void OnSearchTextChanged(string value) => ScheduleReload();

    partial void OnStatusFilterChanged(HistoryStatusFilter value) => ScheduleReload();

    partial void OnCurrentConnectionOnlyChanged(bool value) => ScheduleReload();

    /// <summary>입력이 멈춘 뒤 다시 읽는다.</summary>
    private void ScheduleReload()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDelay, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            PostToUi(() => _ = ReloadAsync(CancellationToken.None));
        });
    }

    /// <summary>조건에 맞는 최신 <see cref="PageSize"/> 건으로 목록을 새로 채운다.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var store = ResolveStore();
        if (store is null)
        {
            return;
        }

        var generation = ++_generation;
        IReadOnlyList<HistoryEntry> rows;
        try
        {
            rows = await store.SearchAsync(BuildQuery(null), ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = "이력을 읽지 못했다: " + ex.Message;
            return;
        }

        if (generation != _generation)
        {
            return; // 더 새 요청이 있다.
        }

        Items.Clear();
        foreach (var r in rows)
        {
            Items.Add(new HistoryItem(r));
        }

        CanLoadMore = rows.Count >= PageSize;
        StatusMessage = rows.Count == 0 ? "이력이 없다" : $"{rows.Count}건" + (CanLoadMore ? " 이상" : string.Empty);
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>다음 쪽(더 오래된 것)을 목록 끝에 붙인다.</summary>
    public async Task LoadMoreAsync(CancellationToken ct = default)
    {
        var store = ResolveStore();
        if (store is null || Items.Count == 0)
        {
            return;
        }

        var rows = await store.SearchAsync(BuildQuery(Items[^1].Id), ct).ConfigureAwait(true);
        foreach (var r in rows)
        {
            Items.Add(new HistoryItem(r));
        }

        CanLoadMore = rows.Count >= PageSize;
        StatusMessage = $"{Items.Count}건" + (CanLoadMore ? " 이상" : string.Empty);
    }

    /// <summary>선택한 항목(여러 개면 같이)을 지운다.</summary>
    public async Task DeleteAsync(IReadOnlyList<HistoryItem> items, CancellationToken ct = default)
    {
        var store = ResolveStore();
        if (store is null || items.Count == 0)
        {
            return;
        }

        await store.DeleteAsync([.. items.Select(i => i.Id)], ct).ConfigureAwait(true);
        foreach (var i in items)
        {
            Items.Remove(i);
        }

        StatusMessage = $"{items.Count}건 지움";
    }

    /// <summary>이력 비우기. <paramref name="currentConnectionOnly"/> 면 지금 접속 것만, 아니면 전부.</summary>
    public async Task ClearAsync(bool currentConnectionOnly, CancellationToken ct = default)
    {
        var store = ResolveStore();
        if (store is null)
        {
            return;
        }

        await store.ClearAsync(currentConnectionOnly ? CurrentProfileId() : null, ct).ConfigureAwait(true);
        await ReloadAsync(ct).ConfigureAwait(true);
    }

    /// <summary>항목의 SQL 을 그 접속(없으면 활성 세션)에서 다시 실행한다.</summary>
    public async Task RerunAsync(HistoryItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_services is null)
        {
            return;
        }

        var ok = await HistoryScriptsCommands.RerunAsync(_services, item.FullSql, item.Entry.ProfileId, ct).ConfigureAwait(true);
        StatusMessage = ok ? "다시 실행했다" : "다시 실행하지 못했다(연결된 세션이 없거나 오류)";
    }

    /// <summary>항목의 SQL 을 활성 편집기 커서 위치에 넣는다.</summary>
    public async Task SendToEditorAsync(HistoryItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_services is null)
        {
            return;
        }

        var ok = await HistoryScriptsCommands.SendToEditorAsync(_services, item.FullSql, "이력에서 가져오기", ct).ConfigureAwait(true);
        StatusMessage = ok ? "편집기에 넣었다" : "편집기가 열려 있지 않거나 취소했다";
    }

    internal HistoryQuery BuildQuery(long? beforeId) => new()
    {
        ProfileId = CurrentConnectionOnly ? CurrentProfileId() : null,
        Text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
        Status = StatusFilter.Status,
        Limit = PageSize,
        BeforeId = beforeId,
    };

    private Guid? CurrentProfileId()
    {
        if (CurrentProfileProvider is { } p)
        {
            return p();
        }

        return _services is null ? null : HistoryScriptsCommands.ResolveSession(_services)?.Profile.Id;
    }

    private IHistoryStore? ResolveStore()
    {
        if (_store is null && _services is not null)
        {
            _store = _services.GetService(typeof(IHistoryStore)) as IHistoryStore;
        }

        return _store;
    }
}
