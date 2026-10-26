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

/// <summary>툴바 접속 콤보의 한 줄: 「번호 - 사용자@이름」 과 환경 칩(ui-shell.md 1장·6장, 오렌지 SQL Tool 콤보).</summary>
public sealed record ConnectionComboItem(int Number, ISession Session, bool Broken)
{
    public string Label => string.Create(CultureInfo.InvariantCulture, $"{Number} - {Session.Profile.User}@{Session.Profile.Name}")
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

    public ObservableCollection<ConnectionComboItem> Items { get; } = [];

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
            if (!_syncing && value is not null)
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
        var sessions = router?.Sessions ?? [];
        var items = sessions.Select((s, i) => new ConnectionComboItem(i + 1, s, router!.IsBroken(s))).ToList();
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

    /// <summary>고른 접속을 활성 문서에 묶는다. 끝나면(거절·취소 포함) 목록을 라우터에 다시 맞춘다. 시험이 기다릴 수 있게 돌려준다.</summary>
    internal async Task<SessionSelect?> ChooseAsync(ConnectionComboItem item)
    {
        if (_router() is not { } router)
        {
            return null;
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
        combo.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(ConnectionComboViewModel.Items)));
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
        var text = new TextBlock { Text = item?.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
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
