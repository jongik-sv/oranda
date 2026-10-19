using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Oranda.App.Shell.Appearance;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Layout;
using Oranda.App.Shell.Palette;
using Oranda.App.Shell.Settings;
using Oranda.Core.Connection;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>
/// 명령 처리기가 셸에 부탁하는 일의 입구(ui-contract.md: 처리기는 계약만 안다). 시험에서는 가짜로 바꿀 수 있다.
/// </summary>
public interface IShellUi
{
    DockWorkspace Docking { get; }

    SettingsService Settings { get; }

    void ShowCommandPalette();

    void ShowSettings();

    void ShowAbout();

    /// <summary>메인 창을 닫는다(종료 흐름은 창 닫기 이벤트가 이어 받는다).</summary>
    void CloseMainWindow();

    /// <summary>활성 문서의 내용으로 키보드 포커스를 옮긴다.</summary>
    void FocusDocument();

    /// <summary>
    /// 활성 문서의 접속 환경(창 테두리·창 제목 <c>[운영]</c> 표시, ui-shell.md 4.2). 접속 없는 문서면 null.
    /// 접속 관리자(TSK-03-04)·편집기가 활성 문서가 바뀔 때 갱신한다.
    /// </summary>
    ConnectionEnvironment? ActiveEnvironment { get; set; }
}

/// <summary>설정·레이아웃 파일을 읽은 결과.</summary>
public sealed record ShellLoadResult(LayoutDocument Layout, string? Notice);

/// <summary>
/// 앱 셸의 조정자: 시작 때 설정·테마·레이아웃을 불러오고, 종료 때 레이아웃을 저장하며,
/// 팔레트·설정·정보 창을 연다. 화면 조각(메뉴·툴바·도킹)은 각자의 클래스가 맡는다.
/// </summary>
public sealed class ShellController : IShellUi
{
    private readonly ICommandRegistry _registry;
    private readonly ContextKeyService _keys;
    private readonly IShellText _text;
    private readonly ILayoutStore _layoutStore;
    private readonly KeymapService _keymap;
    private readonly ThemeService _theme;
    private readonly List<string> _recent = [];
    private readonly List<Func<CancellationToken, Task>> _shutdownHooks = [];
    private CommandPaletteWindow? _palette;
    private ConnectionEnvironment? _activeEnvironment;

    public ShellController(
        ICommandRegistry registry,
        ContextKeyService keys,
        IShellText text,
        SettingsService settings,
        ILayoutStore layoutStore,
        DockWorkspace docking,
        KeymapService keymap,
        ThemeService theme)
    {
        _registry = registry;
        _keys = keys;
        _text = text;
        Settings = settings;
        _layoutStore = layoutStore;
        Docking = docking;
        _keymap = keymap;
        _theme = theme;
        docking.ActiveDocumentChanged += (_, _) => UpdateDocumentKeys();
        docking.LayoutRebuilt += (_, _) => UpdateDocumentKeys();
    }

    public DockWorkspace Docking { get; }

    public SettingsService Settings { get; }

    public KeymapService Keymap => _keymap;

    public IShellText Text => _text;

    /// <summary>레이아웃 파일을 읽을 때 알린 말(손상 백업 등).</summary>
    public string? LayoutNotice { get; private set; }

    /// <summary>복원할 창 위치(첫 실행이면 null).</summary>
    public WindowPlacement? SavedWindow { get; private set; }

    /// <summary>명령 팔레트가 열려 있는지(시험·중복 열기 방지).</summary>
    public bool IsPaletteOpen => _palette is not null;

    /// <summary>가장 최근에 연 부속 창(팔레트·설정·정보). 시험이 살펴본다.</summary>
    internal Window? LastWindow { get; private set; }

    /// <summary>열려 있는 명령 팔레트 창(없으면 null).</summary>
    internal CommandPaletteWindow? Palette => _palette;

    /// <summary>메인 창. 팔레트·설정 창의 소유자로 쓴다.</summary>
    public Window? MainWindow { get; set; }

    public ConnectionEnvironment? ActiveEnvironment
    {
        get => _activeEnvironment;
        set
        {
            if (_activeEnvironment != value)
            {
                _activeEnvironment = value;
                EnvironmentChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? EnvironmentChanged;

    /// <summary>
    /// 설정·레이아웃 파일을 읽는다(디스크 읽기만, 화면을 건드리지 않는다). 첫 프레임 전에 부를 때는
    /// UI 스레드가 막혀 있어도 되도록 <see cref="Task.Run(Func{Task})"/> 안에서 기다린다.
    /// </summary>
    public async Task<ShellLoadResult> LoadAsync(CancellationToken ct = default)
    {
        await Settings.LoadAsync(ct).ConfigureAwait(false);
        var stored = await _layoutStore.LoadAsync(ct).ConfigureAwait(false);
        return new ShellLoadResult(stored.Value, stored.Notice);
    }

    /// <summary>읽은 값으로 키맵·테마·도킹을 짓는다. UI 스레드에서 부른다.</summary>
    public void Initialize(ShellLoadResult loaded)
    {
        _keymap.Reload();
        _theme.Apply();
        LayoutNotice = loaded.Notice;
        SavedWindow = loaded.Layout.Window;
        Docking.Apply(loaded.Layout);
        UpdateDocumentKeys();
    }

    /// <summary>읽기와 짓기를 이어서 한다(시험·비동기 호출용).</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var loaded = await LoadAsync(ct).ConfigureAwait(true);
        Initialize(loaded);
    }

    /// <summary>현재 배치와 창 위치를 layout.json 에 저장한다(종료 때, 배치가 바뀐 뒤 잠시 쉬었을 때).</summary>
    public Task SaveLayoutAsync(WindowPlacement? window, CancellationToken ct = default) =>
        _layoutStore.SaveAsync(Docking.Capture(window), ct);

    /// <summary>창을 닫기 직전에 할 일(작업 공간 저장 등)을 등록한다. 기능이 시작 작업에서 부른다.</summary>
    public void RegisterShutdown(Func<CancellationToken, Task> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _shutdownHooks.Add(hook);
    }

    /// <summary>등록된 종료 작업을 차례로 부른다. 하나가 실패해도 나머지는 계속한다.</summary>
    public async Task RunShutdownHooksAsync(CancellationToken ct = default)
    {
        foreach (var hook in _shutdownHooks.ToList())
        {
            try
            {
                await hook(ct).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("종료 작업 실패: {0}", ex);
            }
        }
    }

    public void ShowCommandPalette()
    {
        if (_palette is not null)
        {
            _palette.Activate();
            return;
        }

        var vm = new CommandPaletteViewModel(
            _registry,
            _keys,
            _text,
            _keymap.ShortcutText,
            _recent,
            id =>
            {
                _recent.Remove(id);
                _recent.Insert(0, id);
            });
        var window = new CommandPaletteWindow { DataContext = vm };
        window.Closed += (_, _) => _palette = null;
        _palette = window;
        Present(window, modal: false);
    }

    public void ShowSettings()
    {
        var fonts = FontManager.Current.SystemFonts.Select(f => f.Name);
        var window = new SettingsWindow { DataContext = new SettingsViewModel(Settings, _text, fonts) };
        Present(window, modal: true);
    }

    public void ShowAbout()
    {
        var version = typeof(ShellController).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var ok = new Button { Content = "OK", MinWidth = 80, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        var window = new Window
        {
            Title = _text.Lookup("Shell.about.title"),
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 320,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Oranda", FontSize = 22, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"{version}" },
                    ok,
                },
            },
        };
        ok.Click += (_, _) => window.Close();
        Present(window, modal: true);
    }

    public void CloseMainWindow() => MainWindow?.Close();

    public void FocusDocument()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Focus();
    }

    private void Present(Window window, bool modal)
    {
        LastWindow = window;
        if (MainWindow is { IsVisible: true } owner)
        {
            if (modal)
            {
                _ = window.ShowDialog(owner);
            }
            else
            {
                window.Show(owner);
            }
        }
        else
        {
            window.Show();
        }
    }

    private void UpdateDocumentKeys()
    {
        var docs = Docking.Documents;
        _keys.Set("hasDocument", docs.Count > 0);
    }
}
