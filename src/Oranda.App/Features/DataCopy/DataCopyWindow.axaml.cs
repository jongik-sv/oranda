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

    // 대상 계정 칸에 사용자가 적는 중(규칙 값이 바뀌어도 칸 글자를 되쓰지 않는다).
    private bool _typingTarget;

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
        FindTablesButton.Click += async (_, _) => await RunUiAsync(OpenTableFinderAsync).ConfigureAwait(true);
        // Enter 는 자동 완성 칸보다 먼저 받는다(터널): 목록에서 고른 후보가 있으면 그 이름, 아니면 적은 글로 바로 더한다.
        AddTableBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                OnAddTable(this, new RoutedEventArgs());
            }
        }, RoutingStrategies.Tunnel);
        AddTableBox.AsyncPopulator = async (text, ct) =>
        {
            IReadOnlyList<string> found = Vm is { } vm ? await vm.TableCandidatesAsync(text, ct).ConfigureAwait(true) : [];
            return found;
        };
        AddTableBox.GotFocus += async (_, _) => await RunUiAsync(ct => Vm?.PrepareTableCandidatesAsync(ct) ?? Task.CompletedTask).ConfigureAwait(true);
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
        AddVariableButton.Click += (_, _) =>
        {
            if (Vm is { HasJob: true } vm)
            {
                vm.Editor.AddVariable();
                VariablesExpander.IsExpanded = true;
            }
        };
        SaveJobButton.Click += async (_, _) => await RunUiAsync(ct => Vm?.SaveCurrentJobAsync(quiet: false, ct) ?? Task.CompletedTask).ConfigureAwait(true);
        // Ctrl+S(맥은 Cmd+S): 규칙 편집 중인 작업 저장. 입력 칸이 키를 먼저 먹지 않게 터널로 받는다.
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.S && e.KeyModifiers is Avalonia.Input.KeyModifiers.Control or Avalonia.Input.KeyModifiers.Meta && Vm is { HasJob: true } vm)
            {
                e.Handled = true;
                await RunUiAsync(ct => vm.SaveCurrentJobAsync(quiet: false, ct)).ConfigureAwait(true);
            }
        }, RoutingStrategies.Tunnel);
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
    private bool _closeSaved;

    private async Task SaveAndCloseAsync(DataCopyViewModel vm)
    {
        try
        {
            await vm.SaveCurrentJobAsync(quiet: true, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("데이터 복사 작업을 닫으며 저장하지 못했다: {0}", ex.Message);
        }

        // 저장에 실패했으면(상태 줄에 이유) 창을 열어 둔다. 한 번 더 닫으면 저장 없이 닫는다.
        if (!vm.HasUnsavedChanges)
        {
            Close();
        }
    }

    private DataCopyViewModel? Vm => DataContext as DataCopyViewModel;

    // 복사가 도는 중에 창을 닫으려 하면 「중지하고 닫을까요?」를 묻는다. 승인하면 중지하고 실행기가 정리(끈 트리거·FK 다시 켜기)를 마칠 때까지
    // 창을 열어 둔 채 기다린 뒤 닫는다(그동안 같은 창이 이미 있으므로 새 창이 열리지 않고, 진행 상태는 이 창의 상태 줄에 보인다).
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved || Vm is not { } vm)
        {
            return;
        }

        if (!vm.Run.IsRunning)
        {
            // 닫기 전에 규칙 편집에서 한 일을 저장한다(저장이 끝난 뒤 다시 닫는다).
            if (!_closeSaved && vm.HasUnsavedChanges)
            {
                e.Cancel = true;
                _closeSaved = true;
                _ = SaveAndCloseAsync(vm);
            }

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
        vm.ConfirmRun = confirmation => Dialogs.ConfirmRunAsync(confirmation);
        _editor = vm.Editor;
        _editor.PropertyChanged += OnEditorChanged;
        _editor.Rules.CollectionChanged += (_, _) => SyncRulePanel(); // 변수 칸은 접힌 채로 둔다(머리줄에 변수 수가 보인다)
        SyncRulePanel();
        HookGrids(vm);
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 기본 원본 계정을 바꿔도 「원본과 같음」·「기본 대상」 이 달라지므로 대상 계정 목록을 다시 만든다.
        if (e.PropertyName is nameof(RuleEditorViewModel.SelectedRule) or nameof(RuleEditorViewModel.Target) or nameof(RuleEditorViewModel.TargetOwner)
            or nameof(RuleEditorViewModel.SourceOwner))
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
            RefreshTargetOwnerChoices();
        }
        finally
        {
            _syncing = false;
        }
    }

    // 대상 계정 고를 거리(원본과 같음·기본 대상·접속 계정)를 다시 만든다. 칸 글자는 규칙의 TargetOwner 그대로 둔다(목록을 바꿔도 값이 바뀌지 않게).
    private void RefreshTargetOwnerChoices()
    {
        if (_rule is null || _editor is null)
        {
            return;
        }

        var syncing = _syncing;
        _syncing = true;
        try
        {
            var owner = _rule.TargetOwner;
            TargetOwnerBox.ItemsSource = _editor.TargetOwnerOptions(_rule);
            TargetOwnerBox.SelectedItem = null;
            TargetOwnerBox.Text = owner;
        }
        finally
        {
            _syncing = syncing;
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
            else if (e.PropertyName == nameof(TableRuleViewModel.TargetOwner) && !_typingTarget
                     && !string.Equals(TargetOwnerBox.Text, _rule.TargetOwner, StringComparison.Ordinal))
            {
                TargetOwnerBox.Text = _rule.TargetOwner; // 기본 계정을 바꿔 따라 바뀐 값
            }
            else if (e.PropertyName is nameof(TableRuleViewModel.SourceOwner) or nameof(TableRuleViewModel.DefaultTarget))
            {
                RefreshTargetOwnerChoices(); // 「원본과 같음」·「기본 대상」 이 가리키는 계정이 바뀌었다
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
        // 대상 계정 콤보: 고르거나 적은 글자를 고른 규칙에 돌려준다(코드가 맞추는 동안은 건너뛴다). 적은 이름은 사전 이름으로 정리하고
        // (대문자, 큰따옴표로 감싼 이름만 그대로), 「기본 대상」 항목을 고르거나 비우면 다시 기본 계정을 따른다. 적는 동안에는 칸 글자를 되쓰지 않고 칸을 떠날 때 정리한 글자를 보인다.
        TargetOwnerBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty && !_syncing && _rule is not null && _editor is not null && TargetOwnerBox.Text is { } text)
            {
                _typingTarget = true;
                try
                {
                    RuleEditorViewModel.ChooseTargetOwner(_rule, text, TargetOwnerBox.SelectedItem as TargetOwnerOption);
                }
                finally
                {
                    _typingTarget = false;
                }
            }
        };
        TargetOwnerBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _rule is not null && _editor is not null && TargetOwnerBox.SelectedItem is TargetOwnerOption option)
            {
                RuleEditorViewModel.ChooseTargetOwner(_rule, option.Owner, option);
            }
        };
        TargetOwnerBox.LostFocus += (_, _) =>
        {
            if (_rule is not null && !string.Equals(TargetOwnerBox.Text, _rule.TargetOwner, StringComparison.Ordinal))
            {
                _syncing = true;
                TargetOwnerBox.Text = _rule.TargetOwner;
                _syncing = false;
            }
        };
        LoadColumnsButton.Click +=async (_, _) => await LoadSelectedColumnsAsync().ConfigureAwait(true);
        ReadColumnsToolButton.Click += async (_, _) => await LoadSelectedColumnsAsync().ConfigureAwait(true);
        MoveRuleUpButton.Click += (_, _) => MoveSelectedRule(-1);
        MoveRuleDownButton.Click += (_, _) => MoveSelectedRule(1);
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
            ToolTip.SetShowOnDisabled(item, true); // 꺼진 항목(운영 접속)에도 이유 툴팁이 뜨게
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

    // 이름·확인 대화상자는 복사 창을 소유자로 띄운다(닫힌 뒤 주 창이 복사 창을 덮지 않게).
    private DataCopyDialogs Dialogs => _dialogs ??= new DataCopyDialogs(this, () => SqlEditorHost.Current.Dialogs);

    private DataCopyDialogs? _dialogs;

    private async void OnNewJob(object? sender, RoutedEventArgs e)
    {
        var initial = Vm?.Jobs.SuggestName("새 작업") ?? "새 작업"; // 「새 작업」이 있으면 「새 작업 2」…
        var name = await Dialogs.PromptAsync("새 작업 이름", initial, CancellationToken.None).ConfigureAwait(true);
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

        var name = await Dialogs.PromptAsync("복제한 작업 이름", vm.Jobs.SuggestName(item.Name + " 복사본"), CancellationToken.None).ConfigureAwait(true);
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

    // 「테이블 찾기…」: 팝업에서 검색·체크한 표를 한 번에 규칙으로 더한다.
    private async Task OpenTableFinderAsync(CancellationToken ct)
    {
        if (Vm is not { } vm || await vm.CreateTableFinderAsync(ct).ConfigureAwait(true) is not { } finder)
        {
            return;
        }

        var ok = await new TableFinderWindow { DataContext = finder }.ShowDialog<bool>(this).ConfigureAwait(true);
        if (ok)
        {
            await vm.AddFoundTablesAsync(finder.PickedTables, ct).ConfigureAwait(true);
        }
    }

    private async void OnAddTable(object? sender, RoutedEventArgs e)
    {
        var name = (AddTableBox.IsDropDownOpen && AddTableBox.SelectedItem is string picked ? picked : AddTableBox.Text)?.Trim();
        if (string.IsNullOrEmpty(name) || Vm is not { } vm)
        {
            return;
        }

        // 표·계정.표·시노님: 원본 세션으로 실제 계정·표를 풀어 더한다(풀리지 않으면 이유를 상태 줄에 알린다).
        AddTableBox.IsDropDownOpen = false;
        AddTableBox.Text = string.Empty;
        await RunUiAsync(ct => vm.AddTableInputAsync(name, ct)).ConfigureAwait(true);
    }

    private void OnRemoveVariable(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && (sender as Control)?.DataContext is CopyVariableViewModel variable)
        {
            vm.Editor.RemoveVariable(variable);
        }
    }

    private void OnInsertVariableCondition(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && (sender as Control)?.DataContext is CopyVariableViewModel variable)
        {
            vm.InsertVariableCondition(variable);
        }
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
