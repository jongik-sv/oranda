using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Oranda.App.Shell.Icons;

namespace Oranda.App.Features.HistoryScripts;

// ScriptsView 의 일부: 거르기 줄·결과 목록 입력과 편집기로 열기(TSK-10-01).
public partial class ScriptsView
{
    private ScriptsViewModel? _searchWired;

    private void WireSearch()
    {
        SearchBox.InnerLeftContent = ShellIcons.Create("find", 14);
        AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        ResultList.DoubleTapped += async (_, _) =>
        {
            if (DataContext is ScriptsViewModel { SelectedResult: { } row } vm)
            {
                await vm.OpenResultAsync(row);
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ScriptsViewModel vm && !ReferenceEquals(_searchWired, vm))
            {
                _searchWired = vm;
                vm.OpenAtRequested += async (_, request) => await OpenAtAsync(vm, request);
            }
        };
    }

    /// <summary>파일을 새 편집기 탭(이미 열려 있으면 그 탭)으로 열고 그 줄로 이동한다.</summary>
    private static async System.Threading.Tasks.Task OpenAtAsync(ScriptsViewModel vm, ScriptOpenAt request)
    {
        try
        {
            var tab = await SqlEditor.SqlEditorHost.Current.OpenFileAsync(request.Path, default);
            if (tab is not null && request.Line > 0)
            {
                tab.GoToLine(request.Line);
                // 탭이 막 만들어졌으면 화면이 붙은 뒤 한 번 더 이동해 그 줄이 보이게 한다.
                Dispatcher.UIThread.Post(() => tab.GoToLine(request.Line), DispatcherPriority.Loaded);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            vm.StatusMessage = "파일을 열지 못했다: " + ex.Message.ReplaceLineEndings(" ");
        }
    }

    // Ctrl+F = 거르기 칸, Esc = 검색 지우고 트리로, 칸에서 ↓ = 결과 첫 줄, 칸에서 Enter = 첫 결과 열기, 결과 목록에서 Enter = 고른 줄 열기.
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ScriptsViewModel vm)
        {
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && (vm.IsSearchActive || vm.SearchText.Length > 0))
        {
            vm.ClearSearch();
            Dispatcher.UIThread.Post(() => Tree.Focus(), DispatcherPriority.Input);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && SearchBox.IsFocused && vm.IsSearchActive)
        {
            e.Handled = true;
            _ = FocusFirstResultAsync(vm);
        }
        else if (e.Key == Key.Enter && SearchBox.IsFocused && vm.IsSearchActive)
        {
            e.Handled = true;
            _ = OpenFirstResultAsync(vm);
        }
        else if (e.Key == Key.Enter && ResultList.IsKeyboardFocusWithin && vm.SelectedResult is { } row)
        {
            e.Handled = true;
            _ = vm.OpenResultAsync(row);
        }
    }

    private async System.Threading.Tasks.Task FocusFirstResultAsync(ScriptsViewModel vm)
    {
        await vm.SearchNowAsync();
        if (vm.FirstResult is { } first)
        {
            vm.SelectedResult = first;
            Dispatcher.UIThread.Post(() => ResultList.ContainerFromIndex(ResultList.SelectedIndex)?.Focus(), DispatcherPriority.Loaded);
        }
    }

    private static async System.Threading.Tasks.Task OpenFirstResultAsync(ScriptsViewModel vm)
    {
        await vm.SearchNowAsync();
        await vm.OpenResultAsync(vm.SelectedResult ?? vm.FirstResult);
    }
}
