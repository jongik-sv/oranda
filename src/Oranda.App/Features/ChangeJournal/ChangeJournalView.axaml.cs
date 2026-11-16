using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Oranda.App.Shell.Controls;

namespace Oranda.App.Features.ChangeJournal;

public partial class ChangeJournalView : UserControl
{
    public ChangeJournalView()
    {
        InitializeComponent();
        List.SelectionChanged += async (_, _) =>
        {
            if (DataContext is ChangeJournalViewModel vm)
            {
                await vm.SetRowSelectionAsync([.. List.SelectedItems?.OfType<IJournalRow>() ?? []]);
            }
        };
        List.DoubleTapped += (_, e) => Activate((e.Source as Avalonia.StyledElement)?.DataContext as IJournalRow);
        OpenButton.Click += (_, _) => OpenSelected();
        NarrowOpenButton.Click += (_, _) => OpenSelected();
        OpenScriptButton.Click += OnOpenScript;
        NarrowOpenScriptButton.Click += OnOpenScript;
        CompareButton.Click += OnCompare;
        NarrowCompareButton.Click += OnCompare;
        RefreshButton.Click += OnRefresh;
        NarrowRefreshButton.Click += OnRefresh;
        ClearFilterButton.Click += OnClearFilter;
        NarrowClearFilterButton.Click += OnClearFilter;
        CopyDetailButton.Click += (_, _) => _ = CopyAsync((DataContext as ChangeJournalViewModel)?.DetailText);
        CopyDiffButton.Click += (_, _) => _ = CopyAsync(DataContext is ChangeJournalViewModel vm ? DiffText(vm) : null);
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is ChangeJournalViewModel vm)
            {
                await vm.ReloadAsync();
            }
        };
    }

    /// <summary>좁은 배치일 때 아래 두 상자를 세로로 쌓는다(시험용).</summary>
    internal bool PanesStacked => Grid.GetRow(DiffPane) == 2;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NarrowLayout.IsNarrowProperty)
        {
            ApplyPaneLayout(change.GetNewValue<bool>());
        }
    }

    /// <summary>위 상자(<see cref="DetailBox"/>) 안의 읽기 전용 편집기.</summary>
    internal AvaloniaEdit.TextEditor? DetailEditor => DetailBox.GetLogicalDescendants().OfType<AvaloniaEdit.TextEditor>().FirstOrDefault();

    /// <summary>복사할 원복 스크립트·차이 글: 차이 줄은 「+ 」·「- 」 를 앞에 붙이고, 스크립트(같은 줄)는 그대로.</summary>
    internal static string DiffText(ChangeJournalViewModel vm) =>
        string.Join(Environment.NewLine, vm.Diff.Select(d => d.Kind is '+' or '-' ? d.Kind + " " + d.Text : d.Text));

    // 넓은 배치 = 좌우 반반(열 *,5,*), 좁은 배치 = 위아래(행 *,5,*). 바꿀 때마다 반반으로 돌아간다(끌어 둔 폭을 높이로 쓰지 않게).
    private void ApplyPaneLayout(bool narrow)
    {
        PaneGrid.ColumnDefinitions = narrow ? new ColumnDefinitions("*") : new ColumnDefinitions("*,5,*");
        PaneGrid.RowDefinitions = narrow ? new RowDefinitions("*,5,*") : new RowDefinitions("*");
        Grid.SetColumn(DetailPane, 0);
        Grid.SetRow(DetailPane, 0);
        Grid.SetColumn(PaneSplitter, narrow ? 0 : 1);
        Grid.SetRow(PaneSplitter, narrow ? 1 : 0);
        Grid.SetColumn(DiffPane, narrow ? 0 : 2);
        Grid.SetRow(DiffPane, narrow ? 2 : 0);
        PaneSplitter.ResizeDirection = narrow ? GridResizeDirection.Rows : GridResizeDirection.Columns;

        // 좁으면 위 상자(실행한 문장·DDL) 글을 줄바꿈해 가로로 잘리지 않게. SqlCodeView 는 안쪽 편집기 하나를 품는다.
        if (DetailEditor is { } editor)
        {
            editor.WordWrap = narrow;
        }
    }

    private async void OnOpenScript(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChangeJournalViewModel vm)
        {
            await vm.OpenScriptAsync();
        }
    }

    private async void OnCompare(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChangeJournalViewModel vm)
        {
            await vm.CompareAsync();
        }
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChangeJournalViewModel vm)
        {
            await vm.ReloadAsync();
        }
    }

    private void OnClearFilter(object? sender, RoutedEventArgs e) => (DataContext as ChangeJournalViewModel)?.ClearObjectFilter();

    private async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            if (DataContext is ChangeJournalViewModel done)
            {
                done.StatusMessage = "복사했다";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (DataContext is ChangeJournalViewModel vm)
            {
                vm.StatusMessage = "복사하지 못했다: " + ex.Message;
            }
        }
    }

    /// <summary>두 번 누른 그 줄: 묶음 줄은 접고 펴고, DDL 보관본은 새 편집기에 연다. 문장 줄은 아무것도 하지 않는다.</summary>
    private void Activate(IJournalRow? row)
    {
        if (DataContext is not ChangeJournalViewModel vm)
        {
            return;
        }

        switch (row)
        {
            case RollbackGroupItem group:
                vm.ToggleExpand(group);
                break;
            case JournalItem item:
                vm.OpenInEditor(item);
                break;
        }
    }

    private void OpenSelected()
    {
        if (DataContext is ChangeJournalViewModel vm && List.SelectedItems?.OfType<JournalItem>().FirstOrDefault() is { } item)
        {
            vm.OpenInEditor(item);
        }
    }
}
