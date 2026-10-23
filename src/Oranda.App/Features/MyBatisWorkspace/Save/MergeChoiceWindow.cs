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
using Oranda.App.ViewModels;
using AvDock = Avalonia.Controls.Dock;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>고르기 창 왼쪽 목록의 충돌 문장 하나: 「파일 것으로 / 내 것으로」 라디오(기본 내 것).</summary>
public sealed class MergeChoiceItemViewModel : ViewModelBase
{
    private MapperMergeChoice _choice = MapperMergeChoice.Mine;

    public MergeChoiceItemViewModel(MapperMergeConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        Conflict = conflict;
    }

    public MapperMergeConflict Conflict { get; }

    public string Id => Conflict.CurrentId;

    /// <summary>「양쪽이 고침」·「파일에서 지워짐」·「내가 지움」.</summary>
    public string Summary => Conflict.DiskText is null
        ? UiText.Get("MyBatis.save.merge.diskDeleted", "파일에서 지워짐")
        : Conflict.MineText is null
            ? UiText.Get("MyBatis.save.merge.mineDeleted", "내가 지움")
            : UiText.Get("MyBatis.save.merge.both", "양쪽이 고침");

    public MapperMergeChoice Choice
    {
        get => _choice;
        set
        {
            if (SetProperty(ref _choice, value))
            {
                OnPropertyChanged(nameof(IsMine));
                OnPropertyChanged(nameof(IsDisk));
            }
        }
    }

    public bool IsMine
    {
        get => _choice == MapperMergeChoice.Mine;
        set
        {
            if (value)
            {
                Choice = MapperMergeChoice.Mine;
            }
        }
    }

    public bool IsDisk
    {
        get => _choice == MapperMergeChoice.Disk;
        set
        {
            if (value)
            {
                Choice = MapperMergeChoice.Disk;
            }
        }
    }
}

/// <summary>「차이 보고 고르기」 창의 상태: 충돌 문장 목록, 고른 문장의 파일 것 ↔ 내 것 diff, 문장마다의 답.</summary>
public sealed class MergeChoiceViewModel : ViewModelBase
{
    private MergeChoiceItemViewModel? _selected;
    private IReadOnlyList<MapperDiffRow> _rows = [];

    public MergeChoiceViewModel(string fileName, IReadOnlyList<MapperMergeConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        FileName = fileName;
        Items = [.. conflicts.Select(c => new MergeChoiceItemViewModel(c))];
        SelectedItem = Items.Count > 0 ? Items[0] : null;
    }

    public string FileName { get; }

    public IReadOnlyList<MergeChoiceItemViewModel> Items { get; }

    public string Title => MapperSaveUi.Format("MyBatis.save.merge.title", "{0} 의 양쪽이 고친 문장", FileName);

    public MergeChoiceItemViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                // 지워진 쪽은 빈 글로 맞댄다: 왼쪽 = 파일 것, 오른쪽 = 내 것.
                _rows = value is null
                    ? []
                    : MapperDiffRow.Section(null, MapperLineDiff.Build(value.Conflict.DiskText ?? string.Empty, value.Conflict.MineText ?? string.Empty, 1, 1), sideBySide: true);
                OnPropertyChanged(nameof(Rows));
            }
        }
    }

    public IReadOnlyList<MapperDiffRow> Rows => _rows;

    /// <summary>모든 충돌의 답(키 = <see cref="MapperMergeConflict.Key"/>).</summary>
    public IReadOnlyDictionary<string, MapperMergeChoice> BuildResult() =>
        Items.ToDictionary(i => i.Conflict.Key, i => i.Choice, StringComparer.Ordinal);
}

/// <summary>
/// 알림 띠 「차이 보고 고르기」 창을 띄운다(design.md 5.3). 확인하면 문장마다의 답, 취소·창 닫기면 null.
/// 주 창이 없으면(헤드리스 시험) 띄우지 않고 null.
/// </summary>
internal sealed class MergeChoiceWindowPresenter : IMergeChoicePresenter
{
    /// <summary>대화상자의 주인 창(시험은 바꿔 끼운다).</summary>
    internal Func<Window?> OwnerProvider { get; set; } = MapperSaveUi.MainWindow;

    public async Task<IReadOnlyDictionary<string, MapperMergeChoice>?> ChooseAsync(string fileName, IReadOnlyList<MapperMergeConflict> conflicts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        if (conflicts.Count == 0 || OwnerProvider() is not { } owner)
        {
            return null;
        }

        var window = new MergeChoiceWindow(new MergeChoiceViewModel(fileName, conflicts));
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(window.Close));
        await window.ShowDialog(owner).ConfigureAwait(true);
        return window.Answer;
    }
}

/// <summary>왼쪽 충돌 문장 목록(라디오) · 오른쪽 파일 것 ↔ 내 것 나란히 diff · 바닥 취소/확인. 코드로 만든다.</summary>
internal sealed class MergeChoiceWindow : Window
{
    private readonly MergeChoiceViewModel _vm;
    private readonly ListBox _items = new();
    private readonly MapperDiffView _diff = new();

    public MergeChoiceWindow(MergeChoiceViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        Title = vm.Title;
        Width = 900;
        Height = 620;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        CanResize = true;

        var ok = new Button { Content = UiText.Get("Shell.dialog.ok", "확인"), IsDefault = true, MinWidth = 80, Classes = { "accent" } };
        var cancel = new Button { Content = UiText.Get("Shell.dialog.cancel", "취소"), IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            Answer = _vm.BuildResult();
            Close();
        };
        cancel.Click += (_, _) => Close();

        ConfigureItems();
        Content = BuildLayout(ok, cancel);
        _vm.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => _vm.PropertyChanged -= OnViewModelChanged;
        Opened += (_, _) => ok.Focus();
        _diff.Rows = _vm.Rows;
    }

    /// <summary>확인했을 때의 답. 취소·창 닫기면 null.</summary>
    public IReadOnlyDictionary<string, MapperMergeChoice>? Answer { get; private set; }

    public MergeChoiceViewModel ViewModel => _vm;

    private Grid BuildLayout(Button ok, Button cancel)
    {
        var hint = new TextBlock { Text = UiText.Get("MyBatis.save.merge.hint", "양쪽이 고친 문장마다 어느 쪽을 남길지 고르십시오. 고르지 않으면 내 것을 남깁니다."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 12) };
        MapperSaveUi.BindResource(hint, TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");

        var leftPane = new Border { Width = 260, BorderThickness = new Thickness(0, 1, 1, 0), Child = _items };
        MapperSaveUi.BindResource(leftPane, BorderBrushProperty, "OrandaSeparatorBrush");

        var diskHead = new TextBlock { Text = UiText.Get("MyBatis.save.merge.disk", "파일 것"), FontWeight = FontWeight.SemiBold };
        var mineHead = new TextBlock { Text = UiText.Get("MyBatis.save.merge.mine", "내 것"), FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(mineHead, 1);
        var heads = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(46, 6, 0, 6), Children = { diskHead, mineHead } };
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        right.Children.Add(heads);
        Grid.SetRow(_diff, 1);
        right.Children.Add(_diff);
        var rightPane = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = right };
        MapperSaveUi.BindResource(rightPane, BorderBrushProperty, "OrandaSeparatorBrush");

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(leftPane);
        Grid.SetColumn(rightPane, 1);
        body.Children.Add(rightPane);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, ok } };
        var bottom = new DockPanel { Margin = new Thickness(20, 12), LastChildFill = false };
        DockPanel.SetDock(buttons, AvDock.Right);
        bottom.Children.Add(buttons);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(hint);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);
        return root;
    }

    // 문장 한 줄: id + 요약 + 「파일 것으로 / 내 것으로」 라디오(같은 부모 안에서 한 묶음).
    private void ConfigureItems()
    {
        _items.ItemsSource = _vm.Items;
        _items.ItemTemplate = new FuncDataTemplate<MergeChoiceItemViewModel>((_, _) =>
        {
            var id = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            id.Bind(TextBlock.TextProperty, new Binding(nameof(MergeChoiceItemViewModel.Id)));
            var summary = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            summary.Bind(TextBlock.TextProperty, new Binding(nameof(MergeChoiceItemViewModel.Summary)));
            MapperSaveUi.BindResource(summary, TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
            var disk = new RadioButton { Content = UiText.Get("MyBatis.save.merge.useDisk", "파일 것으로") };
            disk.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MergeChoiceItemViewModel.IsDisk)) { Mode = BindingMode.TwoWay });
            var mine = new RadioButton { Content = UiText.Get("MyBatis.save.merge.useMine", "내 것으로") };
            mine.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MergeChoiceItemViewModel.IsMine)) { Mode = BindingMode.TwoWay });
            var radios = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { disk, mine } };
            return new StackPanel { Spacing = 1, Margin = new Thickness(0, 2), Children = { id, summary, radios } };
        });
        _items.SelectedItem = _vm.SelectedItem;
        _items.SelectionChanged += (_, _) => _vm.SelectedItem = _items.SelectedItem as MergeChoiceItemViewModel;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MergeChoiceViewModel.Rows))
        {
            _diff.Rows = _vm.Rows;
        }
    }
}
