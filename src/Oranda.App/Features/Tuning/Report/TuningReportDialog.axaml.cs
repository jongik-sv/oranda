using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.Tuning.Report;

/// <summary>
/// 「튜닝 보고서 만들기」 창(시안 화면 k). 기본 단추 = 취소(Enter·Esc), 만들기 = accent. 만드는 중에는 취소가 멈춤이다.
/// sql_id 칸을 떠나면 자식 커서 목록을 다시 읽는다. 찾아보기는 HTML 형식 저장 대화 상자(<see cref="SaveFileNames.CollapseDoubled"/>).
/// </summary>
public partial class TuningReportDialog : Window
{
    private string? _loadedSqlId;

    public TuningReportDialog()
    {
        InitializeComponent();
        CancelButton.Click += OnCancel;
        CreateButton.Click += async (_, _) =>
        {
            if (Vm is { } vm)
            {
                await vm.CreateAsync().ConfigureAwait(true);
            }
        };
        BrowseButton.Click += OnBrowse;
        SqlIdBox.LostFocus += (_, _) => ReloadChildren();
        Opened += (_, _) =>
        {
            CancelButton.Focus();
            ReloadChildren();
        };
        Closing += (_, _) => Vm?.Cancel();
        DataContextChanged += OnDataContext;
    }

    private TuningReportDialogViewModel? Vm => DataContext as TuningReportDialogViewModel;

    private void OnDataContext(object? sender, EventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        EnvChip.Bind(Border.BackgroundProperty, EnvChip.GetResourceObservable(vm.EnvironmentBrushKey));
        vm.Completed += (_, _) => Close(vm.ResultPath);
        vm.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TuningReportDialogViewModel.IsRunning) && Vm is { IsRunning: false })
        {
            CancelButton.Focus();
        }
    }

    private void ReloadChildren()
    {
        if (Vm is { } vm && vm.SqlId.Trim() != _loadedSqlId)
        {
            _loadedSqlId = vm.SqlId.Trim();
            _ = vm.LoadChildrenAsync();
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (Vm is { IsRunning: true } vm)
        {
            vm.Cancel();
            return;
        }

        Close(null);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var suggested = vm.SuggestedFileName;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Title,
            SuggestedFileName = suggested,
            DefaultExtension = "html",
            FileTypeChoices = [new FilePickerFileType("HTML") { Patterns = ["*.html", "*.htm"], MimeTypes = ["text/html"] }],
        }).ConfigureAwait(true);
        vm.SetPickedPath(SaveFileNames.CollapseDoubled(file?.TryGetLocalPath(), suggested, "html"));
    }
}
