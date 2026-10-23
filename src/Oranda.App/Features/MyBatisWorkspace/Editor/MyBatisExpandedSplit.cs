using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Expanded;
using Oranda.App.Shell.Icons;
using Oranda.App.Shell.Settings;
using Oranda.Core.Settings;

namespace Oranda.App.Features.MyBatisWorkspace.Editor;

/// <summary>
/// MyBatis 쿼리 탭 안의 「원문 편집기 | 펼친 SQL」 분할(TSK-03-10). 세로 분할(펼친 SQL 이 편집기 오른쪽)이 기본이고 가로 분할(아래)로 바꿀 수 있으며
/// 펼친 SQL 을 숨길 수도 있다. 방향·보임·비율은 앱 설정(<see cref="Oranda.Core.Settings.MyBatisSettings"/>)에 기억하고, 탭이 화면에 붙을 때마다 다시 읽는다
/// (다른 탭에서 바꾼 방향을 따라간다). 전환 단추 4개(<see cref="Buttons"/>: 세로 · 가로 | 보이기 · 스크롤 맞춤)는 문서 도구 막대 줄 오른쪽에 놓는다.
/// 스크롤 맞춤 자체는 <see cref="MyBatisScrollSync"/> 가 하고, 이 분할은 켬/끔만 기억한다.
/// 펼친 SQL 이 탭 안에 있으므로 아래 도구 창(조회 결과 등)을 덮지 않는다.
/// </summary>
public sealed class MyBatisExpandedSplit : Grid
{
    /// <summary>편집기 비율의 하한·상한(한쪽이 사라지지 않게).</summary>
    public const double MinRatio = 0.15;

    public const double MaxRatio = 0.85;

    /// <summary>분할선 두께(DIP).</summary>
    public const double SplitterSize = 5;

    private readonly Control _editor;
    private readonly GridSplitter _splitter = new() { Background = Brushes.Transparent, Focusable = false };
    private readonly Border _expandedFrame = new();
    private readonly SettingsService? _settings;
    private readonly ToggleButton _verticalButton;
    private readonly ToggleButton _horizontalButton;
    private readonly ToggleButton _showButton;
    private readonly ToggleButton _syncButton;
    private bool _horizontal;
    private bool _shown = true;
    private bool _syncScroll = true;
    private double _ratio = 0.5;

    public MyBatisExpandedSplit(Control editor, MyBatisExpandedView expanded, SettingsService? settings)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(expanded);
        _editor = editor;
        ExpandedView = expanded;
        _settings = settings;
        _expandedFrame.Child = expanded;
        _expandedFrame.Bind(Border.BorderBrushProperty, _expandedFrame.GetResourceObservable("OrandaSeparatorBrush"));
        _splitter.DragCompleted += (_, _) => OnSplitterMoved();
        Children.Add(editor);
        Children.Add(_splitter);
        Children.Add(_expandedFrame);

        _verticalButton = Toggle("vertical", SplitIcon(horizontal: false), "MyBatis.split.vertical", "펼친 SQL 을 편집기 오른쪽에(세로 분할)", () => Choose(horizontal: false));
        _horizontalButton = Toggle("horizontal", SplitIcon(horizontal: true), "MyBatis.split.horizontal", "펼친 SQL 을 편집기 아래에(가로 분할)", () => Choose(horizontal: true));
        _showButton = Toggle("show", ShellIcons.Create("objView"), "MyBatis.split.show", "펼친 SQL 보이기 · 숨기기", () => SetShown(!_shown));
        _syncButton = Toggle("syncScroll", ShellIcons.Create("objSynonym"), "MyBatis.split.syncScroll", "원문 · 펼친 SQL 스크롤 맞춤", () => SetSyncScroll(!_syncScroll));
        Buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _verticalButton, _horizontalButton, new Separator { Classes = { "toolbar" } }, _showButton, _syncButton },
        };
        AutomationProperties.SetName(Buttons, UiText.Get("MyBatis.split.buttons", "펼친 SQL 분할"));

        LoadSettings();
    }

    /// <summary>방향·보임·스크롤 맞춤이 바뀌었다(설정을 다시 읽을 때 포함).</summary>
    public event EventHandler? Changed;

    /// <summary>분할 전환 단추 줄(세로 · 가로 | 보이기 · 스크롤 맞춤). 쿼리 탭 머리 줄에 놓는다.</summary>
    public StackPanel Buttons { get; }

    /// <summary>펼친 SQL 영역.</summary>
    public MyBatisExpandedView ExpandedView { get; }

    /// <summary>가로 분할(펼친 SQL 이 아래)인가. false = 세로 분할(오른쪽).</summary>
    public bool IsHorizontal => _horizontal;

    /// <summary>펼친 SQL 이 보이는가.</summary>
    public bool IsExpandedShown => _shown;

    /// <summary>원문 편집기와 펼친 SQL 의 스크롤을 맞추는가(「스크롤 맞춤」 토글).</summary>
    public bool IsScrollSynced => _syncScroll;

    /// <summary>원문 편집기 비율(<see cref="MinRatio"/>~<see cref="MaxRatio"/>).</summary>
    public double Ratio => _ratio;

    /// <summary>마지막 설정 저장(시험용). 저장하지 않았으면 완료된 Task.</summary>
    internal Task LastSave { get; private set; } = Task.CompletedTask;

    internal ToggleButton VerticalButton => _verticalButton;

    internal ToggleButton HorizontalButton => _horizontalButton;

    internal ToggleButton ShowButton => _showButton;

    internal ToggleButton SyncScrollButton => _syncButton;

    internal GridSplitter Splitter => _splitter;

    /// <summary>비율을 허용 범위로 자른다. 숫자가 아니면 기본 0.5.</summary>
    public static double ClampRatio(double ratio) => double.IsFinite(ratio) ? Math.Clamp(ratio, MinRatio, MaxRatio) : 0.5;

    /// <summary>방향을 정한다(펼친 SQL 이 숨어 있으면 함께 보인다). 설정에 기억한다.</summary>
    public void Choose(bool horizontal)
    {
        _horizontal = horizontal;
        _shown = true;
        Apply();
        Save(m => m with { ExpandedHorizontal = horizontal, ShowExpanded = true });
    }

    /// <summary>펼친 SQL 을 보이거나 숨긴다. 설정에 기억한다.</summary>
    public void SetShown(bool shown)
    {
        _shown = shown;
        Apply();
        Save(m => m with { ShowExpanded = shown });
    }

    /// <summary>스크롤 맞춤을 켜거나 끈다. 설정에 기억한다.</summary>
    public void SetSyncScroll(bool sync)
    {
        _syncScroll = sync;
        Apply();
        Save(m => m with { SyncScroll = sync });
    }

    /// <summary>편집기 비율을 정한다(분할선 끌기와 같은 길). 설정에 기억한다.</summary>
    public void SetRatio(double ratio)
    {
        _ratio = ClampRatio(ratio);
        Apply();
        var clamped = _ratio;
        Save(m => m with { ExpandedRatio = clamped });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LoadSettings(); // 다른 탭에서 바꾼 방향·비율을 따라간다
    }

    private void LoadSettings()
    {
        if (_settings?.Current.MyBatis is { } mybatis)
        {
            _horizontal = mybatis.ExpandedHorizontal;
            _shown = mybatis.ShowExpanded;
            _ratio = ClampRatio(mybatis.ExpandedRatio);
            _syncScroll = mybatis.SyncScroll;
        }

        Apply();
    }

    /// <summary>지금 방향·보임·비율로 칸을 다시 나눈다.</summary>
    private void Apply()
    {
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        _splitter.IsVisible = _shown;
        _expandedFrame.IsVisible = _shown;
        SetCell(_editor, 0);
        SetCell(_splitter, 0); // 숨길 때도 칸 번호가 정의 수를 넘지 않게(Grid 가 숨은 자식도 잰다)
        SetCell(_expandedFrame, 0);
        if (_shown)
        {
            var definitions = new[] { new GridLength(_ratio, GridUnitType.Star), new GridLength(SplitterSize), new GridLength(1 - _ratio, GridUnitType.Star) };
            if (_horizontal)
            {
                RowDefinitions.AddRange(definitions.Select(d => new RowDefinition(d)));
                _splitter.ResizeDirection = GridResizeDirection.Rows;
                _splitter.Width = double.NaN;
                _splitter.Height = SplitterSize;
                _expandedFrame.BorderThickness = new Thickness(0, 1, 0, 0);
            }
            else
            {
                ColumnDefinitions.AddRange(definitions.Select(d => new ColumnDefinition(d)));
                _splitter.ResizeDirection = GridResizeDirection.Columns;
                _splitter.Width = SplitterSize;
                _splitter.Height = double.NaN;
                _expandedFrame.BorderThickness = new Thickness(1, 0, 0, 0);
            }

            SetCell(_splitter, 1);
            SetCell(_expandedFrame, 2);
        }

        _verticalButton.IsChecked = _shown && !_horizontal;
        _horizontalButton.IsChecked = _shown && _horizontal;
        _showButton.IsChecked = _shown;
        _syncButton.IsChecked = _syncScroll;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetCell(Control control, int index)
    {
        SetColumn(control, _horizontal ? 0 : index);
        SetRow(control, _horizontal ? index : 0);
    }

    /// <summary>분할선을 놓았다: 두 칸의 실제 크기로 비율을 다시 적는다.</summary>
    private void OnSplitterMoved()
    {
        double first, second;
        if (_horizontal && RowDefinitions.Count == 3)
        {
            (first, second) = (RowDefinitions[0].ActualHeight, RowDefinitions[2].ActualHeight);
        }
        else if (!_horizontal && ColumnDefinitions.Count == 3)
        {
            (first, second) = (ColumnDefinitions[0].ActualWidth, ColumnDefinitions[2].ActualWidth);
        }
        else
        {
            return;
        }

        if (first + second > 0)
        {
            SetRatio(first / (first + second));
        }
    }

    /// <summary>바꾼 필드만 저장한다. 나머지는 저장된 값을 그대로 둬서, 화면에 함께 떠 있는 다른 탭이 바꾼 값을 덮지 않는다.</summary>
    private void Save(Func<MyBatisSettings, MyBatisSettings> change)
    {
        if (_settings is null)
        {
            return;
        }

        LastSave = SaveAsync(_settings, change);
    }

    private static async Task SaveAsync(SettingsService settings, Func<MyBatisSettings, MyBatisSettings> change)
    {
        try
        {
            await settings.UpdateAsync(s => s with { MyBatis = change(s.MyBatis) }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 설정 파일을 못 써도 화면은 고른 대로 둔다(다음 실행에서 기본값으로 돌아갈 뿐이다).
            Trace.TraceWarning("펼친 SQL 분할 설정을 저장하지 못했다: {0}", ex.Message);
        }
    }

    private static ToggleButton Toggle(string name, Control? icon, string key, string fallback, Action click)
    {
        var title = UiText.Get(key, fallback);
        var button = new ToggleButton
        {
            Name = "Split_" + name,
            Content = icon ?? (object)title,
            Classes = { "toolbar" },
            Focusable = false,
        };
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, title);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>분할 아이콘: 바깥 네모와 가운데 선(세로 분할 = 세로선, 가로 분할 = 가로선). 테마의 아이콘 색을 따른다.</summary>
    private static Avalonia.Controls.Shapes.Path SplitIcon(bool horizontal)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(horizontal ? "M2.5,3.5 H17.5 V16.5 H2.5 Z M2.5,10 H17.5" : "M2.5,3.5 H17.5 V16.5 H2.5 Z M10,3.5 V16.5"),
            StrokeThickness = 1.3,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
        };
        path.Bind(Shape.StrokeProperty, path.GetResourceObservable("OrandaIconBlueBrush"));
        return path;
    }
}
