using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ChangeBundle;

/// <summary>
/// <c>oranda.panel.changeBundle</c> — 반영 묶음 창(테이블 편집기 설계 9.2): 「지금 묶음」 고르기·＋·이름 바꾸기·지우기, 대상 소유자·DBMS,
/// 문장 표(체크·#·요약·출처·개발·테스트·운영), ↑↓·－, 전문 보기·고치기(TE-14), 「실행…」(TE-15)·「의뢰용 스크립트 저장…」(9.4).
/// 저장소(<see cref="IChangeBundleStore"/>)와 서비스(<see cref="IChangeBundleService"/>)는 처음 쓸 때 꺼낸다(D-006). 자동 넣기는 Platform 의 기록기가 한다.
/// </summary>
[OrandaToolPanel("oranda.panel.changeBundle", ToolPanelKind.Tool, DockLocation.Bottom, Order = 46)]
public sealed partial class ChangeBundleViewModel : ToolPanelViewModelBase, IDisposable
{
    /// <summary>넣은 건수를 보이는 시간.</summary>
    internal static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(4);

    private readonly IServiceProvider? _services;
    private readonly List<IDisposable> _subscriptions = [];
    private IChangeBundleStore? _store;
    private IChangeBundleService? _service;
    private IOwnerRewriter? _rewriter;
    private ILanguageService? _language;
    private IExecutionService? _execution;
    private IEventBus? _bus;
    private bool _resolved;
    private bool _loading;
    private int _generation;
    private CancellationTokenSource? _noticeCts;

    /// <summary>시험·디자이너용: 서비스 없이 만든다(빈 목록).</summary>
    public ChangeBundleViewModel()
    {
    }

    /// <summary>셸(DI)이 쓰는 생성자. 묶음이 바뀌면(<see cref="ChangeBundleChanged"/>) 다시 읽고 넣은 건수를 잠깐 보인다.</summary>
    public ChangeBundleViewModel(IServiceProvider services)
        : this()
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        if (services.GetService(typeof(IEventBus)) is IEventBus bus)
        {
            _bus = bus;
            _subscriptions.Add(bus.Subscribe<ChangeBundleChanged>(this, e => PostToUi(() => OnBundleChanged(e))));
        }

        SessionsProvider = () => OpenSessions(services);
        CurrentSessionProvider = () => SessionRouter.ShownOf(services);
        RouteSession = s => SessionRouter.ForTool(services, s) ?? s;
        ConfigureRun = run =>
        {
            run.ChecksFor = s => DbmsServices.For<Oranda.Core.Schema.TableEdit.ITableEditChecks>(services, s);
            run.PurgesOnDropFor = s => DbmsServices.For<Oranda.Core.Schema.TableEdit.ITableEditDialect>(services, s)?.PurgesOnDrop ?? false;
            run.Policy = services.GetService(typeof(IExecutionPolicy)) as IExecutionPolicy;
            run.Interaction = services.GetService(typeof(IExecutionInteraction)) as IExecutionInteraction;
            run.ConfirmPendingCommit = token => Dialogs.ConfirmAsync("묶음 실행",
                "이 세션에 커밋하지 않은 변경이 있다. DDL 을 실행하면 그 변경도 커밋된다. 계속할까?", "계속", token);
            run.AttachReview(r => Ai.DdlReviewShell.Create(services, r.ReviewInput)); // 「AI 검토」(TSK-15-09)
        };
        _ = ReloadAsync();
    }

    /// <summary>시험용: 저장소·서비스를 직접 넣는다(대화상자는 <see cref="ChangeBundleDialogs.Current"/>).</summary>
    internal static ChangeBundleViewModel ForTests(IChangeBundleStore store, IChangeBundleService? service, IOwnerRewriter? rewriter = null,
        ILanguageService? language = null, IExecutionService? execution = null, IEventBus? bus = null) =>
        new(store, service, rewriter, language, execution, bus, TestsOnly.Marker);

    /// <summary>
    /// <see cref="ForTests"/> 의 생성자. 컨테이너는 인자가 가장 많은 생성자를 고르므로 컨테이너가 채울 수 없는 표지 인자를 둔다(변경 일지와 같은 꼴).
    /// </summary>
    private ChangeBundleViewModel(IChangeBundleStore store, IChangeBundleService? service, IOwnerRewriter? rewriter, ILanguageService? language,
        IExecutionService? execution, IEventBus? bus, TestsOnly marker)
        : this()
    {
        if (!marker.Set)
        {
            throw new ArgumentException("ForTests 로만 만든다.", nameof(marker));
        }

        _store = store;
        _service = service;
        _rewriter = rewriter;
        _language = language;
        _execution = execution;
        _bus = bus;
        _resolved = true;
        PostToUi = a => a();
        if (bus is not null)
        {
            _subscriptions.Add(bus.Subscribe<ChangeBundleChanged>(this, e => PostToUi(() => OnBundleChanged(e))));
        }
    }

    /// <summary>컨테이너가 만들 수 없는 표지(<see cref="ForTests"/>).</summary>
    private readonly struct TestsOnly
    {
        public static readonly TestsOnly Marker = new(true);

        private TestsOnly(bool set) => Set = set;

        public bool Set { get; }
    }

    /// <summary>모든 묶음(만든 순서). 고른 것이 「지금 묶음」.</summary>
    public ObservableCollection<ChangeBundleInfo> Bundles { get; } = [];

    /// <summary>지금 묶음의 문장(화면 순서).</summary>
    public ObservableCollection<ChangeBundleRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBundle), nameof(DbmsText), nameof(TargetOwnersText), nameof(CanRunOrSave))]
    private ChangeBundleInfo? _currentBundle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ChangeBundleRow? _selectedRow;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>제목줄에 잠깐 보이는 넣은 건수(「+2」). 비면 안 보인다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _noticeText = string.Empty;

    public bool HasNotice => NoticeText.Length > 0;

    public bool HasBundle => CurrentBundle is not null;

    public bool HasSelection => SelectedRow is not null;

    /// <summary>체크한 문장이 있다(「실행…」·「의뢰용 스크립트 저장…」).</summary>
    public bool CanRunOrSave => CurrentBundle is not null && Rows.Any(r => r.IsChecked);

    /// <summary>도구줄 오른쪽 대상 DBMS(빈 묶음은 첫 문장을 따른다).</summary>
    public string DbmsText => CurrentBundle is null ? string.Empty : CurrentBundle.Dbms?.ToString() ?? "DBMS: 첫 문장을 따름";

    /// <summary>대상 소유자(「대상: SAMPLE, SAMPLE2」).</summary>
    public string TargetOwnersText => CurrentBundle is null
        ? string.Empty
        : "대상: " + (CurrentBundle.TargetOwners.Count == 0 ? "첫 문장을 따름" : string.Join(", ", CurrentBundle.TargetOwners));

    /// <summary>이벤트를 UI 스레드로 넘기는 방법. 시험은 바로 실행하도록 바꾼다.</summary>
    public Action<Action> PostToUi { get; set; } = UiThread.Post;

    /// <summary>「실행…」 창에 보일 열린 접속. 셸은 접속 서비스에서 읽는다. 시험이 바꿔 끼운다.</summary>
    internal Func<IReadOnlyList<ISession>> SessionsProvider { get; set; } = () => [];

    /// <summary>새 묶음의 대상 소유자를 채울 지금 접속. 시험이 바꿔 끼운다.</summary>
    internal Func<ISession?> CurrentSessionProvider { get; set; } = () => null;

    /// <summary>새 SQL 편집기 탭을 연다(글). 시험이 바꿔 끼운다.</summary>
    internal Func<string, bool> EditorOpener { get; set; } = OpenInNewEditor;

    /// <summary>「실행…」 이 고른 접속에서 실제로 보낼 세션(<see cref="SessionRouter.ForTool"/>). 시험은 그대로.</summary>
    internal Func<ISession, ISession> RouteSession { get; set; } = s => s;

    /// <summary>「실행…」 창의 검사·정책·확인 연결(셸 = 서비스에서). 시험이 바꿔 끼운다.</summary>
    internal Action<ChangeBundleRunViewModel>? ConfigureRun { get; set; }

    /// <summary>오늘 날짜(새 묶음 기본 이름). 시험이 바꿔 끼운다.</summary>
    internal Func<DateTime> Today { get; set; } = () => DateTime.Now;

    /// <summary>목록을 새로 채운 뒤 알린다(시험용).</summary>
    public event EventHandler? Reloaded;

    private static IChangeBundleDialogs Dialogs => ChangeBundleDialogs.Current;

    public void Dispose()
    {
        foreach (var s in _subscriptions)
        {
            s.Dispose();
        }

        _subscriptions.Clear();
        _noticeCts?.Cancel();
        _noticeCts?.Dispose();
        _noticeCts = null;
    }

    /// <summary>사용자가 「지금 묶음」 목록에서 다른 묶음을 골랐다 → 그것을 「지금 묶음」으로 둔다.</summary>
    partial void OnCurrentBundleChanged(ChangeBundleInfo? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        _ = SwitchCurrentAsync(value.Id);
    }

    private async Task SwitchCurrentAsync(long id)
    {
        if (ResolveStore() is not { } store)
        {
            return;
        }

        await WriteAsync(() => store.SetCurrentAsync(id, CancellationToken.None)).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 저장소 쓰기 하나. 실패하면(<c>oranda.db</c> 잠김 등) 상태 줄에 이유를 두고 false — 화면은 부르는 쪽이 다시 읽어 저장소와 맞춘다.
    /// </summary>
    private async Task<bool> WriteAsync(Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = "반영 묶음에 쓰지 못했다: " + ex.Message;
            return false;
        }
    }

    private void OnBundleChanged(ChangeBundleChanged e)
    {
        if (e.Added > 0 || e.Marked > 0)
        {
            ShowNotice(e.Added > 0 ? $"+{e.Added}" : $"✔{e.Marked}");
        }

        _ = ReloadAsync();
    }

    /// <summary>넣은 건수를 제목줄에 잠깐 보인다(창을 열지는 않는다, 설계 9.3).</summary>
    internal void ShowNotice(string text)
    {
        _noticeCts?.Cancel();
        _noticeCts?.Dispose();
        var cts = _noticeCts = new CancellationTokenSource();
        NoticeText = text;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(NoticeDuration, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            PostToUi(() =>
            {
                if (!cts.IsCancellationRequested)
                {
                    NoticeText = string.Empty;
                }
            });
        });
    }

    /// <summary>묶음 목록·「지금 묶음」·그 문장과 실행 표시를 새로 읽는다.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        if (ResolveStore() is not { } store)
        {
            return;
        }

        var generation = ++_generation;
        IReadOnlyList<ChangeBundleInfo> bundles;
        IReadOnlyList<ChangeBundleItem> items = [];
        IReadOnlyDictionary<long, ChangeBundleRunMarks> marks = new Dictionary<long, ChangeBundleRunMarks>();
        try
        {
            bundles = await store.ListBundlesAsync(ct).ConfigureAwait(true);
            if (bundles.FirstOrDefault(b => b.IsCurrent) is { } current)
            {
                items = await store.ListItemsAsync(current.Id, ct).ConfigureAwait(true);
                marks = await store.GetRunMarksAsync(current.Id, ct).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = "반영 묶음을 읽지 못했다: " + ex.Message;
            return;
        }

        if (generation != _generation)
        {
            return;
        }

        var selectedId = SelectedRow?.Id;
        _loading = true;
        try
        {
            Bundles.Clear();
            foreach (var b in bundles)
            {
                Bundles.Add(b);
            }

            CurrentBundle = Bundles.FirstOrDefault(b => b.IsCurrent);
            foreach (var r in Rows)
            {
                r.CheckChanged -= OnRowCheckChanged;
            }

            Rows.Clear();
            var number = 1;
            foreach (var item in items)
            {
                var row = new ChangeBundleRow(item, number++, marks.TryGetValue(item.Id, out var m) ? m : ChangeBundleRunMarks.None);
                row.CheckChanged += OnRowCheckChanged;
                Rows.Add(row);
            }

            SelectedRow = selectedId is { } id ? Rows.FirstOrDefault(r => r.Id == id) : null;
        }
        finally
        {
            _loading = false;
        }

        if (!store.IsWritable)
        {
            StatusMessage = "반영 묶음 저장소에 쓸 수 없다(oranda.db). 보기만 한다.";
        }
        else if (CurrentBundle is null)
        {
            StatusMessage = Bundles.Count == 0
                ? "반영 묶음이 없다. ＋ 로 새 묶음을 만들면 SQL 편집기·테이블 편집기에서 실행한 DDL 이 자동으로 들어간다."
                : "「지금 묶음」이 없다. 목록에서 고르면 DDL 이 그 묶음에 들어간다.";
        }
        else
        {
            StatusMessage = $"{Rows.Count}문장 · 체크 {Rows.Count(r => r.IsChecked)}";
        }

        OnPropertyChanged(nameof(CanRunOrSave));
        OnPropertyChanged(nameof(DbmsText));
        OnPropertyChanged(nameof(TargetOwnersText));
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    private void OnRowCheckChanged(ChangeBundleRow row, bool value)
    {
        if (ResolveStore() is { } store)
        {
            _ = SetCheckedAsync(store, row, value);
        }
    }

    private async Task SetCheckedAsync(IChangeBundleStore store, ChangeBundleRow row, bool value)
    {
        if (!await WriteAsync(() => store.SetCheckedAsync(row.Id, value, CancellationToken.None)).ConfigureAwait(true))
        {
            var message = StatusMessage;
            await ReloadAsync().ConfigureAwait(true); // 체크 표시를 저장소 값으로 되돌린다
            StatusMessage = message;
            return;
        }

        StatusMessage = $"{Rows.Count}문장 · 체크 {Rows.Count(r => r.IsChecked)}";
        OnPropertyChanged(nameof(CanRunOrSave));
    }

    /// <summary>＋ 새 묶음: 이름을 묻고 「지금 묶음」으로 만든다. 대상 소유자는 지금 접속의 현재 스키마(TE-24).</summary>
    public async Task NewBundleAsync(CancellationToken ct = default)
    {
        if (ResolveService() is not { } service)
        {
            return;
        }

        var initial = "반영 묶음 " + Today().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var name = (await Dialogs.PromptAsync("새 반영 묶음", "묶음 이름", initial, ct).ConfigureAwait(true))?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var session = CurrentSessionProvider();
        var origin = session is null ? null : service.OriginOf(session.Id) ?? ChangeBundleOrigin.From(session);
        ChangeBundleInfo? created = null;
        if (!await WriteAsync(async () => created = await service.CreateAsync(name, origin, makeCurrent: true, ct).ConfigureAwait(true)).ConfigureAwait(true))
        {
            return;
        }

        if (created is null)
        {
            StatusMessage = "새 묶음을 만들지 못했다(저장소에 쓸 수 없음).";
            return;
        }

        await ReloadAsync(ct).ConfigureAwait(true);
    }

    /// <summary>지금 묶음 이름 바꾸기.</summary>
    public async Task RenameBundleAsync(CancellationToken ct = default)
    {
        if (CurrentBundle is not { } bundle || ResolveStore() is not { } store)
        {
            return;
        }

        var name = (await Dialogs.PromptAsync("묶음 이름 바꾸기", "묶음 이름", bundle.Name, ct).ConfigureAwait(true))?.Trim();
        if (string.IsNullOrEmpty(name) || name == bundle.Name)
        {
            return;
        }

        if (await WriteAsync(() => store.RenameAsync(bundle.Id, name, ct)).ConfigureAwait(true))
        {
            await ReloadAsync(ct).ConfigureAwait(true);
        }
    }

    /// <summary>지금 묶음 지우기(확인). 문장·실행 기록도 함께 지운다.</summary>
    public async Task DeleteBundleAsync(CancellationToken ct = default)
    {
        if (CurrentBundle is not { } bundle || ResolveStore() is not { } store)
        {
            return;
        }

        var ok = await Dialogs.ConfirmAsync("묶음 지우기",
            $"「{bundle.Name}」 묶음과 그 문장 {Rows.Count}개·실행 표시를 지운다. 되돌릴 수 없다.", "지우기", ct).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        if (await WriteAsync(() => store.DeleteAsync(bundle.Id, ct)).ConfigureAwait(true))
        {
            await ReloadAsync(ct).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 대상 소유자 고치기(TE-24): 쉼표로 나눈 이름. 따옴표 없는 일반 이름은 대문자로. 비우면 첫 문장을 따른다.
    /// </summary>
    public async Task EditTargetOwnersAsync(CancellationToken ct = default)
    {
        if (CurrentBundle is not { } bundle || ResolveStore() is not { } store)
        {
            return;
        }

        var text = await Dialogs.PromptAsync("대상 소유자", "이 소유자의 객체를 바꾸는 DDL 만 자동으로 넣는다(쉼표로 나눔)",
            string.Join(", ", bundle.TargetOwners), ct).ConfigureAwait(true);
        if (text is null)
        {
            return;
        }

        var owners = ParseOwners(text);
        if (owners.SequenceEqual(bundle.TargetOwners, StringComparer.Ordinal))
        {
            return;
        }

        // 창이 떠 있는 사이 자동 넣기가 빈 묶음의 DBMS 를 정했을 수 있다 — 쓰기 직전 값을 다시 읽어 그대로 둔다.
        var write = await WriteAsync(async () =>
        {
            var latest = await store.GetBundleAsync(bundle.Id, ct).ConfigureAwait(true);
            await store.UpdateTargetAsync(bundle.Id, latest?.Dbms ?? bundle.Dbms, owners, ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
        if (write)
        {
            await ReloadAsync(ct).ConfigureAwait(true);
        }
    }

    internal static IReadOnlyList<string> ParseOwners(string text)
    {
        var owners = new List<string>();
        foreach (var part in text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = part;
            if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
            {
                name = name[1..^1];
            }
            else if (Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_$#]*$"))
            {
                name = name.ToUpperInvariant();
            }

            if (name.Length > 0 && !owners.Contains(name, StringComparer.Ordinal))
            {
                owners.Add(name);
            }
        }

        return owners;
    }

    /// <summary>고른 문장을 한 칸 위로.</summary>
    public Task MoveUpAsync(CancellationToken ct = default) => MoveAsync(-1, ct);

    /// <summary>고른 문장을 한 칸 아래로.</summary>
    public Task MoveDownAsync(CancellationToken ct = default) => MoveAsync(+1, ct);

    private async Task MoveAsync(int delta, CancellationToken ct)
    {
        if (CurrentBundle is not { } bundle || SelectedRow is not { } row || ResolveStore() is not { } store)
        {
            return;
        }

        var ids = Rows.Select(r => r.Id).ToList();
        var index = ids.IndexOf(row.Id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ids.Count)
        {
            return;
        }

        (ids[index], ids[target]) = (ids[target], ids[index]);
        if (await WriteAsync(() => store.ReorderAsync(bundle.Id, ids, ct)).ConfigureAwait(true))
        {
            await ReloadAsync(ct).ConfigureAwait(true);
        }
    }

    /// <summary>－: 고른 문장을 묶음에서 지운다(확인).</summary>
    public async Task RemoveSelectedAsync(CancellationToken ct = default)
    {
        if (SelectedRow is not { } row || ResolveStore() is not { } store)
        {
            return;
        }

        var ok = await Dialogs.ConfirmAsync("문장 지우기", $"{row.Number}번 문장을 묶음에서 지운다(실행 표시도 지워진다).\n\n{row.SummaryText}", "지우기", ct)
            .ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        if (await WriteAsync(() => store.DeleteItemAsync(row.Id, ct)).ConfigureAwait(true))
        {
            SelectedRow = null;
            await ReloadAsync(ct).ConfigureAwait(true);
        }
    }

    /// <summary>두 번 누르기: 전문 보기·고치기(고치면 실행 표시를 지운다 TE-14)·편집기에서 열기.</summary>
    public async Task OpenSelectedAsync(CancellationToken ct = default)
    {
        if (SelectedRow is not { } row || ResolveStore() is not { } store)
        {
            return;
        }

        var result = await Dialogs.EditTextAsync($"{row.Number}번 문장", row.FullText, ct).ConfigureAwait(true);
        if (result is null)
        {
            return;
        }

        if (result.OpenInEditor)
        {
            EditorOpener(row.FullText);
            return;
        }

        if (result.Text is not { } text || string.Equals(text, row.FullText, StringComparison.Ordinal))
        {
            return;
        }

        // 한 문장만, 분리기 글로(끝 ; 는 떼고 PL/SQL 단위는 END; 까지 — 묶음 글 규칙).
        Resolve();
        if (ChangeBundleScript.NormalizeEdited(text, _language) is not { } normalized)
        {
            StatusMessage = "고친 글을 받지 않았다: 묶음 문장 하나에는 문장 하나만 둔다.";
            return;
        }

        if (!string.Equals(normalized, row.FullText, StringComparison.Ordinal)
            && await WriteAsync(() => store.UpdateItemTextAsync(row.Id, normalized, ct)).ConfigureAwait(true))
        {
            await ReloadAsync(ct).ConfigureAwait(true);
        }
    }

    /// <summary>「실행…」: 접속을 고르는 창을 띄우고, 끝나면 실행 표시를 다시 읽는다.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (CurrentBundle is not { } bundle || CheckedItems() is not { Count: > 0 } items)
        {
            return;
        }

        Resolve();
        var vm = new ChangeBundleRunViewModel(bundle, items, SessionsProvider(), _service, _rewriter, _language, _execution, _bus)
        {
            Route = RouteSession,
        };
        ConfigureRun?.Invoke(vm);
        if (vm.Connections.Count == 0)
        {
            await Dialogs.NoticeAsync("묶음 실행", "열린 접속이 없다. 실행할 접속을 먼저 연다.", ct).ConfigureAwait(true);
            return;
        }

        await Dialogs.ShowRunAsync(vm, ct).ConfigureAwait(true);
        if (vm.Finished)
        {
            await ReloadAsync(ct).ConfigureAwait(true);
            StatusMessage = vm.StatusText;
        }
    }

    /// <summary>「의뢰용 스크립트 저장…」 창.</summary>
    public async Task SaveScriptAsync(CancellationToken ct = default)
    {
        if (CurrentBundle is not { } bundle || CheckedItems() is not { Count: > 0 } items)
        {
            return;
        }

        Resolve();
        var vm = new ChangeBundleSaveViewModel(bundle, items, _rewriter, _language);
        await Dialogs.ShowSaveAsync(vm, ct).ConfigureAwait(true);
    }

    private List<ChangeBundleItem> CheckedItems() => [.. Rows.Where(r => r.IsChecked).Select(r => r.Item)];

    private static bool OpenInNewEditor(string text)
    {
        SqlEditorHost.Current.NewDocument(text);
        return true;
    }

    /// <summary>지금 열린 접속(프로필마다 첫 작업 세션, 연 순서). 접속 서비스가 없으면 세션 관리자의 작업 세션.</summary>
    private static IReadOnlyList<ISession> OpenSessions(IServiceProvider services)
    {
        if (services.GetRegisteredOrDefault<IConnectionService>() is { } connections)
        {
            return [.. connections.Sessions.Where(s => s.State is not (SessionState.Closed or SessionState.Broken))];
        }

        return [.. (services.GetRegisteredOrDefault<ISessionManager>()?.Sessions ?? [])
            .Where(s => s.Purpose == SessionPurpose.Work && s.State is not (SessionState.Closed or SessionState.Broken))];
    }

    private IChangeBundleStore? ResolveStore()
    {
        Resolve();
        return _store;
    }

    private IChangeBundleService? ResolveService()
    {
        Resolve();
        return _service;
    }

    private void Resolve()
    {
        if (_resolved || _services is null)
        {
            return;
        }

        _resolved = true;
        _store = _services.GetService(typeof(IChangeBundleStore)) as IChangeBundleStore;
        _service = _services.GetService(typeof(IChangeBundleService)) as IChangeBundleService;
        _rewriter = _services.GetService(typeof(IOwnerRewriter)) as IOwnerRewriter;
        _language = _services.GetService(typeof(ILanguageService)) as ILanguageService;
        _execution = _services.GetService(typeof(IExecutionService)) as IExecutionService;
    }
}
