using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Expanded;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Settings;
using TextRun = Avalonia.Controls.Documents.Run; // 이 폴더 위 이름 공간(…MyBatisWorkspace.Run)과 겹치지 않게 별칭을 쓴다.

namespace Oranda.App.Features.MyBatisWorkspace.Editor;

/// <summary>
/// MyBatis 문장 탭의 편집기 View(design.md 1장·9장). 위에서 아래로 문서 도구 막대(오른쪽 끝에 분할 단추), 그리고
/// 원문 칸(문장 머리 줄 · 알림 띠 · 원문 편집기 <see cref="SqlEditorView"/>) | 펼친 SQL(<see cref="MyBatisExpandedView"/>) 분할(<see cref="MyBatisExpandedSplit"/>, TSK-03-10)이다.
/// 펼친 SQL 은 이 탭의 문장만 보인다 — 「매퍼 쿼리」 창에서 고르기만 하고 탭을 열지 않으면 펼친 SQL 은 어디에도 없다.
/// 안쪽 편집기의 포커스 자리를 <c>mybatisText</c> 로 덮어쓰고, SQL 강조를 떼고 등록된 <see cref="IMyBatisEditorDecorator"/> 를 Order 순으로 붙인다.
/// 꾸밈은 화면에서 떨어지거나 탭이 바뀔 때 Dispose 하고, 다시 붙으면 새로 붙인다(탭을 오가도 강조가 사라지지 않는다).
/// </summary>
public sealed class MyBatisStatementEditorView : UserControl, IEditorFocusTarget
{
    private readonly IMyBatisWorkspaceSession _session;
    private readonly List<IDisposable> _decorations = [];
    private readonly ContentControl _toolbarHost = new() { IsVisible = false };
    private readonly TextBlock _pathText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _dynamicTagsText = new();
    private readonly ContentControl _bannerSlot = new(); // 03-03 밖에서 바뀜 알림 띠 자리(Save/MapperBannerDecorator 가 채움)
    private readonly StackPanel _headerExtra = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly SqlEditorView _editorView = new();
    private readonly IServiceProvider? _services;
    private readonly MyBatisExpandedView _expandedView = new();
    private readonly MyBatisExpandedSplit _split;
    private readonly Border _headerBar = new() { Height = MyBatisExpandedView.PaneHeaderHeight, Classes = { "panel-toolbar" } };
    private readonly DockPanel _editorPane;
    private readonly MyBatisScrollSync _scrollSync;
    private SqlEditorViewModel _tab;
    private bool _decorated;
    private bool _subscribed;

    /// <param name="tab">문장 탭.</param>
    /// <param name="session">작업 공간 세션.</param>
    /// <param name="toolbar">문서 도구 막대(없으면 막대 자리를 숨긴다).</param>
    /// <param name="services">펼친 SQL 이 쓰는 서비스(계산 허브·명령 레지스트리·문맥 키·설정). 없으면 펼친 SQL 자리에 안내 한 줄만 보인다.</param>
    public MyBatisStatementEditorView(SqlEditorViewModel tab, IMyBatisWorkspaceSession session, MyBatisDocumentToolbar? toolbar = null, IServiceProvider? services = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(session);
        _tab = tab;
        _session = session;
        _services = services;
        _editorPane = BuildEditorPane();
        _split = new MyBatisExpandedSplit(_editorPane, _expandedView, services?.GetService(typeof(SettingsService)) as SettingsService);
        Content = BuildLayout();
        _scrollSync = new MyBatisScrollSync(_editorView, _editorView.TextEditorControl, _expandedView.CodeView, _expandedView.CodeView.TextEditorControl);
        _split.Changed += (_, _) => ApplyScrollSync();
        ApplyScrollSync();

        // 안쪽 편집기 View 는 생성자에서 포커스 자리 sqlText 와 「editor」 우클릭 메뉴를 건다. 인스턴스 값으로 덮어쓴다.
        FocusSlot.SetName(_editorView, "mybatisText");
        MyBatisEditorContextMenu.Attach(_editorView.TextEditorControl);
        if (toolbar is not null)
        {
            _toolbarHost.Content = toolbar;
            _toolbarHost.IsVisible = true;
        }

        UseTab(tab);
        DataContext = tab;
    }

    /// <summary>이 편집기가 보이는 문장 탭.</summary>
    public SqlEditorViewModel Tab => _tab;

    /// <summary>안쪽 원문 편집기(시험·확장용).</summary>
    public SqlEditorView Editor => _editorView;

    /// <summary>원문 편집기 | 펼친 SQL 분할.</summary>
    public MyBatisExpandedSplit Split => _split;

    /// <summary>펼친 SQL 영역의 ViewModel(이 탭의 문장).</summary>
    public MyBatisExpandedViewModel? Expanded => _expandedView.DataContext as MyBatisExpandedViewModel;

    /// <summary>원문 ↔ 펼친 SQL 스크롤 맞춤(시험·계측용).</summary>
    internal MyBatisScrollSync ScrollSync => _scrollSync;

    /// <summary>문서 도구 막대(없으면 null: 레지스트리를 얻지 못한 시험 환경).</summary>
    public MyBatisDocumentToolbar? Toolbar => _toolbarHost.Content as MyBatisDocumentToolbar;

    /// <summary>머리 줄 오른쪽 빈 자리. 03-02 가 켜진 조건 칩 같은 것을 더한다(꾸밈 문맥에서는 편집기의 조상 View 로 찾는다).</summary>
    public Panel HeaderExtraSlot => _headerExtra;

    /// <summary>머리 줄과 원문 편집기 사이의 알림 띠 자리(03-03). 비어 있으면 자리를 차지하지 않는다.</summary>
    public ContentControl BannerSlot => _bannerSlot;

    /// <summary>머리 줄의 「동적 태그 n」 칩 글(시험·계측용).</summary>
    public string? DynamicTagsChipText => _dynamicTagsText.Text;

    /// <summary>머리 줄 경로 글(「EmpMapper.xml › selectEmpList」, 시험·계측용).</summary>
    public string HeaderPathText => string.Concat(_pathText.Inlines?.OfType<TextRun>().Select(r => r.Text) ?? []);

    /// <summary>머리 줄 경로 글자 컨트롤(시험·계측용).</summary>
    internal TextBlock PathBlock => _pathText;

    /// <summary>문장 머리 줄(시험·계측용). 원문 칸 맨 위에 있어 펼친 SQL 의 시나리오 바와 같은 높이에서 시작한다.</summary>
    internal Border HeaderBar => _headerBar;

    /// <summary>원문 칸: 머리 줄 · 알림 띠 · 원문 편집기(시험·계측용). 분할의 왼쪽(가로 분할이면 위) 칸이다.</summary>
    internal DockPanel EditorPane => _editorPane;

    /// <summary>문서 도구 막대를 담는 자리(시험·계측용).</summary>
    internal ContentControl ToolbarHost => _toolbarHost;

    /// <summary>지금 붙어 있는 꾸밈 수(시험·계측용).</summary>
    public int AttachedDecorationCount => _decorations.Count;

    public void FocusEditor() => _editorView.FocusEditor();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SqlEditorViewModel tab && !ReferenceEquals(tab, _tab))
        {
            UseTab(tab);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
        _scrollSync.Attach();
        RefreshHeader();
        if (!_decorated)
        {
            AttachDecorations();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
        _scrollSync.Detach();
        DisposeDecorations();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>탭을 정한다. 안쪽 DataContext 를 정한 뒤에야 SQL 강조가 만들어지므로 그다음에 떼고 꾸밈을 붙인다.</summary>
    private void UseTab(SqlEditorViewModel tab)
    {
        bool wasSubscribed = _subscribed;
        Unsubscribe();
        _scrollSync.Detach();
        DisposeDecorations();
        _tab = tab;
        _editorView.DataContext = tab;
        _expandedView.DataContext = _services is null ? new MyBatisExpandedViewModel() : new MyBatisExpandedViewModel(_services, tab);
        _editorView.Colorizer?.Detach();
        RefreshHeader();
        AttachDecorations();
        if (wasSubscribed)
        {
            Subscribe();
            _scrollSync.Attach();
        }
    }

    /// <summary>
    /// 원문 칸(분할 왼쪽 · 가로 분할이면 위): 문장 머리 줄(<see cref="MyBatisExpandedView.PaneHeaderHeight"/> DIP) / 알림 띠 / 원문 편집기.
    /// 머리 줄이 칸 안에 있어 오른쪽 펼친 SQL 의 시나리오 바와 같은 높이에서 시작한다.
    /// </summary>
    private DockPanel BuildEditorPane()
    {
        // 칩의 바탕과 글자 크기는 테마 리소스를 따라간다(값을 직접 쓰지 않는다).
        var chip = new Border
        {
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(7, 1),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _dynamicTagsText,
        };
        chip.Bind(Border.BackgroundProperty, chip.GetResourceObservable("OrandaCodeBackgroundBrush"));
        _dynamicTagsText.Bind(TextBlock.FontSizeProperty, _dynamicTagsText.GetResourceObservable("OrandaFontSizeSmall"));

        // 경로 · 칩은 왼쪽에 붙여 두고, 칸이 좁아지면 경로부터 말줄임한다. 오른쪽 빈 자리(_headerExtra, 03-02 조건 칩·조합 검사 단추 등)는 먼저 자리를 잡아 잘리지 않고,
        // 그보다 더 좁으면 칩이 그 자리를 덮지 않게 경로 · 칩 묶음을 잘라 그린다.
        var path = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        Grid.SetColumn(_pathText, 0);
        Grid.SetColumn(chip, 1);
        path.Children.Add(_pathText);
        path.Children.Add(chip);
        DockPanel.SetDock(_headerExtra, Avalonia.Controls.Dock.Right);
        _headerBar.Child = new DockPanel { Children = { _headerExtra, path } };

        DockPanel.SetDock(_headerBar, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(_bannerSlot, Avalonia.Controls.Dock.Top);
        return new DockPanel { Children = { _headerBar, _bannerSlot, _editorView } };
    }

    /// <summary>위에서 아래로: 문서 도구 막대(레지스트리 <c>toolbar:mybatis</c> 자리, 오른쪽 끝 분할 단추) / 원문 칸 | 펼친 SQL.</summary>
    private DockPanel BuildLayout()
    {
        // 도구 막대 줄: 레지스트리 단추(왼쪽) ........ 분할 단추(오른쪽). 막대가 없어도(시험 환경) 분할 단추는 보인다.
        DockPanel.SetDock(_split.Buttons, Avalonia.Controls.Dock.Right);
        var toolbarRow = new Border { Classes = { "panel-toolbar" }, Child = new DockPanel { Children = { _split.Buttons, _toolbarHost } } };

        DockPanel.SetDock(toolbarRow, Avalonia.Controls.Dock.Top);
        return new DockPanel { Children = { toolbarRow, _split } };
    }

    /// <summary>스크롤 맞춤은 토글이 켜져 있고 펼친 SQL 이 보일 때만 한다.</summary>
    private void ApplyScrollSync() => _scrollSync.IsEnabled = _split.IsScrollSynced && _split.IsExpandedShown;

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        _tab.TextChanged += OnTabTextChanged;
        _session.StateChanged += OnSessionStateChanged;
        if (UiText.Current is { } text)
        {
            text.LanguageChanged += OnLanguageChanged;
        }
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        _tab.TextChanged -= OnTabTextChanged;
        _session.StateChanged -= OnSessionStateChanged;
        if (UiText.Current is { } text)
        {
            text.LanguageChanged -= OnLanguageChanged;
        }
    }

    private void OnTabTextChanged(object? sender, EventArgs e) => RefreshChip();

    private void OnSessionStateChanged(object? sender, EventArgs e) => RefreshHeader();

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshHeader();

    private void RefreshHeader()
    {
        // 「EmpMapper.xml › selectEmpList」: 파일 이름은 그대로, 문장 id 는 진하게(id 바꾸기·경로 바꾸기 뒤에도 따라간다).
        var id = _session.StatementIdOf(_tab) ?? _tab.Title;
        _pathText.Inlines = new InlineCollection
        {
            new TextRun(Path.GetFileName(_session.MapperPath) + " › "),
            new TextRun(id) { FontWeight = FontWeight.Bold },
        };
        RefreshChip();
    }

    private void RefreshChip()
    {
        var format = UiText.Get("MyBatis.editor.dynamicTags", "동적 태그 {0}");
        _dynamicTagsText.Text = string.Format(CultureInfo.CurrentCulture, format, MyBatisStatementText.CountDynamicTags(_tab.Text));
    }

    private void AttachDecorations()
    {
        _decorated = true;
        var hooks = (_session as MyBatisWorkspaceSession)?.Workspace.Host.MyBatisHooks ?? MyBatisHooks.Empty;
        var context = new MyBatisEditorContext(_session, _tab, _editorView.TextEditorControl);
        foreach (var decorator in hooks.EditorDecorators.OrderBy(d => d.Order))
        {
            try
            {
                _decorations.Add(decorator.Attach(context));
            }
            catch (Exception ex)
            {
                // 꾸밈 하나가 실패해도 편집기는 쓸 수 있어야 한다.
                Trace.TraceError("MyBatis 편집기 꾸밈 '{0}' 을(를) 붙이지 못했다: {1}", decorator.GetType().Name, ex);
            }
        }
    }

    private void DisposeDecorations()
    {
        _decorated = false;
        for (int i = _decorations.Count - 1; i >= 0; i--)
        {
            try
            {
                _decorations[i].Dispose();
            }
            catch (Exception ex)
            {
                Trace.TraceError("MyBatis 편집기 꾸밈을 떼지 못했다: {0}", ex);
            }
        }

        _decorations.Clear();
    }
}
