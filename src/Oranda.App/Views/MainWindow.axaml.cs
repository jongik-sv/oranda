using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Icons;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Layout;
using Oranda.App.Shell.Menus;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan LayoutSaveDelay = TimeSpan.FromSeconds(3);

    private ServiceContainer? _services;
    private ShellController? _shell;
    private ICommandRegistry? _registry;
    private IToolPanelRegistry? _panels;
    private ContextKeyService? _keys;
    private KeymapService? _keymap;
    private MenuControls? _controls;
    private KeyDispatcher? _dispatcher;
    private MainViewModel? _viewModel;
    private IReadOnlyList<MenuNode> _toolbarModel = [];
    private WindowPlacement? _lastNormal;
    private DispatcherTimer? _layoutSaveTimer;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        PerfMarks.Mark("window-created");
        Opened += OnOpenedForPerf;
    }

    /// <summary>메뉴·툴바를 가장 최근에 만든 결과(시험이 항목을 살필 때 쓴다).</summary>
    internal Menu Menus => MenuBar;

    internal StackPanel Toolbar => ToolbarPanel;

    internal Shell.Docking.DockHostView Dock => DockHost;

    internal KeyDispatcher? Keys => _dispatcher;

    internal MainViewModel? ViewModel => _viewModel;

    /// <summary>앱 조립이 끝난 뒤 창을 셸에 묶는다(메뉴·툴바·키 처리·배치 복원·종료 저장).</summary>
    public void Attach(ServiceContainer services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _shell = services.GetRequiredService<ShellController>();
        _registry = services.GetRequiredService<ICommandRegistry>();
        _panels = services.GetRequiredService<IToolPanelRegistry>();
        _keys = services.GetRequiredService<ContextKeyService>();
        _keymap = services.GetRequiredService<KeymapService>();

        _viewModel = (MainViewModel)services.Create(typeof(MainViewModel));
        DataContext = _viewModel;
        _shell.MainWindow = this;

        _controls = new MenuControls(_registry, _keys, id => _keymap.Table.SingleGesture(id), _shell.Docking);
        _dispatcher = new KeyDispatcher(() => _keymap.Table, _registry, _keys);
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);

        PaletteIcon.Content = ShellIcons.Create("find", 14);
        PaletteButton.Click += (_, _) => _shell.ShowCommandPalette();
        ToolbarHost.SizeChanged += (_, _) => UpdateToolbarOverflow();
        SizeChanged += (_, _) => UpdateStatusBarCollapse();
        PositionChanged += (_, _) => RememberNormalBounds();
        Resized += (_, _) => RememberNormalBounds();

        _registry.CommandsChanged += (_, _) => Dispatcher.UIThread.Post(RebuildMenus);
        _panels.PanelsChanged += (_, _) => Dispatcher.UIThread.Post(RebuildMenus);
        _shell.Docking.LayoutRebuilt += (_, _) => RebuildMenus();
        _shell.Docking.LayoutChanged += (_, _) => ScheduleLayoutSave();
        _keys.Changed += (_, _) => Dispatcher.UIThread.Post(() => _controls?.RefreshEnablement());
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        services.GetRequiredService<IEventBus>().Subscribe<SettingsChanged>(this, e =>
        {
            if (e.Section == "keymapId")
            {
                Dispatcher.UIThread.Post(RebuildMenus);
            }
        });

        RebuildMenus();
        ShellContextMenus.Factory = surface =>
            _controls is null || _registry is null || _keymap is null || _shell is null
                ? []
                : _controls.BuildContextItems(MenuModelBuilder.BuildContext(_registry.Commands, _shell.Text, _keymap.ShortcutText, surface));
        ApplyEnvironment();
        ApplyPlacement(_shell.SavedWindow);
    }

    /// <summary>레지스트리·키맵으로 메뉴 막대와 툴바를 다시 만든다.</summary>
    internal void RebuildMenus()
    {
        if (_controls is null || _registry is null || _panels is null || _keymap is null || _shell is null)
        {
            return;
        }

        var text = _shell.Text;
        var bar = MenuModelBuilder.BuildMenuBar(_registry.Commands, text, _keymap.ShortcutText, _panels.Panels);
        MenuBar.ItemsSource = _controls.BuildMenuBar(bar);

        _toolbarModel = MenuModelBuilder.BuildToolbar(_registry.Commands, text, _keymap.ShortcutText);
        ToolbarPanel.Children.Clear();
        ToolbarPanel.Children.AddRange(_controls.BuildToolbarItems(_toolbarModel));
        UpdateToolbarOverflow();
        _controls.RefreshEnablement();
    }

    /// <summary>툴바 폭이 모자라면 오른쪽 단추부터 「…」 메뉴로 접는다.</summary>
    internal void UpdateToolbarOverflow()
    {
        if (_controls is null)
        {
            return;
        }

        var children = ToolbarPanel.Children.ToList();
        foreach (var c in children)
        {
            c.IsVisible = true;
            c.Measure(Size.Infinity);
        }

        var widths = children.Select(c => c.DesiredSize.Width).ToList();
        var reserved = Math.Max(PaletteButton.DesiredSize.Width, PaletteButton.MinWidth) + 12;
        var available = Math.Max(0, ToolbarHost.Bounds.Width - reserved);
        if (ToolbarHost.Bounds.Width <= 0)
        {
            OverflowButton.IsVisible = false;
            return;
        }

        var visible = ToolbarOverflow.VisibleCount(widths, available, ToolbarPanel.Spacing, 40);
        var hidden = new List<Control>();
        for (var i = visible; i < children.Count; i++)
        {
            children[i].IsVisible = false;
            hidden.Add(children[i]);
        }

        OverflowButton.IsVisible = hidden.Count > 0;
        var flyout = new MenuFlyout();
        foreach (var button in hidden.OfType<Button>())
        {
            flyout.Items.Add(new MenuItem
            {
                Header = AutomationProperties.GetName(button),
                Command = button.Command,
                Icon = ShellIcons.Create(button.Tag as string),
            });
        }

        OverflowButton.Flyout = flyout;
    }

    /// <summary>레이아웃을 저장하지 않고 닫는다(시험 정리용).</summary>
    internal void CloseWithoutSaving()
    {
        _closeConfirmed = true;
        Close();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && _shell is not null)
        {
            e.Cancel = true;
            try
            {
                _layoutSaveTimer?.Stop();
                await _shell.SaveLayoutAsync(CapturePlacement());
            }
            catch (Exception ex)
            {
                Trace.TraceError("layout.json 저장 실패: {0}", ex);
            }

            _closeConfirmed = true;
            Close();
            return;
        }

        base.OnClosing(e);
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_dispatcher is not null && !e.Handled && _dispatcher.Handle(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Environment))
        {
            ApplyEnvironment();
        }
    }

    private void ApplyEnvironment()
    {
        var env = _viewModel?.Environment;
        EnvironmentBorder.Classes.Set("env-production", env == ConnectionEnvironment.Production);
        EnvironmentBorder.Classes.Set("env-test", env == ConnectionEnvironment.Test);
        EnvironmentBorder.Classes.Set("env-development", env == ConnectionEnvironment.Development);
        EnvironmentBorder.BorderThickness = env is null ? new Thickness(0) : new Thickness(3);
        StatusBarBorder.Classes.Set("env-production", env == ConnectionEnvironment.Production);
    }

    /// <summary>좁을 때 상태 표시줄 구간을 순위(CollapseRank)가 큰 것부터 접는다(ui-shell.md 1.2).</summary>
    private void UpdateStatusBarCollapse()
    {
        if (_viewModel is null)
        {
            return;
        }

        var ranks = _viewModel.StatusItems.Select(i => i.Definition.CollapseRank).Where(r => r > 0).Distinct().OrderByDescending(r => r).ToList();
        for (var i = 0; i < ranks.Count; i++)
        {
            var hide = Bounds.Width < 1100 - (i * 120);
            foreach (var entry in _viewModel.StatusItems.Where(s => s.Definition.CollapseRank == ranks[i]))
            {
                entry.Collapsed = hide;
            }
        }
    }

    private void ScheduleLayoutSave()
    {
        if (_shell is null)
        {
            return;
        }

        _layoutSaveTimer ??= new DispatcherTimer { Interval = LayoutSaveDelay };
        _layoutSaveTimer.Tick -= OnLayoutSaveTick;
        _layoutSaveTimer.Tick += OnLayoutSaveTick;
        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
    }

    private async void OnLayoutSaveTick(object? sender, EventArgs e)
    {
        _layoutSaveTimer?.Stop();
        try
        {
            await _shell!.SaveLayoutAsync(CapturePlacement());
        }
        catch (Exception ex)
        {
            Trace.TraceError("layout.json 저장 실패: {0}", ex);
        }
    }

    private IReadOnlyList<ScreenArea> CurrentScreens()
    {
        if (Screens is null)
        {
            return [];
        }

        return [.. Screens.All.Select(s => new ScreenArea(
            ScreenName(s),
            (int)Math.Round(s.WorkingArea.X / s.Scaling),
            (int)Math.Round(s.WorkingArea.Y / s.Scaling),
            (int)Math.Round(s.WorkingArea.Width / s.Scaling),
            (int)Math.Round(s.WorkingArea.Height / s.Scaling),
            s.IsPrimary))];
    }

    private static string ScreenName(Avalonia.Platform.Screen s) => s.DisplayName ?? $"{s.Bounds.X},{s.Bounds.Y}";

    /// <summary>저장된 창 위치로 열 자리를 정한다(없는 화면·화면 밖이면 주 화면 가운데, 최소 960×600).</summary>
    internal void ApplyPlacement(WindowPlacement? saved)
    {
        var screens = CurrentScreens();
        var placement = WindowPlacementPolicy.Resolve(saved, screens);
        var area = screens.FirstOrDefault(s => s.Name == placement.Screen) ?? (screens.Count > 0 ? screens[0] : null);
        var (minW, minH) = area is null ? (WindowPlacementPolicy.MinWidth, WindowPlacementPolicy.MinHeight) : WindowPlacementPolicy.MinimumFor(area);
        MinWidth = minW;
        MinHeight = minH;
        Width = placement.Width;
        Height = placement.Height;

        var screen = Screens?.All.FirstOrDefault(s => ScreenName(s) == placement.Screen) ?? Screens?.Primary;
        if (screen is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)Math.Round(placement.X * screen.Scaling), (int)Math.Round(placement.Y * screen.Scaling));
        }

        _lastNormal = new WindowPlacement { X = placement.X, Y = placement.Y, Width = placement.Width, Height = placement.Height, Screen = placement.Screen };
        WindowState = placement.State switch
        {
            WindowStateKind.Maximized => WindowState.Maximized,
            WindowStateKind.FullScreen => WindowState.FullScreen,
            _ => WindowState.Normal,
        };
    }

    private void RememberNormalBounds()
    {
        if (WindowState != WindowState.Normal || Screens?.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        _lastNormal = new WindowPlacement
        {
            X = (int)Math.Round(Position.X / screen.Scaling),
            Y = (int)Math.Round(Position.Y / screen.Scaling),
            Width = (int)Math.Round(ClientSize.Width),
            Height = (int)Math.Round(ClientSize.Height),
            Screen = ScreenName(screen),
        };
    }

    /// <summary>지금 창 위치·크기·상태(최대화일 때는 복원 크기). 최소화는 저장하지 않는다.</summary>
    internal WindowPlacement? CapturePlacement()
    {
        RememberNormalBounds();
        if (_lastNormal is null)
        {
            return null;
        }

        return _lastNormal with
        {
            State = WindowState switch
            {
                WindowState.Maximized => WindowStateKind.Maximized,
                WindowState.FullScreen => WindowStateKind.FullScreen,
                _ => WindowStateKind.Normal,
            },
        };
    }

    private void OnOpenedForPerf(object? sender, EventArgs e)
    {
        Opened -= OnOpenedForPerf;

        // 첫 rAF 콜백은 첫 프레임을 그리기 *전* 이다(R2R 에서 실제 프레임보다 약 0.5 s 이름, TSK-01-04).
        // 두 번째 콜백 = 첫 프레임이 한 번 그려진 뒤. 그 뒤 입력보다 낮은 우선순위 작업이 돌면 입력을 바로 받을 수 있다.
        RequestAnimationFrame(_ => RequestAnimationFrame(_ =>
        {
            PerfMarks.Mark("first-render");
            Dispatcher.UIThread.Post(() => PerfMarks.Mark("input-ready"), DispatcherPriority.ApplicationIdle);
        }));
    }
}
