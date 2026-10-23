using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Oranda.App.Features.Localization;
using Oranda.App.Shell.Icons;
using AvDock = Avalonia.Controls.Dock;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>
/// 「바뀌는 부분」 창을 띄운다(design.md 5.1 의 5번). 주 창 위에 모달로 띄우고, 저장하면 체크한 항목, 취소·창 닫기면 null.
/// 주 창이 없으면(헤드리스 시험) 띄우지 않고 null.
/// </summary>
internal sealed class SaveDiffWindowPresenter : ISaveDiffPresenter
{
    /// <summary>대화상자의 주인 창(시험은 바꿔 끼운다).</summary>
    internal Func<Window?> OwnerProvider { get; set; } = MapperSaveUi.MainWindow;

    public async Task<SaveDiffResult?> ShowAsync(SaveDiffRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (OwnerProvider() is not { } owner)
        {
            return null;
        }

        var window = new SaveDiffWindow(new SaveDiffViewModel(request));
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(window.Close));
        await window.ShowDialog(owner).ConfigureAwait(true);
        return window.Answer;
    }
}

/// <summary>
/// 가운데 모달 900×760(시안 SaveDiff): 머리 / 왼쪽 문장 목록 + 검사 상자 / 오른쪽 diff / 바닥 확인란·취소·저장.
/// 상태와 논리는 <see cref="SaveDiffViewModel"/> 에 있고 이 창은 그리기와 단추 연결만 한다. XAML 없이 코드로 만든다.
/// </summary>
internal sealed class SaveDiffWindow : Window
{
    private readonly SaveDiffViewModel _vm;
    private readonly ListBox _items = new();
    private readonly StackPanel _checks = new() { Spacing = 3 };
    private readonly MapperDiffView _diff = new();
    private readonly ToggleButton _oneLine;
    private readonly ToggleButton _sideBySide;
    private readonly CheckBox _showEveryTime;
    private readonly Button _save;
    private bool _syncing;

    public SaveDiffWindow(SaveDiffViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        Title = vm.Title;
        Width = 900;
        Height = 760;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        CanResize = true;

        _oneLine = MapperSaveUi.ToolToggle("docEdit", UiText.Get("MyBatis.save.dialog.oneLine", "한 줄 보기"));
        _sideBySide = MapperSaveUi.ToolToggle("columns", UiText.Get("MyBatis.save.dialog.sideBySide", "나란히 보기"));
        _showEveryTime = new CheckBox { Content = UiText.Get("MyBatis.save.dialog.showEveryTime", "저장할 때마다 이 창 보기"), VerticalAlignment = VerticalAlignment.Center };
        _save = new Button { IsDefault = true, MinWidth = 80, Classes = { "accent" } };
        var cancel = new Button { Content = UiText.Get("Shell.dialog.cancel", "취소"), IsCancel = true, MinWidth = 80 };

        _oneLine.Click += (_, _) =>
        {
            _vm.IsSideBySide = false;
            Sync(); // 이미 눌린 단추를 또 눌러 풀린 표시를 되돌린다
        };
        _sideBySide.Click += (_, _) =>
        {
            _vm.IsSideBySide = true;
            Sync();
        };
        _showEveryTime.IsCheckedChanged += (_, _) =>
        {
            if (!_syncing)
            {
                _vm.ShowEveryTime = _showEveryTime.IsChecked == true;
            }
        };
        _save.Click += (_, _) =>
        {
            if (_vm.BuildResult() is { } result)
            {
                Answer = result;
                Close();
            }
        };
        cancel.Click += (_, _) => Close();

        ConfigureItems();
        Content = BuildLayout(cancel);
        _vm.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => _vm.PropertyChanged -= OnViewModelChanged;
        Opened += (_, _) => _save.Focus();
        Sync();
        _diff.Rows = _vm.Rows;
        ScrollToSelected();
    }

    /// <summary>저장했을 때의 답. 취소·창 닫기면 null.</summary>
    public SaveDiffResult? Answer { get; private set; }

    public SaveDiffViewModel ViewModel => _vm;

    private Grid BuildLayout(Button cancel)
    {
        var title = new TextBlock { Text = _vm.Title, FontSize = 16, FontWeight = FontWeight.SemiBold };
        var description = new TextBlock { Text = SaveDiffViewModel.Description, TextWrapping = TextWrapping.Wrap };
        MapperSaveUi.BindResource(description, TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var head = new StackPanel { Spacing = 4, Margin = new Thickness(20, 14, 20, 12), Children = { title, description } };

        var unchanged = new TextBlock { Text = _vm.UnchangedText, Margin = new Thickness(12, 6) };
        MapperSaveUi.BindResource(unchanged, TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var checksHeading = new TextBlock { Text = SaveDiffViewModel.ChecksHeading, FontWeight = FontWeight.SemiBold };
        var checksBox = new Border { Padding = new Thickness(12, 8), BorderThickness = new Thickness(0, 1, 0, 0), Child = new StackPanel { Spacing = 4, Children = { checksHeading, _checks } } };
        MapperSaveUi.BindResource(checksBox, BorderBrushProperty, "OrandaSeparatorBrush");
        var left = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        left.Children.Add(_items);
        Grid.SetRow(unchanged, 1);
        left.Children.Add(unchanged);
        Grid.SetRow(checksBox, 2);
        left.Children.Add(checksBox);
        var leftPane = new Border { Width = 240, BorderThickness = new Thickness(0, 1, 1, 0), Child = left };
        MapperSaveUi.BindResource(leftPane, BorderBrushProperty, "OrandaSeparatorBrush");

        var diffHeading = new TextBlock { Text = SaveDiffViewModel.DiffHeading, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _sideBySide, _oneLine } };
        var diffHead = new DockPanel { Margin = new Thickness(12, 6) };
        DockPanel.SetDock(toggles, AvDock.Right);
        diffHead.Children.Add(toggles);
        diffHead.Children.Add(diffHeading);
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        right.Children.Add(diffHead);
        Grid.SetRow(_diff, 1);
        right.Children.Add(_diff);
        var rightPane = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = right };
        MapperSaveUi.BindResource(rightPane, BorderBrushProperty, "OrandaSeparatorBrush");

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(leftPane);
        Grid.SetColumn(rightPane, 1);
        body.Children.Add(rightPane);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, _save } };
        var bottom = new DockPanel { Margin = new Thickness(20, 12) };
        DockPanel.SetDock(buttons, AvDock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_showEveryTime);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(head);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);
        return root;
    }

    // 문장 목록: 체크 + id + 요약. 고르면 오른쪽 diff 가 그 문장 머리 줄로 간다.
    private void ConfigureItems()
    {
        _items.ItemsSource = _vm.Items;
        _items.ItemTemplate = new FuncDataTemplate<SaveDiffItemViewModel>((_, _) =>
        {
            var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
            check.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(SaveDiffItemViewModel.IsChecked)) { Mode = BindingMode.TwoWay });
            var id = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            id.Bind(TextBlock.TextProperty, new Binding(nameof(SaveDiffItemViewModel.Id)));
            var summary = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            summary.Bind(TextBlock.TextProperty, new Binding(nameof(SaveDiffItemViewModel.Summary)));
            MapperSaveUi.BindResource(summary, TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            grid.Children.Add(check);
            var texts = new StackPanel { Children = { id, summary } };
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            return grid;
        });
        _items.SelectedItem = _vm.SelectedItem;
        _items.SelectionChanged += (_, _) =>
        {
            if (!_syncing)
            {
                _vm.SelectedItem = _items.SelectedItem as SaveDiffItemViewModel;
            }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SaveDiffViewModel.Rows):
                _diff.Rows = _vm.Rows;
                break;
            case nameof(SaveDiffViewModel.SelectedRowIndex):
                ScrollToSelected();
                break;
            case nameof(SaveDiffViewModel.Checks):
            case nameof(SaveDiffViewModel.CanSave):
            case nameof(SaveDiffViewModel.SaveText):
            case nameof(SaveDiffViewModel.IsSideBySide):
                Sync();
                break;
        }
    }

    // 검사 상자·저장 단추·보기 전환 단추를 VM 에 맞춘다.
    private void Sync()
    {
        _syncing = true;
        try
        {
            _checks.Children.Clear();
            foreach (var line in _vm.Checks)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (ShellIcons.Create(line.IconKey, 14) is { } icon)
                {
                    row.Children.Add(icon);
                }

                row.Children.Add(new TextBlock { Text = line.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 190, VerticalAlignment = VerticalAlignment.Center });
                _checks.Children.Add(row);
            }

            _save.Content = _vm.SaveText;
            _save.IsEnabled = _vm.CanSave;
            _sideBySide.IsChecked = _vm.IsSideBySide;
            _oneLine.IsChecked = !_vm.IsSideBySide;
            _showEveryTime.IsChecked = _vm.ShowEveryTime;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ScrollToSelected()
    {
        int index = _vm.SelectedRowIndex;
        if (index >= 0)
        {
            Dispatcher.UIThread.Post(() => _diff.ScrollToRow(index), DispatcherPriority.Loaded);
        }
    }
}
