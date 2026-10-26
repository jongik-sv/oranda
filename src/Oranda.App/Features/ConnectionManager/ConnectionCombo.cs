using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Oranda.App.Features.Execution;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>
/// 툴바 접속 콤보의 한 줄: 「번호 - 사용자@이름」 과 환경 칩(ui-shell.md 1장·6장, 오렌지 SQL Tool 콤보). 같은 DB 에 더 연 세션은 「번호 - 사용자@이름 (2)」
/// (<paramref name="Name"/> = <see cref="SessionRouter.LabelOf"/>, TSK-14-03). <paramref name="IsNewSession"/> 이면 「새 세션 — …」 줄이고
/// <paramref name="Session"/> 은 그 DB 의 접속(기본 세션)이다.
/// </summary>
public sealed record ConnectionComboItem(int Number, ISession Session, bool Broken, string? Name = null, bool IsNewSession = false, string? Tip = null, bool Enabled = true)
{
    public string Label => IsNewSession
        ? Name ?? string.Empty
        : (Name ?? string.Create(CultureInfo.InvariantCulture, $"{Number} - {Session.Profile.User}@{Session.Profile.Name}"))
          + (Broken ? UiText.Get("Execution.status.brokenSuffix", " (끊김)") : string.Empty);

    public string EnvironmentText => ConnectionStatusViewModel.EnvironmentTextOf(Session.Profile.Environment);

    public string? EnvironmentBrushKey => ConnectionStatusViewModel.EnvironmentBrushKeyOf(Session.Profile.Environment);
}

/// <summary>
/// 툴바 접속 콤보(CONN-06, TSK-03-08). 열린 접속을 연 순서로 보이고, 활성 문서의 접속(<see cref="SessionRouter.ShownSession"/>)을 고른 채로 둔다.
/// 고르면 활성 SQL 탭(PL/SQL 문서면 그 문서)이 그 접속을 쓰고 지금 접속이 바뀐다(<see cref="SessionRouter.SelectAsync"/>, TSK-12-04).
/// 이전 접속에 미커밋이 있으면 묻고, 실행 중이면 바꾸지 않고 메시지 창을 띄워 이유를 알린다. 활성 SQL 탭의 접속이 열려 있지 않으면
/// 「접속 안 됨(이름)」 을 보인다. 라우터는 접속 서비스와 함께 생기므로 그 전에는 세션 이벤트만 듣는다(시작 때 Oracle 어셈블리를 깨우지 않는다).
/// </summary>
public sealed class ConnectionComboViewModel : ViewModelBase
{
    private static readonly ConditionalWeakTable<ServiceContainer, ConnectionComboViewModel> PerContainer = [];

    private readonly ServiceContainer? _container;
    private readonly Func<SessionRouter?> _router;
    private SessionRouter? _hooked;
    private ConnectionComboItem? _selected;
    private bool _syncing;
    private bool _dropDownOpen;
    private string _placeholder = NotConnected;

    private static string NotConnected => UiText.Get("Execution.status.notConnected", "접속 안 됨");

    public ConnectionComboViewModel(IEventBus? events = null, ServiceContainer? container = null, Func<SessionRouter?>? router = null)
    {
        _container = container;
        _router = router ?? (() => SessionRouter.Find(container));
        events?.Subscribe<SessionOpened>(this, _ => Post(Refresh));
        events?.Subscribe<SessionClosed>(this, _ => Post(Refresh));
        events?.Subscribe<SessionBroken>(this, _ => Post(Refresh));
        if (container is not null)
        {
            PerContainer.AddOrUpdate(container, this);
        }

        UiText.WatchLanguage((_, _) => Post(() => Refresh(rebuild: true))); // 항목 글(끊김·환경 칩)도 다시 만든다
        Refresh();
    }

    /// <summary>컨테이너의 콤보(주 창이 만든 것). 없으면 null.</summary>
    public static ConnectionComboViewModel? For(ServiceContainer container) =>
        PerContainer.TryGetValue(container, out var vm) ? vm : null;

    /// <summary>열린 세션 줄(접속 순서, SQL 작업 공간이 활성이면 접속마다 같은 DB 에 더 연 세션이 뒤따른다).</summary>
    public ObservableCollection<ConnectionComboItem> Items { get; } = [];

    /// <summary>콤보에 보이는 줄: <see cref="Items"/> + (SQL 작업 공간이 활성이면) 「새 세션 — …」 줄(TSK-14-03).</summary>
    public ObservableCollection<ConnectionComboItem> Entries { get; } = [];

    /// <summary>고른 접속. 사용자가 바꾸면 활성 SQL 탭에 묶는다. 활성 문서 접속이 바뀌면 콤보가 따라 고른다.</summary>
    public ConnectionComboItem? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            _selected = value;
            OnPropertyChanged();
            if (!_syncing && value is { IsNewSession: true } && !_dropDownOpen)
            {
                // 닫힌 콤보에서 화살표·휠로 「새 세션」 줄까지 넘어가도 세션을 열지 않는다(펼친 목록에서 고를 때만, TSK-14-03).
                Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
            }
            else if (!_syncing && value is not null)
            {
                _ = ChooseAsync(value);
            }
        }
    }

    /// <summary>고른 것이 없을 때 콤보에 보이는 글: 「접속 안 됨」, 활성 탭 접속이 닫혔으면 「접속 안 됨(이름)」.</summary>
    public string PlaceholderText
    {
        get => _placeholder;
        private set => SetProperty(ref _placeholder, value);
    }

    public bool IsDropDownOpen
    {
        get => _dropDownOpen;
        set => SetProperty(ref _dropDownOpen, value);
    }

    /// <summary>
    /// <c>oranda.conn.switchSession</c>(오렌지 F7): 열린 접속이 있으면 콤보를 펼치고 true, 없으면 false(부른 쪽이 접속 관리자를 연다).
    /// </summary>
    public bool OpenList()
    {
        Refresh();
        if (Items.Count == 0)
        {
            return false;
        }

        IsDropDownOpen = true;
        return true;
    }

    /// <summary>목록·고른 것·빈 글을 라우터에 맞춘다.</summary>
    public void Refresh() => Refresh(rebuild: false);

    private void Refresh(bool rebuild)
    {
        var router = Hook();
        var items = router is null ? [] : ItemsOf(router);
        var entries = items.ToList();
        if (router?.NewSessionTarget is { } target)
        {
            entries.Add(NewSessionItem(router, target));
        }

        _syncing = true;
        try
        {
            if (rebuild || !items.SequenceEqual(Items))
            {
                Items.Clear();
                foreach (var item in items)
                {
                    Items.Add(item);
                }
            }

            if (rebuild || !entries.SequenceEqual(Entries))
            {
                Entries.Clear();
                foreach (var item in entries)
                {
                    Entries.Add(Items.FirstOrDefault(i => i == item) ?? item); // 세션 줄은 Items 와 같은 것(고른 것이 두 목록에서 같게)
                }
            }

            var shown = router?.ShownSession;
            Selected = shown is null ? null : Items.FirstOrDefault(i => ReferenceEquals(i.Session, shown));
            PlaceholderText = router?.ShownMissing is { } missing
                ? UiText.Format("Execution.status.notConnectedNamed", "접속 안 됨({0})", router.ProfileName(missing) ?? "?")
                : shown is { } dedicated && Selected is null
                    ? $"{UiText.Get("Execution.status.dedicated", "전용 세션")} · {dedicated.Profile.User}@{dedicated.Profile.Name}"
                    : NotConnected;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 세션 줄: 접속마다 기본 세션, SQL 작업 공간이 활성이면 그 뒤에 같은 DB 에 더 연 세션(TSK-14-03). PL/SQL 문서가 활성이면 지금처럼 접속(기본 세션)만.
    /// 툴팁 = SID(세션 모니터에서 찾게)와 그 세션을 쓰는 워크스페이스.
    /// </summary>
    private static List<ConnectionComboItem> ItemsOf(SessionRouter router)
    {
        var items = new List<ConnectionComboItem>();
        var primaries = router.Sessions;
        for (var i = 0; i < primaries.Count; i++)
        {
            var sessions = router.SqlDocumentActive ? router.SessionsOf(primaries[i].Profile.Id) : [primaries[i]];
            foreach (var s in sessions.Count == 0 ? [primaries[i]] : sessions)
            {
                items.Add(new ConnectionComboItem(i + 1, s, router.IsBroken(s), router.LabelOf(s), Tip: TipOf(router, s)));
            }
        }

        return items;
    }

    private static string TipOf(SessionRouter router, ISession session)
    {
        var sid = UiText.Format("Execution.combo.sidTip", "세션 SID {0}", session.Server.SessionId.ToString(CultureInfo.InvariantCulture));
        var workspaces = router.WorkspacesOn(session);
        return workspaces.Count == 0 ? sid : sid + " · " + string.Join(", ", workspaces.Select(w => w.Name));
    }

    /// <summary>「새 세션 — 사용자@이름 (n)」 줄. 상한(프로필당 <see cref="ConnectionService.MaxSessionsPerProfile"/>)이면 흐리고 툴팁에 이유.</summary>
    private static ConnectionComboItem NewSessionItem(SessionRouter router, ISession primary)
    {
        var profile = primary.Profile;
        var count = router.SessionsOf(profile.Id).Count;
        var full = count >= ConnectionService.MaxSessionsPerProfile;
        var name = UiText.Format("Execution.combo.newSession", "새 세션 — {0}",
            string.Create(CultureInfo.InvariantCulture, $"{profile.User}@{profile.Name} ({count + 1})"));
        var tip = full
            ? UiText.Format("Execution.combo.newSessionFull", "같은 DB 세션 상한({0}개)에 닿았습니다. 쓰지 않는 세션을 닫으세요.",
                ConnectionService.MaxSessionsPerProfile.ToString(CultureInfo.InvariantCulture))
            : UiText.Get("Execution.combo.newSessionTip", "이 워크스페이스 전용으로 같은 DB 에 세션을 하나 더 엽니다.");
        return new ConnectionComboItem(0, primary, false, name, IsNewSession: true, tip, !full);
    }

    /// <summary>고른 접속을 활성 문서에 묶는다. 끝나면(거절·취소 포함) 목록을 라우터에 다시 맞춘다. 시험이 기다릴 수 있게 돌려준다.</summary>
    internal async Task<SessionSelect?> ChooseAsync(ConnectionComboItem item)
    {
        if (_router() is not { } router)
        {
            return null;
        }

        if (item.IsNewSession)
        {
            // 「새 세션」: 상한이면 이유를 알리고, 아니면 활성 작업 공간에 같은 DB 의 새 세션을 연다(못 열면 라우터가 메시지 창에 이유를 남긴다).
            if (!item.Enabled && item.Tip is { } reason)
            {
                ExecutionLog.For(_container?.GetService<IEventBus>()).AddMessage(MessageKind.Warning, reason);
            }

            var opened = item.Enabled ? await router.NewSessionAsync().ConfigureAwait(true) : null;
            if (opened is null)
            {
                _container?.GetService<IShellUi>()?.Docking.ShowTool("oranda.panel.messages");
            }

            Refresh(); // 「새 세션」 줄이 고른 채로 남지 않게 보이는 세션을 다시 고른다
            return opened is null ? SessionSelect.Cancelled : SessionSelect.Selected;
        }

        var result = await router.SelectAsync(item.Session).ConfigureAwait(true);
        if (SessionRouter.RefusalText(result) is { } refusal)
        {
            ExecutionLog.For(_container?.GetService<IEventBus>()).AddMessage(MessageKind.Warning, refusal);
            _container?.GetService<IShellUi>()?.Docking.ShowTool("oranda.panel.messages"); // 콤보가 되돌아가는 이유를 바로 보이게
        }

        Refresh(); // 거절·취소됐으면 원래 접속으로 되돌린다
        return result;
    }

    private SessionRouter? Hook()
    {
        var router = _router();
        if (router is not null && !ReferenceEquals(router, _hooked))
        {
            if (_hooked is not null)
            {
                _hooked.Changed -= OnRouterChanged;
            }

            _hooked = router;
            router.Changed += OnRouterChanged;
        }

        return router;
    }

    private void OnRouterChanged(object? sender, EventArgs e) => Refresh();

    private static void Post(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}

/// <summary>툴바 맨 왼쪽의 접속 콤보 View. 항목 = 환경 칩 + 「번호 - 사용자@이름」.</summary>
public sealed class ConnectionComboView : UserControl
{
    public ConnectionComboView()
    {
        var combo = new ComboBox
        {
            MinWidth = 180,
            MaxWidth = 320,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            ItemTemplate = new FuncDataTemplate<ConnectionComboItem>((item, _) => ItemView(item), supportsRecycling: false),
        };
        ApplyTexts(combo);
        combo.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(ConnectionComboViewModel.Entries)));
        combo.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(ConnectionComboViewModel.Selected)) { Mode = BindingMode.TwoWay });
        combo.Bind(ComboBox.PlaceholderTextProperty, new Binding(nameof(ConnectionComboViewModel.PlaceholderText)));
        combo.Bind(ComboBox.IsDropDownOpenProperty, new Binding(nameof(ConnectionComboViewModel.IsDropDownOpen)) { Mode = BindingMode.TwoWay });
        Content = combo;
        EventHandler onLanguage = (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyTexts(combo));
        AttachedToVisualTree += (_, _) => UiText.WatchLanguage(onLanguage);
        DetachedFromVisualTree += (_, _) => UiText.UnwatchLanguage(onLanguage);
    }

    private static void ApplyTexts(ComboBox combo)
    {
        Avalonia.Automation.AutomationProperties.SetName(combo, UiText.Get("Execution.combo.name", "접속"));
        ToolTip.SetTip(combo, UiText.Get("Execution.combo.tip", "지금 접속 (활성 SQL·PL/SQL 문서의 접속). 바꾸면 그 문서의 다음 실행부터 고른 DB 로 갑니다."));
    }

    /// <summary>콤보 상자(시험이 고른다).</summary>
    public ComboBox Combo => (ComboBox)Content!;

    private static Control ItemView(ConnectionComboItem? item)
    {
        var view = ItemBody(item);
        if (item?.Tip is { } tip)
        {
            ToolTip.SetTip(view, tip); // SID·쓰는 워크스페이스, 「새 세션」 이면 설명이나 상한 이유
        }

        if (item is { Enabled: false })
        {
            view.Opacity = 0.5; // 상한에 닿은 「새 세션」
        }

        return view;
    }

    private static Control ItemBody(ConnectionComboItem? item)
    {
        var text = new TextBlock { Text = item?.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (item is { IsNewSession: true })
        {
            text.FontStyle = FontStyle.Italic; // 세션이 아니라 여는 동작이다
            return text;
        }

        if (item?.EnvironmentBrushKey is not { } key)
        {
            return text;
        }

        var chipText = new TextBlock { Text = item.EnvironmentText, FontSize = 11, FontWeight = FontWeight.SemiBold };
        chipText.Bind(TextBlock.ForegroundProperty, chipText.GetResourceObservable("OrandaEnvChipForegroundBrush"));
        var chip = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center, Child = chipText };
        chip.Bind(Border.BackgroundProperty, chip.GetResourceObservable(key));
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { chip, text } };
    }
}
