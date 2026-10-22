using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
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
    private OptionTextGuard? _optionText;
    private MainViewModel? _viewModel;
    private IReadOnlyList<MenuNode> _toolbarModel = [];
    private readonly List<(Window Window, NativeMenu Root)> _floatMenus = [];
    private WindowPlacement? _lastNormal;
    private DispatcherTimer? _layoutSaveTimer;
    private bool _closeConfirmed;
    private bool _closing;
    private bool _exitWithoutConfirm;

    public MainWindow()
    {
        InitializeComponent();
        PerfMarks.Mark("window-created");
        Opened += OnOpenedForPerf;
    }

    /// <summary>메뉴·툴바를 가장 최근에 만든 결과(시험이 항목을 살필 때 쓴다).</summary>
    internal Menu Menus => MenuBar;

    /// <summary>
    /// true 면 메뉴를 창 안 <see cref="Menu"/> 대신 시스템 메뉴 막대(<see cref="NativeMenu"/>)로 보인다(macOS). 창을 셸에 묶기 전에 정한다.
    /// 시험은 macOS 가 아니어도 켤 수 있다.
    /// </summary>
    internal bool UseNativeMenu { get; set; }

    /// <summary>창에 붙은 시스템 메뉴(<see cref="UseNativeMenu"/> 일 때만 값이 있다).</summary>
    internal NativeMenu? NativeMenuBar => NativeMenu.GetMenu(this);

    internal StackPanel Toolbar => ToolbarPanel;

    internal Shell.Docking.DockHostView Dock => DockHost;

    internal KeyDispatcher? Keys => _dispatcher;

    internal MainViewModel? ViewModel => _viewModel;

    private static IEnumerable<string?> DroppedPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()) ?? [];

    /// <summary>Orange 작업 공간(.sqd)을 끌어 오면 놓을 수 있게 한다.</summary>
    private void OnWorkspaceDragOver(object? sender, DragEventArgs e)
    {
        if (DroppedWorkspaceFiles.Pick(DroppedPaths(e)).Count > 0)
        {
            e.DragEffects = DragDropEffects.Copy;
        }
    }

    private async void OnWorkspaceDrop(object? sender, DragEventArgs e)
    {
        var paths = DroppedPaths(e).ToList();
        if (DroppedWorkspaceFiles.Pick(paths).Count == 0)
        {
            return;
        }

        e.Handled = true;
        await DroppedWorkspaceFiles.OpenAsync(SqlEditorHost.Current, paths, default);
    }

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
        Features.GridEdit.EditModeKey.Install(() => _keymap?.ShortcutText(Features.GridEdit.EditModeKey.CommandId));

        _viewModel = (MainViewModel)services.Create(typeof(MainViewModel));
        DataContext = _viewModel;
        _shell.MainWindow = this;
        ConnectionComboHost.Content = new Features.ConnectionManager.ConnectionComboView
        {
            DataContext = new Features.ConnectionManager.ConnectionComboViewModel(services.GetService<IEventBus>(), services),
        };

        _dispatcher = new KeyDispatcher(() => _keymap.Table, _registry, _keys);
        _optionText = new OptionTextGuard(_dispatcher);
        _controls = new MenuControls(
            _registry,
            _keys,
            id => _keymap.Table.SingleGesture(id),
            _shell.Docking,
            () => FocusManager?.GetFocusedElement(),
            g =>
            {
                var match = _keymap.Table.Find([(g.Key, g.KeyModifiers)], _keys);
                return match.Kind == KeyMatchKind.Match ? match.CommandId : null;
            },
            _dispatcher);
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnTextInputTunnel, RoutingStrategies.Tunnel);
        if (KeyTrace.Enabled)
        {
            AddHandler(KeyDownEvent, (_, e) => KeyTrace.KeyDown("bubble", e, FocusManager?.GetFocusedElement()), RoutingStrategies.Bubble, handledEventsToo: true);
            AddHandler(TextInputEvent, (_, e) => KeyTrace.TextInput("bubble", e, FocusManager?.GetFocusedElement()), RoutingStrategies.Bubble, handledEventsToo: true);
        }

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWorkspaceDragOver);
        AddHandler(DragDrop.DropEvent, OnWorkspaceDrop);

        // 포커스가 옮겨지면 그 화면의 포커스 자리를 문맥 키로(commands.md 2.2). 자리가 없는 곳(툴바·메뉴)은 직전 자리를 둔다.
        ContextKeyService.Shared = _keys;
        AddHandler(GotFocusEvent, (_, e) =>
        {
            if (FocusSlot.Find(e.Source as Visual) is { } slot)
            {
                _keys?.SetFocusSlot(slot);
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        PaletteIcon.Content = ShellIcons.Create("find", 14);
        PaletteButton.Click += (_, _) => _shell.ShowCommandPalette();
        ToolbarHost.SizeChanged += (_, _) => UpdateToolbarOverflow();
        ConnectionComboHost.SizeChanged += (_, _) => UpdateToolbarOverflow(); // 접속 콤보 폭은 고른 접속 이름을 따라 바뀐다
        SizeChanged += (_, _) => UpdateStatusBarCollapse();
        PositionChanged += (_, _) => RememberNormalBounds();
        Resized += (_, _) => RememberNormalBounds();

        _registry.CommandsChanged += (_, _) => Dispatcher.UIThread.Post(RebuildMenus);
        _panels.PanelsChanged += (_, _) => Dispatcher.UIThread.Post(RebuildMenus);
        // 시스템 메뉴의 클릭 콜백 안에서 도구 창 토글이 메뉴를 갈아 끼우지 않도록 시스템 메뉴는 한 박자 미룬다.
        _shell.Docking.LayoutRebuilt += (_, _) =>
        {
            if (UseNativeMenu)
            {
                Dispatcher.UIThread.Post(RebuildMenus);
            }
            else
            {
                RebuildMenus();
            }
        };
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

        _shell.Text.LanguageChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            ApplyStaticText();
            RebuildMenus();
        });

        FloatHostWindow.Presenting = UseNativeMenu ? AttachFloatMenu : null;

        ApplyStaticText();
        RebuildMenus();
        ShellCommandRunner.Runner = id => _registry is null || _keys is null ? Task.CompletedTask : _registry.ExecuteAsync(id, _keys);
        _shell.Docking.EmptyDocumentContent = () => new StartPage(_registry, _keys, _shell.Text, _keymap.ShortcutText);
        ShellContextMenus.Factory = surface =>
            _controls is null || _registry is null || _keymap is null || _shell is null
                ? []
                : _controls.BuildContextItems(MenuModelBuilder.BuildContext(_registry.Commands, _shell.Text, _keymap.ShortcutText, surface));
        ApplyEnvironment();
        ApplyPlacement(_shell.SavedWindow);
    }

    /// <summary>메뉴가 아닌 고정 글자와 화면 낭독기 이름을 지금 언어로 다시 단다(7.5 접근성, TSK-10-02).</summary>
    internal void ApplyStaticText()
    {
        if (_shell is null)
        {
            return;
        }

        var text = _shell.Text;
        PaletteText.Text = text.Lookup("Shell.main.paletteButton");
        ToolTip.SetTip(PaletteButton, text.Lookup("Shell.main.paletteTip"));
        AutomationProperties.SetName(PaletteButton, text.Lookup("Shell.main.paletteButtonName"));
        AutomationProperties.SetName(OverflowButton, text.Lookup("Shell.main.overflowName"));
        AutomationProperties.SetName(MenuBar, text.Lookup("Shell.main.menuBar"));
        AutomationProperties.SetName(ToolbarPanel, text.Lookup("Shell.main.toolbar"));
        AutomationProperties.SetName(StatusBar, text.Lookup("Shell.main.statusBar"));
        AutomationProperties.SetName(DockHost, text.Lookup("Shell.main.documents"));
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
        if (UseNativeMenu)
        {
            // 시스템 메뉴 막대가 메뉴를 맡는다. 창 안 메뉴는 숨기고 키보드 이동(Tab·Alt)에서도 뺀다.
            MenuBar.ItemsSource = null;
            MenuBar.IsVisible = false;
            MenuBar.Focusable = false;
            if (NativeMenu.GetMenu(this) is not { } root)
            {
                root = _controls.CreateNativeRoot();
                NativeMenu.SetMenu(this, root);
            }

            _controls.FillNativeMenu(root, bar);
            foreach (var (_, floatRoot) in _floatMenus)
            {
                _controls.FillNativeMenu(floatRoot, bar);
            }
        }
        else
        {
            MenuBar.ItemsSource = _controls.BuildMenuBar(bar);
        }

        _toolbarModel = MenuModelBuilder.BuildToolbar(_registry.Commands, text, _keymap.ShortcutText);
        ToolbarPanel.Children.Clear();
        ToolbarPanel.Children.AddRange(_controls.BuildToolbarItems(_toolbarModel));
        UpdateToolbarOverflow();
        _controls.RefreshEnablement();
    }

    /// <summary>
    /// 떠 있는 도킹 창에도 같은 메뉴를 시스템 막대에 보이게 한다(창마다 자기 NativeMenu 뿌리 하나).
    /// 창이 닫히면 추적을 거둔다.
    /// </summary>
    private void AttachFloatMenu(Window host)
    {
        if (_controls is null || _registry is null || _panels is null || _keymap is null || _shell is null || !UseNativeMenu)
        {
            return;
        }

        var root = _controls.CreateNativeRoot(() => host.FocusManager?.GetFocusedElement());
        NativeMenu.SetMenu(host, root);
        _floatMenus.Add((host, root));
        _controls.FillNativeMenu(root, MenuModelBuilder.BuildMenuBar(_registry.Commands, _shell.Text, _keymap.ShortcutText, _panels.Panels));
        _controls.RefreshEnablement();
        host.Closed += (_, _) =>
        {
            _floatMenus.RemoveAll(f => f.Window == host);
            _controls.ReleaseNativeRoot(root);
        };
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
        var reserved = Math.Max(PaletteButton.DesiredSize.Width, PaletteButton.MinWidth) + ConnectionComboHost.Bounds.Width + 12;
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
            if (await TryExitAsync().ConfigureAwait(true))
            {
                _closeConfirmed = true;
                Close();
            }

            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// 메인 창을 닫는다. 닫기 확인과 정리(<see cref="TryExitAsync"/>)를 모두 통과하면 창이 닫히고 true,
    /// 사용자가 취소했거나 이미 닫는 중이면 false. 업데이트의 「지금 다시 시작해 적용」이 같은 흐름을 빌려 쓴다.
    /// </summary>
    internal async Task<bool> CloseWithFlowAsync()
    {
        if (_shell is null)
        {
            return false;
        }

        if (!await TryExitAsync().ConfigureAwait(true))
        {
            return false;
        }

        _closeConfirmed = true;
        Close();
        return true;
    }

    /// <summary>
    /// 종료 흐름: 미커밋 확인 → 전용 세션 정리 → 레이아웃 저장 → 종료 작업. 통과하면 true, 취소하면 false(아무것도 정리하지 않고 그대로 둔다).
    /// </summary>
    private async Task<bool> TryExitAsync()
    {
        if (_closing)
        {
            return false; // 확인 대화상자가 떠 있는 동안 다시 닫기(⌘Q 두 번)는 무시한다
        }

        _closing = true;
        try
        {
            // 미커밋 확인을 먼저 한다: 취소하면 레이아웃 저장·종료 작업을 하지 않고 그대로 둔다(D-017).
            // 확인이 실패하면 알리고 다음 닫기는 확인 없이 끝낸다(확인이 늘 실패해도 앱을 끌 수 있어야 한다).
            if (_services is not null && !_exitWithoutConfirm)
            {
                switch (await ConfirmExitAsync(_services))
                {
                    case ExitConfirmResult.Cancelled:
                        return false;
                    case ExitConfirmResult.Failed:
                        _exitWithoutConfirm = true;
                        await NotifyExitConfirmFailedAsync();
                        return false;
                }
            }

            if (_services is not null)
            {
                try
                {
                    await DedicatedSessionCommands.ReleaseAllAsync(_services);
                }
                catch (Exception ex)
                {
                    Trace.TraceError("전용 세션 정리 실패: {0}", ex);
                }
            }

            try
            {
                _layoutSaveTimer?.Stop();
                await _shell!.SaveLayoutAsync(CapturePlacement());
            }
            catch (Exception ex)
            {
                Trace.TraceError("layout.json 저장 실패: {0}", ex);
            }

            await _shell!.RunShutdownHooksAsync();
            return true;
        }
        finally
        {
            _closing = false;
        }
    }

    private static async Task<ExitConfirmResult> ConfirmExitAsync(ServiceContainer services)
    {
        try
        {
            return await DedicatedSessionCommands.ConfirmExitAsync(services, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Trace.TraceError("종료 확인 실패: {0}", ex);
            return ExitConfirmResult.Failed;
        }
    }

    /// <summary>종료 전 확인이 실패했다고 알린다. 이번 닫기는 취소되고, 다음 닫기는 확인 없이 끝낸다.</summary>
    private static async Task NotifyExitConfirmFailedAsync()
    {
        try
        {
            await SqlEditorHost.Current.Dialogs.ShowMessageAsync(
                Oranda.App.Features.Localization.UiText.Get("Execution.exitConfirmFailed.title", "종료"),
                Oranda.App.Features.Localization.UiText.Get("Execution.exitConfirmFailed",
                    "종료하기 전 미커밋 확인에 실패했습니다. 커밋하지 않은 변경이 있을 수 있습니다. 다시 닫으면 확인 없이 종료하며, 커밋하지 않은 변경은 롤백됩니다."),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Trace.TraceError("종료 확인 실패 알림을 띄우지 못했습니다: {0}", ex);
        }
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        KeyTrace.KeyDown("tunnel", e, FocusManager?.GetFocusedElement());
        // 입력 칸(도구 창의 찾기·바인드 값 등)에서는 글자 편집 키를 칸에 맡긴다. 포커스 자리가 없는 칸은 직전 자리(편집기)를
        // 그대로 두므로, 가로채면 ⌘A 가 편집기의 「모두 선택」으로 간다. 실행 키(F 키·Primary+Enter)는 그대로 명령으로 보낸다.
        if (e.Source is TextBox && KeyDispatcher.IsTextEditingKey(e.Key, e.KeyModifiers, OperatingSystem.IsMacOS()))
        {
            return;
        }

        var key = KeyDispatcher.EffectiveKey(e.Key, e.PhysicalKey, e.KeyModifiers);
        if (_dispatcher is not null && !e.Handled && _dispatcher.Handle(key, e.KeyModifiers))
        {
            e.Handled = true;
        }

        if (OperatingSystem.IsMacOS())
        {
            _optionText?.NoteKeyDown(key, e.KeyModifiers, e.Handled);
        }
    }

    // macOS: 명령에 묶인 Option+문자는 글자(c·ç)로 편집기에 들어가지 않게 뒤따르는 TextInput 을 삼킨다.
    private void OnTextInputTunnel(object? sender, TextInputEventArgs e)
    {
        KeyTrace.TextInput("tunnel", e, FocusManager?.GetFocusedElement());
        if (OperatingSystem.IsMacOS() && _optionText?.ShouldSwallowText(e.Text) == true)
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
