using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Controls;
using static Oranda.App.Features.Tuning.Stats.TuningText;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>
/// 「통계 수집…」 확인 창(SG-1). 생김새는 세션 종료 확인 창(<c>LockKillDialog</c>)을 따른다: 머리 띠 = 접속 환경 색, 운영이면 위험 띠에 경고를 크게,
/// 대상·옵션·보낼 문장, 아래 단추 줄(기본 = 취소). 실행해도 창은 남아 테이블마다 상태·걸린 시간·결과를 보인다.
/// </summary>
internal static class GatherStatsDialog
{
    /// <param name="openScript">「스크립트로 열기」(본문, 제목). 누르면 창을 닫는다.</param>
    public static async Task ShowAsync(Window? owner, GatherStatsDialogViewModel vm, Action<string, string> openScript)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(openScript);
        var window = new Window
        {
            Title = GatherStatsDialogViewModel.Title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            MinWidth = 520,
            MaxWidth = 860,
            ShowInTaskbar = false,
            DataContext = vm,
        };
        window.Content = Build(vm, out var cancel, out var run, out var script, out var background);
        Wire(window, vm, run, background);
        cancel.Click += (_, _) =>
        {
            if (vm.IsRunning)
            {
                vm.Cancel();
                return;
            }

            window.Close();
        };
        script.Click += (_, _) =>
        {
            openScript(vm.ScriptText, vm.ScriptTitle);
            window.Close();
        };

        // 실행 중에 창을 닫으면 지금 테이블을 멈춘다(세션 cancel). 백그라운드 실행은 닫아도 이어 가고 끝나면 스스로 정리한다.
        window.Closing += (_, _) =>
        {
            if (!vm.IsBackground)
            {
                vm.Cancel();
            }
        };
        window.Closed += (_, _) =>
        {
            if (!vm.IsBackground)
            {
                vm.Dispose();
            }
        };
        window.Opened += (_, _) => cancel.Focus();
        DialogShell.AutomationName(window, GatherStatsDialogViewModel.Title);
        await DialogShell.ShowAsync(window, owner).ConfigureAwait(true);
    }

    /// <summary>
    /// 실행 단추와 닫기(S4): 「통계 수집 실행」 = 이 창에서 진행을 보이고 다 성공하면 닫는다. 「백그라운드로 실행」 = 누르는 즉시 닫고 실행은 이어 간다.
    /// </summary>
    internal static void Wire(Window window, GatherStatsDialogViewModel vm, Button run, Button background)
    {
        run.Click += (_, _) => _ = vm.RunAsync();
        background.Click += (_, _) => _ = vm.RunInBackgroundAsync();
        vm.CloseRequested += (_, _) => window.Close();
    }

    /// <summary>창 본문(시험이 그대로 그려 본다). DataContext 는 부르는 쪽이 <paramref name="vm"/> 으로 둔다.</summary>
    internal static Control Build(GatherStatsDialogViewModel vm, out Button cancel, out Button run, out Button script, out Button background)
    {
        var header = new Border { Padding = new Thickness(16, 10) };
        var title = new TextBlock { Text = $"[{vm.EnvironmentText}] {vm.HeaderTitle}", FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var detail = new TextBlock { Text = vm.HeaderDetail, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        EnvironmentColors.ApplyHeader(header, [title, detail], vm.Profile.Environment, vm.Profile.Color,
            EnvironmentColors.BrushKeyOf(vm.Profile.Environment), "OrandaEnvProductionForegroundBrush");
        header.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { title, detail } };

        var body = new StackPanel { Margin = new Thickness(16, 14), Spacing = 10 };
        body.Children.Add(new TextBlock { Text = vm.Question, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });

        if (vm.HasProductionWarning)
        {
            var callout = new Border { Name = "ProductionWarning", Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2) };
            callout.Bind(Border.BackgroundProperty, callout.GetResourceObservable("OrandaDiffRemovedBrush"));
            callout.Bind(Border.BorderBrushProperty, callout.GetResourceObservable("OrandaDangerForegroundBrush"));
            Control icon = Shell.Icons.ShellIcons.Create("warn", 20) ?? new Panel();
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 10, 0);
            DockPanel.SetDock(icon, Avalonia.Controls.Dock.Left);
            callout.Child = new DockPanel
            {
                Children =
                {
                    icon,
                    new TextBlock { Text = vm.ProductionWarning, FontSize = 16, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 },
                },
            };
            DialogShell.AutomationName(callout, vm.ProductionWarning);
            body.Children.Add(callout);
        }

        // 대상 테이블: 이름 · 상태 · 걸린 시간 · 결과 글.
        var rows = new ItemsControl
        {
            Name = "GatherTargets",
            ItemsSource = vm.Rows,
            ItemTemplate = new FuncDataTemplate<GatherStatsRow>((_, _) =>
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,72,64,*"), ColumnSpacing = 10 };
                var name = new TextBlock { Classes = { "mono" } };
                name.Bind(TextBlock.TextProperty, new Binding(nameof(GatherStatsRow.Display)));
                var status = new TextBlock();
                status.Bind(TextBlock.TextProperty, new Binding(nameof(GatherStatsRow.StatusText)));
                var elapsed = new TextBlock { Classes = { "dim" }, HorizontalAlignment = HorizontalAlignment.Right };
                elapsed.Bind(TextBlock.TextProperty, new Binding(nameof(GatherStatsRow.ElapsedText)));
                var message = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Classes = { "warning-text" } };
                message.Bind(TextBlock.TextProperty, new Binding(nameof(GatherStatsRow.Message)));
                Grid.SetColumn(status, 1);
                Grid.SetColumn(elapsed, 2);
                Grid.SetColumn(message, 3);
                grid.Children.Add(name);
                grid.Children.Add(status);
                grid.Children.Add(elapsed);
                grid.Children.Add(message);
                return grid;
            }),
        };
        body.Children.Add(new ScrollViewer { MaxHeight = 160, Content = rows });

        body.Children.Add(Options(vm));

        var error = new TextBlock { Classes = { "warning-text" }, TextWrapping = TextWrapping.Wrap };
        error.Bind(TextBlock.TextProperty, new Binding(nameof(GatherStatsDialogViewModel.OptionsError)));
        error.Bind(Visual.IsVisibleProperty, new Binding(nameof(GatherStatsDialogViewModel.HasOptionsError)));
        body.Children.Add(error);

        body.Children.Add(new TextBlock { Text = GatherStatsDialogViewModel.Note, TextWrapping = TextWrapping.Wrap, FontSize = 12, Classes = { "dim" }, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left });
        var sql = new SqlCodeView { ShowLineNumbers = false, MinWidth = 480, MaxHeight = 220 };
        sql.Bind(SqlCodeView.TextProperty, new Binding(nameof(GatherStatsDialogViewModel.Preview)));
        DialogShell.AutomationName(sql, T("보낼 문장", "Statement to send"));
        body.Children.Add(new Border { Classes = { "code" }, Padding = new Thickness(2), Child = sql });

        if (vm.HasBlockedText)
        {
            body.Children.Add(new TextBlock { Name = "GatherBlocked", Text = vm.BlockedText, Classes = { "warning-text" }, TextWrapping = TextWrapping.Wrap });
        }

        cancel = new Button { MinWidth = 80, IsDefault = true, IsCancel = true, Classes = { "accent" } };
        cancel.Bind(ContentControl.ContentProperty, new Binding(nameof(GatherStatsDialogViewModel.CancelText)));
        run = new Button { Content = T("통계 수집 실행", "Gather now"), MinWidth = 80 };
        run.Bind(Button.ForegroundProperty, run.GetResourceObservable("OrandaDangerForegroundBrush"));
        run.Bind(InputElement.IsEnabledProperty, new Binding(nameof(GatherStatsDialogViewModel.CanRun)));
        DialogShell.AutomationName(run, T("통계 수집 실행", "Gather now"));
        background = new Button { Name = "GatherInBackground", Content = T("백그라운드로 실행", "Run in background"), MinWidth = 80 };
        background.Bind(InputElement.IsEnabledProperty, new Binding(nameof(GatherStatsDialogViewModel.CanRun)));
        ToolTip.SetTip(background, T("창을 바로 닫고 뒤에서 수집합니다. 결과는 실행 이력과 스키마 브라우저 상태 줄에 보입니다.", "Close this window and gather in the background. Results show in the history and the schema browser status line."));
        DialogShell.AutomationName(background, T("백그라운드로 실행", "Run in background"));
        script = new Button { Content = T("스크립트로 열기", "Open as script"), MinWidth = 80 };
        script.Bind(InputElement.IsEnabledProperty, new Binding(nameof(GatherStatsDialogViewModel.CanEditOptions)));
        ToolTip.SetTip(script, T("실행하지 않고 같은 문안을 새 편집기 탭에 넣습니다.", "Put the same text in a new editor tab without running it."));
        var buttons = DialogShell.Buttons(script, cancel, background, run);
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
        var summary = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(GatherStatsDialogViewModel.SummaryText)));
        var progress = new ProgressBar { IsIndeterminate = true, Height = 2, MinHeight = 2 };
        progress.Bind(Visual.IsVisibleProperty, new Binding(nameof(GatherStatsDialogViewModel.IsRunning)));
        var footer = new Border { Padding = new Thickness(16, 10), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.Bind(Border.BackgroundProperty, footer.GetResourceObservable("OrandaToolbarBackgroundBrush"));
        footer.Bind(Border.BorderBrushProperty, footer.GetResourceObservable("OrandaSeparatorBrush"));
        footer.Child = new DockPanel { Children = { buttons, summary } };

        var root = new DockPanel();
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(progress, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(footer, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(progress);
        root.Children.Add(footer);
        root.Children.Add(body);
        return root;
    }

    /// <summary>옵션 칸: 표본(자동/퍼센트), 히스토그램, 인덱스 포함, 병렬. 공급자가 지원하지 않는 칸은 넣지 않는다.</summary>
    private static Grid Options(GatherStatsDialogViewModel vm)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 6 };
        var row = 0;
        void Add(string label, Control value)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = new TextBlock { Text = label, Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(k, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(k);
            grid.Children.Add(value);
            row++;
        }

        var enabled = new Binding(nameof(GatherStatsDialogViewModel.CanEditOptions));

        var auto = new CheckBox { Content = T("자동 (AUTO_SAMPLE_SIZE)", "Auto (AUTO_SAMPLE_SIZE)"), Name = "GatherAutoSample" };
        auto.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(GatherStatsDialogViewModel.AutoSample)) { Mode = BindingMode.TwoWay });
        auto.Bind(InputElement.IsEnabledProperty, enabled);
        var percent = new TextBox { Width = 80, Name = "GatherPercent", PlaceholderText = "%" };
        percent.Bind(TextBox.TextProperty, new Binding(nameof(GatherStatsDialogViewModel.SamplePercentText)) { Mode = BindingMode.TwoWay });
        percent.Bind(InputElement.IsEnabledProperty, new MultiBinding
        {
            Converter = Avalonia.Data.Converters.BoolConverters.And,
            Bindings = { enabled, new Binding(nameof(GatherStatsDialogViewModel.AutoSample)) { Converter = Avalonia.Data.Converters.BoolConverters.Not } },
        });
        DialogShell.AutomationName(percent, T("표본 퍼센트", "Sample percent"));
        Add(T("표본", "Sample"), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { auto, percent, new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center } } });

        var histogram = new ComboBox { ItemsSource = vm.HistogramChoices, MinWidth = 260, Name = "GatherHistogram" };
        histogram.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(GatherStatsDialogViewModel.SelectedHistogram)) { Mode = BindingMode.TwoWay });
        histogram.Bind(InputElement.IsEnabledProperty, enabled);
        DialogShell.AutomationName(histogram, T("열 히스토그램", "Column histograms"));
        Add(T("열 히스토그램", "Column histograms"), histogram);

        if (vm.Support.Cascade)
        {
            var cascade = new CheckBox { Content = T("인덱스 통계도 함께 (CASCADE)", "Index statistics too (CASCADE)"), Name = "GatherCascade" };
            cascade.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(GatherStatsDialogViewModel.Cascade)) { Mode = BindingMode.TwoWay });
            cascade.Bind(InputElement.IsEnabledProperty, enabled);
            Add(T("인덱스", "Indexes"), cascade);
        }

        if (vm.Support.Degree)
        {
            var degree = new TextBox { Width = 80, Name = "GatherDegree", PlaceholderText = T("기본", "default") };
            degree.Bind(TextBox.TextProperty, new Binding(nameof(GatherStatsDialogViewModel.DegreeText)) { Mode = BindingMode.TwoWay });
            degree.Bind(InputElement.IsEnabledProperty, enabled);
            DialogShell.AutomationName(degree, T("병렬 정도", "Degree of parallelism"));
            Add(T("병렬 (DEGREE)", "Parallel (DEGREE)"), new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { degree, new TextBlock { Text = T("비우면 테이블 설정", "empty = table setting"), Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center } },
            });
        }

        return grid;
    }
}
