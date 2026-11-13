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
using Oranda.App.Features.Rollback;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Events;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ChangeJournal;

/// <summary>변경 일지 목록의 한 줄(화면용).</summary>
public sealed record JournalItem(JournalEntry Entry) : IJournalRow
{
    public long Id => Entry.Id;

    public DateTimeOffset At => Entry.CapturedAt;

    /// <summary>DDL 보관본은 영향 행·시점이 없다.</summary>
    public string RowsText => string.Empty;

    public string PointText => string.Empty;

    /// <summary>원복 스크립트 칸: 「DDL 보관」(보관 실패면 「보관 실패」).</summary>
    public string ScriptText => IsFailure ? "보관 실패" : "DDL 보관";

    public bool IsScriptWarning => false;

    public bool IsChild => false;

    public string ExpandGlyph => string.Empty;

    public string TimeText => Entry.CapturedAt.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string ObjectText => $"{Entry.Owner}.{Entry.Name}";

    public string TypeText => Entry.ObjectType;

    public string CauseText => Entry.Cause == JournalCause.Ddl ? "DDL 실행" : "컴파일";

    public string ProfileName => Entry.ProfileName;

    /// <summary>보관 실패면 이유, 아니면 실행한 문장 요약.</summary>
    public string SummaryText => Entry.FailureReason is { } reason ? "보관 실패: " + reason : Entry.StatementSummary ?? string.Empty;

    public bool IsFailure => Entry.IsFailure;

    /// <summary>앞 아이콘 키: 보관 실패 = 오류, 컴파일 = 컴파일, DDL 실행 = SQL.</summary>
    public string IconKey => IsFailure ? "error" : Entry.Cause == JournalCause.Compile ? "compile" : "fileSql";
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
    private IRollbackStore? _rollbackStore;
    private bool _rollbackStoreResolved;
    private readonly HashSet<long> _expanded = [];
    private readonly List<IJournalRow> _merged = [];
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

    /// <summary>「원인」 거르기(전체·DDL 실행·컴파일·DML, 되돌리기 지점 설계 11.1).</summary>
    [ObservableProperty]
    private JournalCauseChoice _selectedCause = JournalCauseChoice.Of(JournalCauseFilter.All);

    /// <summary>고른 커밋 묶음(묶음 줄이나 그 안 문장 줄을 골랐을 때). 「원복 스크립트 열기」·「이 실행 전과 비교…」 의 대상.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenScript), nameof(CanCompare))]
    private RollbackGroupItem? _selectedGroup;

    /// <summary>고른 묶음 안 문장(문장 줄을 골랐을 때).</summary>
    [ObservableProperty]
    private RollbackStatementItem? _selectedStatement;

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
            _subscriptions.Add(bus.Subscribe<RollbackGroupSaved>(this, _ => PostToUi(ScheduleReload)));
        }

        RetentionDaysProvider = () => (services.GetService(typeof(Oranda.App.Shell.Settings.SettingsService)) as Oranda.App.Shell.Settings.SettingsService)?.Current.Rollback.Normalized().RetentionDays
            ?? RollbackSettings.DefaultRetentionDays;
        CommandRunner = (id, arg) => services.GetService(typeof(ICommandRegistry)) is ICommandRegistry registry && services.GetService(typeof(ContextKeyService)) is ContextKeyService keys
            ? registry.ExecuteAsync(id, keys, arg)
            : Task.FromResult(false);
    }

    /// <summary>시험용: 저장소를 직접 넣는다.</summary>
    internal ChangeJournalViewModel(IChangeJournalStore store)
        : this()
    {
        _store = store;
        _rollbackStoreResolved = true;
        PostToUi = a => a();
    }

    /// <summary>시험용: 변경 일지 저장소와 원복 묶음 저장소를 함께 넣는다.</summary>
    internal ChangeJournalViewModel(IChangeJournalStore? store, IRollbackStore? rollbackStore)
        : this()
    {
        _store = store;
        _rollbackStore = rollbackStore;
        _rollbackStoreResolved = true;
        PostToUi = a => a();
    }

    /// <summary>패널이 아직 만들어지기 전에 「변경 일지 보기」 가 정한 대상. 패널이 만들어질 때 가져간다.</summary>
    internal static DbObjectRef? PendingObject { get; set; }

    /// <summary>마지막으로 만든 패널(「변경 일지 보기」 가 대상을 알릴 때 쓴다).</summary>
    internal static ChangeJournalViewModel? Active => s_active;

    /// <summary>목록에 있는 DDL 보관본 줄(<see cref="Rows"/> 의 <see cref="JournalItem"/> 만).</summary>
    public ObservableCollection<JournalItem> Items { get; } = [];

    /// <summary>
    /// 표에 보이는 줄: DDL 보관본과 커밋 묶음을 시간 역순으로 합쳐 <see cref="PageSize"/> 줄로 자른 것. 펼친 묶음은 그 아래에 문장 줄이 이어진다.
    /// </summary>
    public ObservableCollection<IJournalRow> Rows { get; } = [];

    public IReadOnlyList<JournalCauseChoice> CauseChoices { get; } = JournalCauseChoice.All;

    /// <summary>원복 스크립트를 열 수 있는 묶음을 골랐다.</summary>
    public bool CanOpenScript => SelectedGroup?.CanOpenScript == true;

    /// <summary>시점 비교를 할 수 있는 묶음(또는 문장)을 골랐다: 기준 시점이 있다.</summary>
    public bool CanCompare => SelectedGroup?.Group.BasePoint is not null;

    /// <summary>보관 기간(상태 줄 안내). 셸은 설정에서 읽는다.</summary>
    internal Func<int> RetentionDaysProvider { get; set; } = () => RollbackSettings.DefaultRetentionDays;

    /// <summary>명령 id 와 인자로 명령을 부른다(원복 스크립트 열기·이 실행 전과 비교). 처리기가 없으면 false. 시험이 바꿔 끼운다.</summary>
    internal Func<string, object, Task<bool>> CommandRunner { get; set; } = (_, _) => Task.FromResult(false);

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

    partial void OnSelectedCauseChanged(JournalCauseChoice value) => ScheduleReload();

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

    /// <summary>
    /// 조건에 맞는 최신 <see cref="PageSize"/> 건으로 목록을 새로 채운다. 두 저장소(<see cref="IChangeJournalStore"/>, <see cref="IRollbackStore"/>)를
    /// 각각 시간 역순으로 읽어 합친 뒤 자른다(되돌리기 지점 설계 11.1). 한쪽 저장소가 없거나 읽지 못해도 다른 쪽은 보인다.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var store = ResolveStore();
        var rollbackStore = ResolveRollbackStore();
        if (store is null && rollbackStore is null)
        {
            return;
        }

        var generation = ++_generation;
        var cause = SelectedCause.Filter;
        var errors = new List<string>();
        var entriesTask = store is not null && cause != JournalCauseFilter.Dml
            ? SafeAsync(() => store.SearchAsync(BuildQuery(), ct), "변경 일지를 읽지 못했다: ")
            : Task.FromResult<(IReadOnlyList<JournalEntry>, string?)>(([], null));
        var groupsTask = rollbackStore is not null && cause is JournalCauseFilter.All or JournalCauseFilter.Dml
            ? SafeAsync(() => rollbackStore.SearchAsync(BuildRollbackQuery(), ct), "실행 기록을 읽지 못했다: ")
            : Task.FromResult<(IReadOnlyList<RollbackGroup>, string?)>(([], null));
        await Task.WhenAll(entriesTask, groupsTask).ConfigureAwait(true);
        var (entries, entriesError) = await entriesTask.ConfigureAwait(true);
        var (groups, groupsError) = await groupsTask.ConfigureAwait(true);
        errors.AddRange(new[] { entriesError, groupsError }.OfType<string>());
        ct.ThrowIfCancellationRequested();

        if (generation != _generation)
        {
            return; // 더 새 요청이 있다.
        }

        if (errors.Count > 0 && entries.Count == 0 && groups.Count == 0)
        {
            // 조건이 바뀐 뒤 읽기에 실패했으면 옛 조건의 줄을 남기지 않는다.
            _merged.Clear();
            Items.Clear();
            Rows.Clear();
            SelectedGroup = null;
            SelectedStatement = null;
            StatusMessage = string.Join(" · ", errors);
            return;
        }

        var journalRows = entries
            .Where(e => cause switch
            {
                JournalCauseFilter.Ddl => e.Cause == JournalCause.Ddl,
                JournalCauseFilter.Compile => e.Cause == JournalCause.Compile,
                _ => true,
            })
            .Select(e => (IJournalRow)new JournalItem(e));
        var merged = journalRows
            .Concat(groups.Select(g => (IJournalRow)new RollbackGroupItem(g) { IsExpanded = _expanded.Contains(g.Id) }))
            .OrderByDescending(r => r.At)
            .Take(PageSize)
            .ToList();

        _merged.Clear();
        _merged.AddRange(merged);
        _expanded.IntersectWith(merged.OfType<RollbackGroupItem>().Select(g => g.Id));
        Items.Clear();
        foreach (var item in merged.OfType<JournalItem>())
        {
            Items.Add(item);
        }

        // 고른 묶음은 같은 번호의 새 줄로 옮긴다(없어졌으면 풀린다).
        var selectedId = SelectedGroup?.Id;
        var selectedSeq = SelectedStatement?.Statement.Seq;
        RebuildRows();
        SelectedGroup = selectedId is { } id ? merged.OfType<RollbackGroupItem>().FirstOrDefault(g => g.Id == id) : null;
        SelectedStatement = SelectedGroup is null || selectedSeq is null
            ? null
            : Rows.OfType<RollbackStatementItem>().FirstOrDefault(r => ReferenceEquals(r.Parent, SelectedGroup) && r.Statement.Seq == selectedSeq);
        var count = merged.Count;
        var status = count == 0 ? "보관된 변경이 없다" : $"{count}건" + (count >= PageSize ? " 이상" : string.Empty);
        if (rollbackStore is not null)
        {
            status += $" · 원복 스크립트는 이 PC 에 암호화해 보관하며 {RetentionDaysProvider()}일 뒤 지웁니다.";
        }

        StatusMessage = errors.Count > 0 ? status + " · " + string.Join(" · ", errors) : status;
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>합친 목록으로 표 줄을 다시 만든다(펼친 묶음은 그 문장 줄을 바로 아래에, 실행한 순서대로).</summary>
    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var row in _merged)
        {
            Rows.Add(row);
            if (row is RollbackGroupItem { IsExpanded: true } group)
            {
                foreach (var st in group.Group.Statements.OrderBy(x => x.Seq))
                {
                    Rows.Add(new RollbackStatementItem(group, st));
                }
            }
        }
    }

    /// <summary>
    /// 묶음 줄을 접거나 편다(문장이 없는 묶음은 그대로). 그 묶음 아래 문장 줄만 넣고 빼서 다른 줄의 선택은 그대로 둔다.
    /// </summary>
    public void ToggleExpand(RollbackGroupItem group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var at = Rows.IndexOf(group);
        if (group.StatementCount == 0 || at < 0)
        {
            return;
        }

        group.IsExpanded = !group.IsExpanded;
        if (group.IsExpanded)
        {
            _expanded.Add(group.Id);
            var i = at + 1;
            foreach (var st in group.Group.Statements.OrderBy(x => x.Seq))
            {
                Rows.Insert(i++, new RollbackStatementItem(group, st));
            }
        }
        else
        {
            _expanded.Remove(group.Id);
            while (at + 1 < Rows.Count && Rows[at + 1] is RollbackStatementItem child && ReferenceEquals(child.Parent, group))
            {
                if (ReferenceEquals(SelectedStatement, child))
                {
                    SelectedStatement = null;
                }

                Rows.RemoveAt(at + 1);
            }
        }
    }

    /// <summary>저장소 읽기를 감싼다: 동기 예외도 이유로 바꾸고, 취소만 그대로 던진다.</summary>
    private static async Task<(IReadOnlyList<T> Rows, string? Error)> SafeAsync<T>(Func<Task<IReadOnlyList<T>>> read, string prefix)
    {
        try
        {
            return (await read().ConfigureAwait(true), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ([], prefix + ex.Message);
        }
    }

    internal RollbackQuery BuildRollbackQuery() => new()
    {
        Text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        DbKey = CurrentConnectionOnly ? CurrentDbKeyProvider?.Invoke() ?? ResolveCurrentDbKey() : null,
        Table = ObjectFilter,
        Limit = PageSize,
    };

    /// <summary>
    /// 보기가 선택이 바뀔 때 알린다(어느 종류의 줄이든). DDL 보관본은 <see cref="SetSelectionAsync"/> 로 본문·차이를 채우고,
    /// 묶음·문장 줄은 그 묶음을 <see cref="SelectedGroup"/> 으로 두고 본문 칸에 문장 목록을 보인다.
    /// </summary>
    public async Task SetRowSelectionAsync(IReadOnlyList<IJournalRow> selected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var first = selected.Count > 0 ? selected[0] : null;
        SelectedStatement = first as RollbackStatementItem;
        SelectedGroup = first switch
        {
            RollbackGroupItem g => g,
            RollbackStatementItem st => st.Parent,
            _ => null,
        };
        if (SelectedGroup is { } group)
        {
            ++_selectionGeneration; // 앞서 고른 DDL 의 차이 읽기가 늦게 끝나도 묶음 본문을 덮지 않게
            Selection = [];
            Diff.Clear();
            DiffTitle = string.Empty;
            DetailTitle = $"#{group.Id} · {group.Group.ProfileName} · {group.CauseText} · {group.ScriptText}";
            DetailText = GroupDetail(group.Group);
            return;
        }

        await SetSelectionAsync([.. selected.OfType<JournalItem>()], ct).ConfigureAwait(true);
    }

    /// <summary>묶음 본문 칸: 문장마다 한 줄(「[1/3] 14:03:21 UPDATE … · 12행 · 12문장」).</summary>
    internal static string GroupDetail(RollbackGroup group)
    {
        var total = group.Statements.Count;
        var lines = group.Statements.OrderBy(x => x.Seq).Select(st =>
        {
            var row = new RollbackStatementItem(new RollbackGroupItem(group), st);
            var rows = st.RowsAffected is { } n ? $" · {n.ToString("N0", CultureInfo.CurrentCulture)}행" : string.Empty;
            var point = st.BeforePoint is { } p ? " · " + p.Text : string.Empty;
            return $"-- [{st.Seq.ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)}] {row.TimeText} {row.SummaryText}{rows}{point} · {row.ScriptText}";
        });
        var head = group.StateReason is { Length: > 0 } reason ? [$"-- {reason}"] : Array.Empty<string>();
        return string.Join('\n', head.Concat(lines));
    }

    /// <summary>「원복 스크립트 열기」: 원복 스크립트 탭(RB-43)을 명령으로 연다. 실행하지 않는다.</summary>
    public async Task<bool> OpenScriptAsync()
    {
        if (SelectedGroup is not { CanOpenScript: true } group)
        {
            StatusMessage = "원복 스크립트가 있는 커밋 묶음을 고르세요";
            return false;
        }

        var ok = await RunCommandAsync(RollbackCommandIds.OpenScript, new RollbackScriptArgs(group.Id)).ConfigureAwait(true);
        // 열기에 실패한 이유(키 없음·파일 없음)는 명령 처리기가 따로 알린다. 여기서는 명령을 찾지 못했을 때만 적는다.
        StatusMessage = ok ? $"원복 스크립트 #{group.Id} 을(를) 여는 중(자동으로 실행하지 않는다)" : "원복 스크립트를 열지 못했다";
        return ok;
    }

    /// <summary>「이 실행 전과 비교…」: 화면 3(RB-32)을 명령으로 연다. 묶음의 기준·커밋 시점과 표를 쓴다.</summary>
    public async Task<bool> CompareAsync()
    {
        if (SelectedGroup is not { } group || !CanCompare)
        {
            StatusMessage = "기준 시점이 기록된 커밋 묶음을 고르세요";
            return false;
        }

        // 묶음 줄은 묶음의 표 전부(GroupId 로 화면 3 이 안다), 문장 줄은 그 문장의 표.
        var table = SelectedStatement is { Parent: var parent } st && ReferenceEquals(parent, group) ? st.Statement.Table : null;
        var ok = await RunCommandAsync(RollbackCommandIds.ComparePoint, new PointCompareArgs(group.Group.ProfileId, group.Id, table, null, null, null)).ConfigureAwait(true);
        if (!ok)
        {
            StatusMessage = "이 실행 전과 비교 화면을 열지 못했다";
        }

        return ok;
    }

    private async Task<bool> RunCommandAsync(string id, object arg)
    {
        try
        {
            return await CommandRunner(id, arg).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
            return false;
        }
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

    private IRollbackStore? ResolveRollbackStore()
    {
        if (!_rollbackStoreResolved && _services is not null)
        {
            _rollbackStore = _services.GetService(typeof(IRollbackStore)) as IRollbackStore;
            _rollbackStoreResolved = _rollbackStore is not null;
        }

        return _rollbackStore;
    }

    private IChangeJournalStore? ResolveStore()
    {
        if (_store is null && _services is not null)
        {
            _store = _services.GetService(typeof(IChangeJournalStore)) as IChangeJournalStore;
        }

        return _store;
    }
}
