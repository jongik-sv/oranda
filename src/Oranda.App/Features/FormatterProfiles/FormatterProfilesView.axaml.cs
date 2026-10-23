using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Oranda.App.Shell.Icons;

namespace Oranda.App.Features.FormatterProfiles;

/// <summary>
/// 포매터 프로필 화면(TSK-13-42 A안). 옵션 묶음은 <see cref="FormatterOptionGroups"/> 가 ViewModel 의 옵션 행으로 한 번 만든다.
/// 기본 제공 프로필이면 옵션을 흐리게 하지 않고 그대로 보이되, 누르면 바꾸지 않고 상태 줄에 「복사해서 고치기」를 안내한다.
/// 목록: F2 = 이름 바꾸기, Delete = 삭제, 오른쪽 클릭 = 복사·이름 바꾸기·삭제·기본 지정·내보내기.
/// </summary>
public partial class FormatterProfilesView : UserControl
{
    /// <summary>기본 제공 프로필 옵션을 누르면 상태 줄에 보이는 글.</summary>
    public const string ReadOnlyHint = "기본 제공 프로필은 고칠 수 없습니다 — 「복사해서 고치기」를 누르세요.";

    private FormatterProfilesViewModel? _vm;

    public FormatterProfilesView()
    {
        InitializeComponent();
        InfoIcon.Content = ShellIcons.Create("info", 14);
        ChipStar.Content = ShellIcons.Create("star", 12);
        MenuCopy.Icon = ShellIcons.Create("copy");
        MenuRename.Icon = ShellIcons.Create("rename");
        MenuDelete.Icon = ShellIcons.Create("delete");
        MenuDefault.Icon = ShellIcons.Create("star");
        MenuExport.Icon = ShellIcons.Create("exportFile");

        NewButton.Click += (_, _) => Run(vm => vm.NewAsync());
        CopyButton.Click += (_, _) => Run(vm => vm.CopySelectedAsync());
        CopyToEditButton.Click += (_, _) => Run(vm => vm.CopySelectedAsync());
        MenuCopy.Click += (_, _) => Run(vm => vm.CopySelectedAsync());
        RenameButton.Click += (_, _) => BeginRename();
        MenuRename.Click += (_, _) => BeginRename();
        DeleteButton.Click += (_, _) => Run(vm => vm.DeleteAsync());
        MenuDelete.Click += (_, _) => Run(vm => vm.DeleteAsync());
        DefaultButton.Click += (_, _) => Run(vm => vm.SetDefaultAsync());
        MenuDefault.Click += (_, _) => Run(vm => vm.SetDefaultAsync());
        SaveButton.Click += (_, _) => Run(vm => vm.SaveAsync());
        RevertButton.Click += (_, _) => _vm?.Revert();
        ExportButton.Click += async (_, _) => await ExportAsync().ConfigureAwait(true);
        MenuExport.Click += async (_, _) => await ExportAsync().ConfigureAwait(true);
        ImportButton.Click += async (_, _) => await ImportAsync().ConfigureAwait(true);
        CopyPreviewButton.Click += async (_, _) => await CopyPreviewAsync().ConfigureAwait(true);

        ProfileList.ContainerPrepared += OnContainerPrepared;
        ProfileMenu.Opening += (_, _) => UpdateMenu();
        ProfileList.KeyDown += OnListKeyDown;
        NameBox.KeyDown += OnNameKeyDown;
        OptionsHost.AddHandler(PointerPressedEvent, OnOptionsPressed, RoutingStrategies.Tunnel);
        RightPanel.SizeChanged += (_, e) => FitOptionsHeight(e.NewSize.Height);
    }

    /// <summary>옵션 묶음 판(ViewModel 이 붙으면 생긴다).</summary>
    public OptionGroupsPanel? OptionsPanel => OptionsHost.Child as OptionGroupsPanel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as FormatterProfilesViewModel;
        OptionsHost.Child = _vm is null ? null : FormatterOptionGroups.Build(_vm.Rows);
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
        }

        UpdateReadOnly();
        UpdateMenu();
    }

    /// <summary>이름 칸에 포커스를 주고 전부 고른다(F2·「이름 바꾸기」).</summary>
    internal void BeginRename()
    {
        if (_vm is not { CanRename: true })
        {
            return;
        }

        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FormatterProfilesViewModel.IsEditable))
        {
            UpdateReadOnly();
        }

        if (e.PropertyName is nameof(FormatterProfilesViewModel.IsEditable) or nameof(FormatterProfilesViewModel.Selected) or nameof(FormatterProfilesViewModel.CanSetDefault))
        {
            UpdateMenu();
        }
        else if (e.PropertyName == nameof(FormatterProfilesViewModel.IsSampleEditing))
        {
            FitOptionsHeight(RightPanel.Bounds.Height);
        }
    }

    private void UpdateMenu()
    {
        MenuCopy.IsEnabled = _vm is { CanCopy: true };
        MenuRename.IsEnabled = _vm is { CanRename: true };
        MenuDelete.IsEnabled = _vm is { CanDelete: true };
        MenuDefault.IsEnabled = _vm is { CanSetDefault: true };
        MenuExport.IsEnabled = _vm is { CanExport: true };
    }

    private void UpdateReadOnly()
    {
        // 읽기 전용이면 Tab 으로 옵션 안에 들어가지 않는다(마우스는 OnOptionsPressed 가 막는다).
        KeyboardNavigation.SetTabNavigation(OptionsHost, _vm is { IsEditable: true } ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
    }

    private void OnOptionsPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is { IsBuiltInSelected: true })
        {
            e.Handled = true;
            _vm.StatusText = ReadOnlyHint;
        }
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        // 묶음 머리·안내 줄은 고를 수 없다(키보드 이동도 건너뛴다).
        var item = e.Index >= 0 && e.Index < ProfileList.ItemCount ? ProfileList.Items[e.Index] : null;
        var selectable = item is ProfileEntry;
        e.Container.IsEnabled = selectable;
        e.Container.Focusable = selectable;
        e.Container.MinHeight = selectable ? 26 : 0;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None)
        {
            BeginRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && _vm is { CanDelete: true })
        {
            Run(vm => vm.DeleteAsync());
            e.Handled = true;
        }
    }

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Run(vm => vm.SaveAsync());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm?.RevertName();
            ProfileList.Focus();
            e.Handled = true;
        }
    }

    /// <summary>창이 낮으면 옵션 칸을 줄여(스크롤) 미리보기가 최소 140 DIP 는 남게 한다.</summary>
    private void FitOptionsHeight(double height)
    {
        const double header = 38, caption = 30, sample = 116, preview = 140;
        var reserved = header + caption + preview + (_vm is { IsSampleEditing: true } ? sample : 0) + 8;
        OptionsScroll.MaxHeight = Math.Max(120, height - reserved);
    }

    private async Task CopyPreviewAsync()
    {
        if (_vm is { PreviewText.Length: > 0 } vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            try
            {
                await clipboard.SetTextAsync(vm.PreviewText).ConfigureAwait(true);
                vm.StatusText = "미리보기 결과를 클립보드에 복사했습니다.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                vm.StatusText = "클립보드를 쓸 수 없습니다: " + ex.Message;
            }
        }
    }

    private async Task ExportAsync()
    {
        if (DataContext is FormatterProfilesViewModel { Selected: { } sel } vm && TopLevel.GetTopLevel(this)?.StorageProvider is { } storage)
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "포매터 프로필 내보내기",
                SuggestedFileName = sel.Profile.Id + ".json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("포매터 프로필") { Patterns = ["*.json"] }],
            }).ConfigureAwait(true);
            if (file?.TryGetLocalPath() is { } path)
            {
                await vm.ExportAsync(path).ConfigureAwait(true);
            }
        }
    }

    private async Task ImportAsync()
    {
        if (DataContext is FormatterProfilesViewModel vm && TopLevel.GetTopLevel(this)?.StorageProvider is { } storage)
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "포매터 프로필 가져오기",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("포매터 프로필") { Patterns = ["*.json"] }],
            }).ConfigureAwait(true);
            if ((files.Count > 0 ? files[0] : null)?.TryGetLocalPath() is { } path)
            {
                await vm.ImportAsync(path).ConfigureAwait(true);
            }
        }
    }

    private void Run(Func<FormatterProfilesViewModel, Task> action)
    {
        if (_vm is { } vm)
        {
            _ = action(vm);
        }
    }
}
