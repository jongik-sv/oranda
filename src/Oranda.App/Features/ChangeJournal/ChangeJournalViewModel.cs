using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.HistoryScripts;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Events;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ChangeJournal;

/// <summary>변경 일지 목록의 한 줄(화면용).</summary>
public sealed record JournalItem(JournalEntry Entry)
{
    public long Id => Entry.Id;

    public string TimeText => Entry.CapturedAt.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string ObjectText => $"{Entry.Owner}.{Entry.Name}";

    public string TypeText => Entry.ObjectType;

    public string CauseText => Entry.Cause == JournalCause.Ddl ? "DDL 실행" : "컴파일";

    public string ProfileName => Entry.ProfileName;

    /// <summary>보관 실패면 이유, 아니면 실행한 문장 요약.</summary>
    public string SummaryText => Entry.FailureReason is { } reason ? "보관 실패: " + reason : Entry.StatementSummary ?? string.Empty;

    public bool IsFailure => Entry.IsFailure;
}

/// <summary>차이 보기 한 줄. <see cref="Kind"/> 은 <c>+</c>(새 쪽에만), <c>-</c>(옛 쪽에만), 공백(같음).</summary>
public sealed record DiffLine(string Text, char Kind)
{
    public bool IsAdded => Kind == '+';

    public bool IsRemoved => Kind == '-';
}

/// <summary>
/// <c>oranda.panel.changeJournal</c> — 변경 일지(TSK-13-07): DDL 실행 직전·PL/SQL 컴파일 직전에 보관한 서버 DDL 의 목록·검색·차이 보기.
/// 되돌리기는 이전 DDL 을 새 SQL 편집기 탭에 여는 데까지이며 자동 실행하지 않는다. 저장소는 <see cref="IChangeJournalStore"/> 만 쓰고
/// 서비스는 처음 쓸 때 꺼낸다(D-006).
/// </summary>
[OrandaToolPanel("oranda.panel.changeJournal", ToolPanelKind.Tool, DockLocation.Bottom, Order = 45)]
public sealed partial class ChangeJournalViewModel : ToolPanelViewModelBase, IDisposable
{
    /// <summary>한 번에 읽는 개수.</summary>
    public const int PageSize = 300;

    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);
    private static ChangeJournalViewModel? s_active;

    private readonly IServiceProvider? _services;
    private readonly List<IDisposable> _subscriptions = [];
    private IChangeJournalStore? _store;
    private CancellationTokenSource? _debounce;
    private int _generation;
    private int _selectionGeneration;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _currentConnectionOnly;

    [ObservableProperty]
    private bool _onlySucceeded;

    [ObservableProperty]
    private DbObjectRef? _objectFilter;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _detailTitle = string.Empty;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private string _diffTitle = string.Empty;

    /// <summary>시험·디자이너용: 서비스 없이 만든다(빈 목록).</summary>
    public ChangeJournalViewModel()
    {
    }

    /// <summary>셸(DI)이 쓰는 생성자. 새 행이 저장되면(<see cref="ChangeJournalSaved"/>) 목록을 잠시 뒤 다시 읽는다.</summary>
    public ChangeJournalViewModel(IServiceProvider services)
        : this()
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        s_active = this;
        _objectFilter = TakePendingObject();
        if (services.GetService(typeof(IEventBus)) is IEventBus bus)
        {
            _subscriptions.Add(bus.Subscribe<ChangeJournalSaved>(this, _ => PostToUi(ScheduleReload)));
        }
    }

    /// <summary>시험용: 저장소를 직접 넣는다.</summary>
    internal ChangeJournalViewModel(IChangeJournalStore store)
        : this()
    {
        _store = store;
        PostToUi = a => a();
    }

    /// <summary>패널이 아직 만들어지기 전에 「변경 일지 보기」 가 정한 대상. 패널이 만들어질 때 가져간다.</summary>
    internal static DbObjectRef? PendingObject { get; set; }

    /// <summary>마지막으로 만든 패널(「변경 일지 보기」 가 대상을 알릴 때 쓴다).</summary>
    internal static ChangeJournalViewModel? Active => s_active;

    public ObservableCollection<JournalItem> Items { get; } = [];

    /// <summary>선택한 항목의 이전 버전과의 차이(또는 고른 두 버전의 차이).</summary>
    public ObservableCollection<DiffLine> Diff { get; } = [];

    /// <summary>지금 선택한 항목들(보기가 알려 준다). 최대 둘까지만 비교에 쓴다.</summary>
    public IReadOnlyList<JournalItem> Selection { get; private set; } = [];

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

    /// <summary>지금 접속(작업 세션)의 db_key. 없으면 null — 「이 접속만」 을 켜도 전체를 보인다.</summary>
    internal Func<string?>? CurrentDbKeyProvider { get; set; }

    /// <summary>새 SQL 편집기 탭을 연다(글자, 접속 프로필 Id). 시험이 바꿔 끼운다.</summary>
    internal Func<string, Guid?, bool> EditorOpener { get; set; } = OpenInNewEditor;

    /// <summary>목록을 새로 채운 뒤 알린다(시험용).</summary>
    public event EventHandler? Reloaded;

    public bool HasObjectFilter => ObjectFilter is not null;

    public string ObjectFilterText => ObjectFilter is { } o ? $"대상: {o.Owner}.{o.Name}" : string.Empty;

    public void Dispose()
    {
        foreach (var s in _subscriptions)
        {
            s.Dispose();
        }

        _debounce?.Cancel();
        _debounce?.Dispose();
        if (ReferenceEquals(s_active, this))
        {
            s_active = null;
        }
    }

    partial void OnSearchTextChanged(string value) => ScheduleReload();

    partial void OnCurrentConnectionOnlyChanged(bool value) => ScheduleReload();

    partial void OnOnlySucceededChanged(bool value) => ScheduleReload();

    partial void OnObjectFilterChanged(DbObjectRef? value)
    {
        OnPropertyChanged(nameof(HasObjectFilter));
        OnPropertyChanged(nameof(ObjectFilterText));
        ScheduleReload();
    }

    /// <summary>스키마 브라우저의 「변경 일지 보기」: 이 객체의 버전만 보인다.</summary>
    public void ShowObject(DbObjectRef? target)
    {
        SearchText = string.Empty;
        ObjectFilter = target;
    }

    public void ClearObjectFilter() => ObjectFilter = null;

    private static DbObjectRef? TakePendingObject()
    {
        var pending = PendingObject;
        PendingObject = null;
        return pending;
    }

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
        IReadOnlyList<JournalEntry> rows;
        try
        {
            rows = await store.SearchAsync(BuildQuery(), ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = "변경 일지를 읽지 못했다: " + ex.Message;
            return;
        }

        if (generation != _generation)
        {
            return; // 더 새 요청이 있다.
        }

        Items.Clear();
        foreach (var r in rows)
        {
            Items.Add(new JournalItem(r));
        }

        StatusMessage = rows.Count == 0 ? "보관된 변경이 없다" : $"{rows.Count}건" + (rows.Count >= PageSize ? " 이상" : string.Empty);
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    internal JournalQuery BuildQuery() => new()
    {
        Text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
        DbKey = CurrentConnectionOnly ? CurrentDbKeyProvider?.Invoke() ?? ResolveCurrentDbKey() : null,
        Owner = ObjectFilter?.Owner,
        Name = ObjectFilter?.Name,
        OnlySucceeded = OnlySucceeded,
        Limit = PageSize,
    };

    /// <summary>보기가 선택이 바뀔 때 알린다. 본문과 차이를 새로 채운다.</summary>
    public async Task SetSelectionAsync(IReadOnlyList<JournalItem> selected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        Selection = selected;
        var generation = ++_selectionGeneration;
        Diff.Clear();
        DiffTitle = string.Empty;
        if (selected.Count == 0)
        {
            DetailTitle = string.Empty;
            DetailText = string.Empty;
            return;
        }

        var item = selected[0];
        DetailTitle = $"#{item.Id} {item.ObjectText} ({item.TypeText}) · {item.TimeText}";
        DetailText = item.Entry.PreviousDdl ?? item.Entry.FailureReason ?? string.Empty;

        JournalEntry? older = null;
        JournalEntry? newer = null;
        if (selected.Count >= 2)
        {
            var other = selected[1];
            if (!SameObject(item.Entry, other.Entry) || item.IsFailure || other.IsFailure)
            {
                DiffTitle = "같은 객체의 보관된 두 버전을 골라야 차이를 볼 수 있다";
                return;
            }

            (older, newer) = item.Id < other.Id ? (item.Entry, other.Entry) : (other.Entry, item.Entry);
        }
        else if (!item.IsFailure)
        {
            newer = item.Entry;
            try
            {
                older = await FindPreviousVersionAsync(item.Entry, ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusMessage = "이전 버전을 읽지 못했다: " + ex.Message;
                return;
            }

            if (generation != _selectionGeneration)
            {
                return;
            }

            if (older is null)
            {
                DiffTitle = "이 객체의 이전 버전이 없다";
                return;
            }
        }
        else
        {
            return;
        }

        FillDiff(older!, newer!);
    }

    private void FillDiff(JournalEntry older, JournalEntry newer)
    {
        var text = PlsqlSourceText.BuildDiff(older.PreviousDdl ?? string.Empty, newer.PreviousDdl ?? string.Empty);
        DiffTitle = $"차이: #{older.Id} → #{newer.Id}";
        if (text.Length == 0)
        {
            DiffTitle += " (같은 내용)";
            return;
        }

        foreach (var line in text.TrimEnd('\n').Split('\n'))
        {
            Diff.Add(new DiffLine(line.Length >= 2 ? line[2..] : string.Empty, line.Length > 0 ? line[0] : ' '));
        }
    }

    private async Task<JournalEntry?> FindPreviousVersionAsync(JournalEntry entry, CancellationToken ct)
    {
        var store = ResolveStore();
        if (store is null)
        {
            return null;
        }

        var versions = await store.SearchAsync(new JournalQuery
        {
            DbKey = entry.DbKey,
            Owner = entry.Owner,
            Name = entry.Name,
            ObjectType = entry.ObjectType,
            OnlySucceeded = true,
            Limit = 200,
        }, ct).ConfigureAwait(true);
        return versions.FirstOrDefault(v => v.Id < entry.Id); // 최신 순이므로 첫 번째가 바로 앞 버전
    }

    private static bool SameObject(JournalEntry a, JournalEntry b)
        => a.DbKey == b.DbKey && a.Owner == b.Owner && a.Name == b.Name && a.ObjectType == b.ObjectType;

    /// <summary>이 항목의 이전 DDL 을 새 SQL 편집기에 연다. 실행하지 않는다. 보관 실패 항목은 false.</summary>
    public bool OpenInEditor(JournalItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Entry.PreviousDdl is null)
        {
            StatusMessage = "보관된 DDL 이 없는 항목이다";
            return false;
        }

        var ok = EditorOpener(BuildRestoreScript(item.Entry), item.Entry.ProfileId == Guid.Empty ? null : item.Entry.ProfileId);
        StatusMessage = ok ? "새 편집기에 열었다(자동으로 실행하지 않는다)" : "편집기를 열지 못했다";
        return ok;
    }

    /// <summary>되돌리기 스크립트: 어느 보관본인지 알리는 주석 + 이전 DDL + 실행 끝맺음(소스는 <c>/</c>, 그 밖은 <c>;</c>).</summary>
    public static string BuildRestoreScript(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var ddl = (entry.PreviousDdl ?? string.Empty).Trim();
        var header = $"-- 변경 일지 #{entry.Id}: {entry.Owner}.{entry.Name} ({entry.ObjectType}) {entry.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} 이전 DDL\n"
            + "-- 확인한 뒤 직접 실행한다(자동으로 실행하지 않는다).\n";
        if (PlsqlSourceText.SourceTypes.Contains(entry.ObjectType))
        {
            // PL/SQL 단위는 끝의 ; 가 문장 끝이 아니라서 / 단독 줄로 끝맺어야 편집기가 한 문장으로 본다.
            // 서버 DDL 이 이미 / 줄을 가졌으면(TRIGGER 는 본문 뒤에 ALTER TRIGGER ... ENABLE 이 따라온다) 그대로 둔다.
            var hasSlash = ddl.Split('\n').Any(line => line.Trim() == "/");
            return header + (hasSlash ? ddl : ddl + "\n/") + "\n";
        }

        return header + (ddl.EndsWith(';') ? ddl : ddl + ";") + "\n";
    }

    private static bool OpenInNewEditor(string text, Guid? profileId)
    {
        var tab = SqlEditorHost.Current.NewDocument(text);
        tab.ConnectionProfileId = profileId;
        return true;
    }

    private string? ResolveCurrentDbKey()
        => _services is null ? null : HistoryScriptsCommands.ResolveSession(_services)?.DbKey;

    private IChangeJournalStore? ResolveStore()
    {
        if (_store is null && _services is not null)
        {
            _store = _services.GetService(typeof(IChangeJournalStore)) as IChangeJournalStore;
        }

        return _store;
    }
}
