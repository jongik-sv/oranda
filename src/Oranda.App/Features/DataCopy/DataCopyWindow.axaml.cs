using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>
/// 데이터 복사 창(비모달, 도킹 안 함). 이름·파일 묻기, 규칙 편집 오른쪽 패널의 콤보 연결, 단추 연결만 맡고 판단은 뷰모델이 한다.
/// 창을 닫으면 실행을 멈추고 원본·대상 세션을 모두 닫는다.
/// </summary>
public partial class DataCopyWindow : Window
{
    private RuleEditorViewModel? _editor;
    private TableRuleViewModel? _rule;
    private bool _syncing;

    public DataCopyWindow()
    {
        InitializeComponent();
        NewJobButton.Click += OnNewJob;
        DuplicateJobButton.Click += OnDuplicateJob;
        RenameJobButton.Click += OnRenameJob;
        ExportJobButton.Click += OnExportJob;
        ImportJobButton.Click += OnImportJob;
        DeleteJobButton.Click += OnDeleteJob;
        JobList.SelectionChanged += OnJobSelected;
        AddTableButton.Click += OnAddTable;
        AddTableBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                OnAddTable(this, new RoutedEventArgs());
            }
        };
        RemoveRuleButton.Click += (_, _) =>
        {
            if (Vm is { Editor.SelectedRule: { } rule } vm)
            {
                vm.Editor.RemoveRule(rule);
            }
        };
        ToPrecheckButton.Click += async (_, _) => await RunUiAsync(ct => Vm!.GoToPrecheckAsync(ct)).ConfigureAwait(true);
        RecheckButton.Click += async (_, _) => await RunUiAsync(ct => Vm!.GoToPrecheckAsync(ct)).ConfigureAwait(true);
        CancelCheckButton.Click += (_, _) => Vm?.Precheck.Cancel();
        BackToRulesButton.Click += (_, _) => Vm?.BackToRuleEdit();
        BackFromRunButton.Click += (_, _) => Vm?.BackToRuleEdit();
        RunButton.Click += async (_, _) => await RunUiAsync(ct => Vm!.StartRunAsync(ct)).ConfigureAwait(true);
        OpenScriptButton.Click += async (_, _) => await Vm!.Precheck.OpenScriptInEditorAsync().ConfigureAwait(true);
        SaveScriptButton.Click += async (_, _) => await Vm!.Precheck.SaveScriptAsync().ConfigureAwait(true);
        PauseButton.Click += (_, _) => Vm?.Run.Pause();
        ResumeButton.Click += (_, _) => Vm?.Run.Resume();
        StopButton.Click += (_, _) => Vm?.Run.Stop();
        RetryButton.Click += async (_, _) => await RunUiAsync(ct => Vm!.RetryFailedRowsAsync(ct)).ConfigureAwait(true);
        CsvButton.Click += OnSaveCsv;
        TargetBox.ContainerPrepared += OnTargetContainerPrepared;
        WireRulePanel();
        DataContextChanged += (_, _) => Hook();
        Opened += async (_, _) => await RunUiAsync(ct => Vm?.InitializeAsync(ct) ?? Task.CompletedTask).ConfigureAwait(true);
        Closing += OnClosing;
        Closed += async (_, _) =>
        {
            await RunUiAsync(_ => Vm?.DisposeAsync().AsTask() ?? Task.CompletedTask).ConfigureAwait(true);
            // 창이 닫힌 뒤의 경고는 닫힌 창에 쓸 수 없으므로 셸의 상태 줄 알림으로 보낸다.
            if (Vm?.ClosingWarning is { } warning)
            {
                DataTransfer.DataTransferCommands.Notify?.Invoke(warning);
            }
        };
    }

    private bool _closeApproved;
    private bool _closeInProgress;

    private DataCopyViewModel? Vm => DataContext as DataCopyViewModel;

    // 복사가 도는 중에 창을 닫으려 하면 「중지하고 닫을까요?」를 묻는다. 승인하면 중지하고 실행기가 정리(끈 트리거·FK 다시 켜기)를 마칠 때까지
    // 창을 열어 둔 채 기다린 뒤 닫는다(그동안 같은 창이 이미 있으므로 새 창이 열리지 않고, 진행 상태는 이 창의 상태 줄에 보인다).
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved || Vm is not { } vm || !vm.Run.IsRunning)
        {
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        _ = StopAndCloseAsync(vm);
    }

    private async Task StopAndCloseAsync(DataCopyViewModel vm)
    {
        try
        {
            var ok = await Dialogs.ConfirmAsync("복사가 도는 중입니다. 중지하고 창을 닫을까요? 지금 배치는 되돌리고, 끈 트리거·FK 는 다시 켠 뒤 닫습니다.", CancellationToken.None).ConfigureAwait(true);
            if (!ok)
            {
                return;
            }

            vm.Run.Stop();
            await RunUiAsync(_ => vm.CloseSessionsAsync()).ConfigureAwait(true); // 정리가 끝날 때까지 기다린다(창은 열려 있다)
            _closeApproved = true;
            Close();
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    // 뷰모델이 정해지면 점검 단계의 편집기·저장 연결과 규칙 편집 패널 연결을 건다.
    private void Hook()
    {
        if (Vm is not { } vm)
        {
            return;
        }

        vm.Precheck.OpenInEditor = text =>
        {
            SqlEditorHost.Current.NewDocument(text);
            return Task.CompletedTask;
        };
        vm.Precheck.SaveScript = SaveScriptAsync;
        _editor = vm.Editor;
        _editor.PropertyChanged += OnEditorChanged;
        _editor.Rules.CollectionChanged += (_, _) => SyncRulePanel();
        SyncRulePanel();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RuleEditorViewModel.SelectedRule) or nameof(RuleEditorViewModel.Target))
        {
            SyncRulePanel();
        }
    }

    // 선택한 규칙의 종류·대상 처리 콤보 목록과 선택을 맞추고, 값이 바뀌면 규칙에 돌려준다.
    private void SyncRulePanel()
    {
        if (_rule is not null)
        {
            _rule.PropertyChanged -= OnRuleChanged;
        }

        _rule = _editor?.SelectedRule;
        if (_rule is null || _editor is null)
        {
            return;
        }

        _rule.PropertyChanged += OnRuleChanged;
        _syncing = true;
        try
        {
            KindBox.ItemsSource = _editor.RuleKinds;
            KindBox.SelectedItem = _rule.Kind;
            ActionBox.ItemsSource = _editor.AvailableActions;
            ActionBox.SelectedItem = _rule.Action;
            AddMaskBox.ItemsSource = _rule.MaskableColumns.ToList();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rule is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (e.PropertyName == nameof(TableRuleViewModel.Kind))
            {
                KindBox.SelectedItem = _rule.Kind;
            }
            else if (e.PropertyName == nameof(TableRuleViewModel.Action))
            {
                ActionBox.SelectedItem = _rule.Action;
            }
            else if (e.PropertyName == nameof(TableRuleViewModel.HasColumns))
            {
                AddMaskBox.ItemsSource = _rule.MaskableColumns.ToList(); // 열 정보를 나중에 읽어 와도 마스킹할 열 목록이 채워진다
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void WireRulePanel()
    {
        KindBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _rule is not null && KindBox.SelectedItem is RuleKind kind && kind != _rule.Kind)
            {
                _rule.ChangeKind(kind);
            }
        };
        ActionBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _rule is not null && ActionBox.SelectedItem is TargetAction action)
            {
                _rule.Action = action;
            }
        };
        AddMaskBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _rule is not null && AddMaskBox.SelectedItem is string column)
            {
                _rule.AddMask(column);
                _syncing = true;
                AddMaskBox.ItemsSource = _rule.MaskableColumns.ToList();
                AddMaskBox.SelectedItem = null;
                _syncing = false;
            }
        };
        LoadColumnsButton.Click += async (_, _) =>
        {
            if (Vm is { } vm && _rule is { } rule)
            {
                await RunUiAsync(ct => vm.LoadColumnsAsync(rule, ct)).ConfigureAwait(true);
            }
        };
        AddSecondColumnButton.Click += (_, _) => _rule?.AddSecondColumn();
        RemoveSecondColumnButton.Click += (_, _) => _rule?.RemoveSecondColumn();
    }

    // 운영 접속처럼 고를 수 없는 대상은 비활성으로 보이고 이유를 툴팁으로 알린다(설계 10장).
    private static void OnTargetContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ComboBoxItem item && item.DataContext is ConnectionOption option)
        {
            item.IsEnabled = option.IsEnabled;
            ToolTip.SetTip(item, option.Tooltip);
        }
    }

    private async Task RunUiAsync(Func<CancellationToken, Task> action)
    {
        try
        {
            await action(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (Vm is { } vm)
            {
                vm.StatusText = "작업을 마치지 못했습니다: " + e.Message;
                vm.StatusIsError = true;
            }
        }
    }

    private static IEditorDialogs Dialogs => SqlEditorHost.Current.Dialogs;

    private async void OnNewJob(object? sender, RoutedEventArgs e)
    {
        var name = await Dialogs.PromptAsync("새 작업 이름", "새 작업", CancellationToken.None).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || Vm is not { } vm)
        {
            return;
        }

        await RunUiAsync(async ct =>
        {
            if (await vm.Jobs.NewAsync(name.Trim(), ct).ConfigureAwait(true) is { } job)
            {
                await vm.OpenJobAsync(job, ct).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    private async void OnDuplicateJob(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Jobs.Selected: { } item } vm)
        {
            return;
        }

        var name = await Dialogs.PromptAsync("복제한 작업 이름", item.Name + " 복사본", CancellationToken.None).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await RunUiAsync(async ct =>
            {
                if (await vm.Jobs.DuplicateAsync(name.Trim(), ct).ConfigureAwait(true) is { } copy)
                {
                    await vm.OpenJobAsync(copy, ct).ConfigureAwait(true);
                }
            }).ConfigureAwait(true);
        }
    }

    private async void OnRenameJob(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Jobs.Selected: { } item } vm)
        {
            return;
        }

        var name = await Dialogs.PromptAsync("작업 이름 바꾸기", item.Name, CancellationToken.None).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await RunUiAsync(ct => vm.RenameSelectedAsync(name.Trim(), ct)).ConfigureAwait(true);
        }
    }

    private async void OnExportJob(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Jobs.Selected: { } item } vm)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "작업 파일로 내보내기",
            SuggestedFileName = item.Name + ".json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("복사 작업 파일") { Patterns = ["*.json"] }],
        }).ConfigureAwait(true);
        if (file?.TryGetLocalPath() is { } path)
        {
            await RunUiAsync(ct => vm.Jobs.ExportAsync(path, ct)).ConfigureAwait(true);
        }
    }

    private async void OnImportJob(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "작업 파일 가져오기",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("복사 작업 파일") { Patterns = ["*.json"] }],
        }).ConfigureAwait(true);
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await RunUiAsync(async ct =>
            {
                if (await vm.Jobs.ImportAsync(path, ct).ConfigureAwait(true) is { } job)
                {
                    await vm.OpenJobAsync(job, ct).ConfigureAwait(true);
                }
            }).ConfigureAwait(true);
        }
    }

    private async void OnDeleteJob(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Jobs.Selected: { } item } vm)
        {
            return;
        }

        if (await Dialogs.ConfirmAsync($"작업 「{item.Name}」 을(를) 삭제할까요? 되돌릴 수 없습니다.", CancellationToken.None).ConfigureAwait(true))
        {
            await RunUiAsync(ct => vm.DeleteSelectedAsync(ct)).ConfigureAwait(true);
        }
    }

    // 목록에서 작업을 고르면 연다. 실행 뒤 목록을 다시 읽을 때 같은 작업이 다시 선택돼도 화면을 되돌리지 않는다.
    private async void OnJobSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is { Jobs.Selected: { } item } vm && item.Job.Id != vm.CurrentJob?.Id)
        {
            await RunUiAsync(ct => vm.OpenJobAsync(item.Job, ct)).ConfigureAwait(true);
        }
    }

    private async void OnAddTable(object? sender, RoutedEventArgs e)
    {
        var name = AddTableBox.Text?.Trim();
        if (string.IsNullOrEmpty(name) || Vm is not { } vm)
        {
            return;
        }

        var owner = string.IsNullOrWhiteSpace(vm.Editor.SourceOwner) ? vm.Editor.Source?.Profile.User ?? string.Empty : vm.Editor.SourceOwner;
        AddTableBox.Text = string.Empty;
        await RunUiAsync(ct => vm.AddTableAsync(new DbObjectRef(owner.ToUpperInvariant(), name.ToUpperInvariant(), "TABLE"), null, ct)).ConfigureAwait(true);
    }

    private async void OnReenable(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && (sender as Control)?.DataContext is UnfinishedRunItem item)
        {
            await RunUiAsync(ct => vm.ReenableAsync(item, ct)).ConfigureAwait(true);
        }
    }

    // 규칙을 바꾸는 점검 선택지(열 건너뛰기·MERGE)는 누르는 즉시 적용하고 점검을 다시 한다.
    private async void OnChoiceClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && (sender as Control)?.DataContext is CheckChoiceViewModel { ChangesJob: true, Owner: { } owner } choice)
        {
            await RunUiAsync(ct => vm.ApplyChoiceAsync(owner, choice.Choice, ct)).ConfigureAwait(true);
        }
    }

    private async void OnSequenceLinkClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && (sender as Control)?.DataContext is SequenceLinkViewModel link)
        {
            await RunUiAsync(ct => vm.SetSequenceLinkAsync(link, link.IsLinked, ct)).ConfigureAwait(true);
        }
    }

    private async void OnSaveCsv(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        await RunUiAsync(ct => vm.Run.SaveCsvAsync(async () =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "실패 행을 CSV 로 저장",
                SuggestedFileName = "failed-rows.csv",
                DefaultExtension = "csv",
                FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
            }).ConfigureAwait(true);
            return file is null ? null : await file.OpenWriteAsync().ConfigureAwait(true);
        }, ct)).ConfigureAwait(true);
    }

    private async Task SaveScriptAsync(string text)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "SQL 스크립트로 저장",
            SuggestedFileName = "data-copy.sql",
            DefaultExtension = "sql",
            FileTypeChoices = [new FilePickerFileType("SQL") { Patterns = ["*.sql"] }],
        }).ConfigureAwait(true);
        if (file?.TryGetLocalPath() is { } path)
        {
            await File.WriteAllTextAsync(path, text).ConfigureAwait(true);
        }
    }
}
