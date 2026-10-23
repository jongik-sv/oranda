using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.PlsqlRun;

/// <summary>
/// PL/SQL 실행 대화상자(TSK-08-02). 화면은 XAML 없이 코드로 만들고 모든 상태·동작은 <see cref="PlsqlRunViewModel"/> 에 둔다.
/// 모달이 아니다 — 결과(OUT 값·커서·DBMS_OUTPUT)를 보면서 값을 고쳐 다시 실행할 수 있다. 기본 단추는 「닫기」(Enter 로 바로 실행하지 않는다).
/// </summary>
public sealed class PlsqlRunWindow : Window
{
    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,Menlo,monospace");

    private readonly PlsqlRunViewModel _vm;

    public PlsqlRunWindow(PlsqlRunViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        DataContext = vm;
        Width = 820;
        Height = 700;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(TitleProperty, new Binding(nameof(PlsqlRunViewModel.Title)));
        Content = Build();
    }

    /// <summary>주 창 위에 연다. 주 창이 없으면(시험·시작 직후) 독립 창으로 띄운다.</summary>
    public static void ShowFor(PlsqlRunViewModel vm, Window? owner)
    {
        var window = new PlsqlRunWindow(vm);
        owner ??= (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is { IsVisible: true })
        {
            window.Show(owner);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
        }
    }

    private Grid Build()
    {
        var root = new Grid { Margin = new Thickness(16), RowSpacing = 8, RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto") };

        // 오버로드(패키지 멤버·오버로드가 여럿일 때만)
        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontFamily = Mono };
        picker.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(PlsqlRunViewModel.Candidates)));
        picker.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, new Binding(nameof(PlsqlRunViewModel.SelectedCandidate)) { Mode = BindingMode.TwoWay });
        picker.Bind(IsVisibleProperty, new Binding(nameof(PlsqlRunViewModel.HasMultipleCandidates)));
        picker.ItemTemplate = new FuncDataTemplate<PlsqlCandidate>((c, _) => new TextBlock { Text = c?.Display, FontFamily = Mono });
        Avalonia.Automation.AutomationProperties.SetName(picker, "호출할 프로시저·함수(오버로드)");
        Grid.SetRow(picker, 0);
        root.Children.Add(picker);

        // 인자 입력
        var header = new Grid { ColumnDefinitions = Columns(), ColumnSpacing = 8 };
        foreach (var (text, col) in new[] { ("인자", 0), ("방향", 1), ("형식", 2), ("값", 3), ("NULL", 4), ("기본값", 5), ("결과(OUT)", 6) })
        {
            var h = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold };
            Grid.SetColumn(h, col);
            header.Children.Add(h);
        }

        var headerBand = new Border { Classes = { "grid-header" }, Child = header, Padding = new Thickness(4, 4) };
        Grid.SetRow(headerBand, 1);
        root.Children.Add(headerBand);

        var rows = new ItemsControl { ItemTemplate = new FuncDataTemplate<PlsqlArgumentRow>((row, _) => RowView(row)) };
        rows.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(PlsqlRunViewModel.Rows)));
        var rowsScroll = new ScrollViewer { Content = rows, MaxHeight = 220, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(rowsScroll, 2);
        root.Children.Add(rowsScroll);

        // 미리보기 + 결과 탭
        var tabs = new TabControl();
        var preview = new SqlCodeView();
        preview.Bind(SqlCodeView.TextProperty, new Binding(nameof(PlsqlRunViewModel.Preview)));
        Avalonia.Automation.AutomationProperties.SetName(preview, "생성한 호출 블록 미리보기");
        var problems = new TextBlock { TextWrapping = TextWrapping.Wrap };
        problems.Bind(TextBlock.TextProperty, new Binding(nameof(PlsqlRunViewModel.ProblemText)));
        problems.Bind(IsVisibleProperty, new Binding(nameof(PlsqlRunViewModel.ProblemText)) { Converter = Avalonia.Data.Converters.StringConverters.IsNotNullOrEmpty });
        problems.Bind(TextBlock.ForegroundProperty, problems.GetResourceObservable("OrandaErrorBrush"));
        var previewPanel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(problems, Avalonia.Controls.Dock.Bottom);
        previewPanel.Children.Add(problems);
        previewPanel.Children.Add(preview);
        tabs.Items.Add(new TabItem { Header = "호출 블록", Content = previewPanel });

        var results = new ResultTabsView();
        results.Bind(DataContextProperty, new Binding(nameof(PlsqlRunViewModel.Results)));
        tabs.Items.Add(new TabItem { Header = "결과", Content = results });

        var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = Mono };
        output.Bind(TextBox.TextProperty, new Binding(nameof(PlsqlRunViewModel.DbmsOutputText)));
        Avalonia.Automation.AutomationProperties.SetName(output, "DBMS_OUTPUT");
        tabs.Items.Add(new TabItem { Header = "DBMS__OUTPUT", Content = output }); // 「__」 = 밑줄 글자(접근 키 아님)
        Grid.SetRow(tabs, 3);
        root.Children.Add(tabs);

        // 상태: 오류는 오류 색 글, 아니면 보통 글(둘 중 하나만 보인다)
        var status = new StackPanel();
        var okText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        okText.Bind(TextBlock.TextProperty, new Binding(nameof(PlsqlRunViewModel.StatusText)));
        okText.Bind(IsVisibleProperty, new Binding(nameof(PlsqlRunViewModel.StatusIsError)) { Converter = Avalonia.Data.Converters.BoolConverters.Not });
        var errText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        errText.Bind(TextBlock.TextProperty, new Binding(nameof(PlsqlRunViewModel.StatusText)));
        errText.Bind(IsVisibleProperty, new Binding(nameof(PlsqlRunViewModel.StatusIsError)));
        errText.Bind(TextBlock.ForegroundProperty, errText.GetResourceObservable("OrandaErrorBrush"));
        Avalonia.Automation.AutomationProperties.SetName(errText, "실행 오류");
        status.Children.Add(okText);
        status.Children.Add(errText);
        Grid.SetRow(status, 4);
        root.Children.Add(status);

        // 단추
        var run = new Button { Content = "실행", MinWidth = 90, Classes = { "accent" } };
        run.Bind(IsEnabledProperty, new Binding(nameof(PlsqlRunViewModel.CanRun)));
        run.Click += async (_, _) => await _vm.RunAsync().ConfigureAwait(true);
        var cancel = new Button { Content = "실행 취소", MinWidth = 90 };
        cancel.Bind(IsEnabledProperty, new Binding(nameof(PlsqlRunViewModel.IsRunning)));
        cancel.Click += (_, _) => _vm.Cancel();
        var send = new Button { Content = "편집기로 보내기", MinWidth = 120 };
        send.Click += (_, _) =>
        {
            if (_vm.SendToEditor())
            {
                Close();
            }
        };
        var close = new Button { Content = "닫기", MinWidth = 80, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in new[] { run, cancel, send, close })
        {
            buttons.Children.Add(b);
        }

        Grid.SetRow(buttons, 5);
        root.Children.Add(buttons);

        Opened += (_, _) => close.Focus();
        return root;
    }

    private static ColumnDefinitions Columns() => new("150,60,110,*,50,70,150");

    private static Control RowView(PlsqlArgumentRow? row)
    {
        if (row is null)
        {
            return new Panel();
        }

        var g = new Grid { ColumnDefinitions = Columns(), ColumnSpacing = 8, Margin = new Thickness(0, 2), DataContext = row };
        g.Children.Add(Cell(new TextBlock { Text = row.Name, FontFamily = Mono, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, 0));
        g.Children.Add(Cell(new TextBlock { Text = row.DirectionText, VerticalAlignment = VerticalAlignment.Center }, 1));
        g.Children.Add(Cell(new TextBlock { Text = row.DataType, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, 2));

        var value = new TextBox { IsVisible = row.CanEdit };
        value.Bind(TextBox.TextProperty, new Binding(nameof(PlsqlArgumentRow.Text)) { Mode = BindingMode.TwoWay });
        value.Bind(IsEnabledProperty, new Binding(nameof(PlsqlArgumentRow.IsNull)) { Converter = Avalonia.Data.Converters.BoolConverters.Not });
        if (row.Note.Length > 0)
        {
            Avalonia.Controls.ToolTip.SetTip(value, row.Note);
            value.PlaceholderText = row.Note;
        }

        Avalonia.Automation.AutomationProperties.SetName(value, $"{row.Name} 값");
        g.Children.Add(Cell(value, 3));

        var unsupported = new TextBlock { Text = row.Note, IsVisible = !row.Parameter.IsSupported, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(Cell(unsupported, 3));

        var isNull = new CheckBox { IsVisible = row.CanEdit };
        isNull.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(PlsqlArgumentRow.IsNull)) { Mode = BindingMode.TwoWay });
        Avalonia.Automation.AutomationProperties.SetName(isNull, $"{row.Name} NULL");
        g.Children.Add(Cell(isNull, 4));

        var useDefault = new CheckBox { IsVisible = row.CanUseDefault };
        useDefault.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(PlsqlArgumentRow.UseDefault)) { Mode = BindingMode.TwoWay });
        Avalonia.Automation.AutomationProperties.SetName(useDefault, $"{row.Name} 기본값 사용");
        g.Children.Add(Cell(useDefault, 5));

        var outText = new TextBox { IsReadOnly = true, IsVisible = row.Parameter.IsOutput && row.Parameter.IsSupported, FontFamily = Mono };
        outText.Bind(TextBox.TextProperty, new Binding(nameof(PlsqlArgumentRow.OutText)));
        Avalonia.Automation.AutomationProperties.SetName(outText, $"{row.Name} 결과");
        g.Children.Add(Cell(outText, 6));
        return g;
    }

    private static Control Cell(Control c, int column)
    {
        Grid.SetColumn(c, column);
        c.VerticalAlignment = VerticalAlignment.Center;
        return c;
    }
}
