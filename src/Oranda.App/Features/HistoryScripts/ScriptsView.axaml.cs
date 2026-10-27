using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Oranda.App.Shell.Icons;

namespace Oranda.App.Features.HistoryScripts;

public partial class ScriptsView : UserControl
{
    private readonly System.Func<NewScriptRequest, System.Threading.Tasks.Task<string?>> _prompt;
    private ScriptsViewModel? _wired;

    public ScriptsView()
    {
        InitializeComponent();
        AddButton.Content = ShellIcons.Create("folderAdd");
        RemoveButton.Content = ShellIcons.Create("folderRemove");
        RefreshButton.Content = ShellIcons.Create("refresh");
        RunButton.Content = ShellIcons.Create("run");
        WireSearch();
        NewScriptButton.Content = ShellIcons.Create("newSql");
        AddButton.Click += async (_, _) => await AddFolderAsync();
        RemoveButton.Click += (_, _) =>
        {
            if (DataContext is ScriptsViewModel { SelectedNode: { IsRoot: true } root } vm)
            {
                vm.RemoveFolder(root);
            }
        };
        RefreshButton.Click += (_, _) =>
        {
            if (DataContext is ScriptsViewModel vm)
            {
                vm.Refresh(vm.SelectedNode);
                vm.RefreshSearch();
            }
        };
        RunButton.Click += async (_, _) =>
        {
            if (DataContext is ScriptsViewModel { SelectedNode: { IsDirectory: false } node } vm)
            {
                await vm.RunAsync(node);
            }
        };
        NewScriptButton.Click += async (_, _) => await NewScriptAsync();
        NewScriptMenu.Click += async (_, _) => await NewScriptAsync();
        Tree.AddHandler(PointerPressedEvent, OnTreePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tree.DoubleTapped += async (_, _) =>
        {
            if (DataContext is ScriptsViewModel { SelectedNode: { } node } vm && !node.IsDirectory)
            {
                await vm.OpenAsync(node);
            }
        };
        Tree.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is ScriptsViewModel { SelectedNode: { } node } vm)
            {
                await vm.OpenAsync(node);
                e.Handled = true;
            }
        };
        // 파일을 열면 새 편집기 탭으로 연다(Orange SQL Tool 의 File Mode). 활성 편집기에 끼워 넣기는 쓰지 않는다.
        // 도킹은 탭을 오갈 때마다 이 보기를 새로 만든다. ViewModel 알림은 화면에 붙어 있는 동안만 받는다:
        // 떼지 않으면 보기 수만큼 받는 곳이 쌓여 파일 하나가 편집기 탭 여러 개로 열린다(TSK-12-07).
        _prompt = request => NewScriptDialog.ShowAsync(request, TopLevel.GetTopLevel(this) as Window);
        DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree())
            {
                Wire(DataContext as ScriptsViewModel);
            }
        };
        AttachedToVisualTree += (_, _) =>
        {
            Wire(DataContext as ScriptsViewModel);
            if (DataContext is ScriptsViewModel { Roots.Count: 0 } vm)
            {
                vm.Reload();
            }
        };
        DetachedFromVisualTree += (_, _) => Wire(null);
    }

    /// <summary>받는 ViewModel 을 바꾼다(null = 모두 뗀다). 같은 ViewModel 이면 아무 일도 없다.</summary>
    private void Wire(ScriptsViewModel? vm)
    {
        if (ReferenceEquals(_wired, vm))
        {
            return;
        }

        if (_wired is { } old)
        {
            old.PropertyChanged -= OnViewModelPropertyChanged;
            old.OpenRequested -= OnOpenRequested;
            old.OpenAtRequested -= OnOpenAtRequested;
            if (old.NewScriptPrompt == _prompt)
            {
                old.NewScriptPrompt = null; // 다음에 붙는 보기가 자기 창을 주인으로 띄운다
            }
        }

        _wired = vm;
        if (vm is null)
        {
            return;
        }

        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.OpenRequested += OnOpenRequested;
        vm.OpenAtRequested += OnOpenAtRequested;
        vm.NewScriptPrompt ??= _prompt;
        SyncMenu(vm);
    }

    // 메뉴는 열리기 전에는 데이터 문맥을 받지 못해 바인딩 대신 코드로 맞춘다.
    private void SyncMenu(ScriptsViewModel vm) => NewScriptMenu.IsEnabled = vm.CanNewScript;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScriptsViewModel.CanNewScript) && sender is ScriptsViewModel vm)
        {
            SyncMenu(vm);
        }
    }

    private async void OnOpenRequested(object? sender, string path) => await SqlEditor.SqlEditorHost.Current.OpenFileAsync(path, default);

    private async System.Threading.Tasks.Task NewScriptAsync()
    {
        if (DataContext is ScriptsViewModel vm)
        {
            await vm.NewScriptAsync();
            // 새 파일이 길어서 보이는 영역 밖이면 보이는 곳으로 가져온다.
            if (vm.SelectedNode is { } node)
            {
                Dispatcher.UIThread.Post(() => Tree.TreeContainerFromItem(node)?.BringIntoView(), DispatcherPriority.Loaded);
            }
        }
    }

    /// <summary>오른쪽 단추는 누른 줄을 먼저 고른다(오른쪽 클릭 메뉴가 그 줄의 폴더에 대해 열린다).</summary>
    private void OnTreePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ScriptsViewModel vm
            && e.GetCurrentPoint(Tree).Properties.IsRightButtonPressed
            && (e.Source as Control)?.DataContext is ScriptNode { FullPath.Length: > 0 } node)
        {
            vm.SelectedNode = node;
        }
    }

    private async System.Threading.Tasks.Task AddFolderAsync()
    {
        if (DataContext is not ScriptsViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "스크립트 폴더 연결", AllowMultiple = false });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            vm.AddFolder(path);
        }
    }
}
