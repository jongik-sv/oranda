using System;
using System.ComponentModel;
using Avalonia.Controls;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>PL/SQL 편집 탭 화면. 논리는 <see cref="PlsqlEditorViewModel"/> 에 있다.</summary>
public partial class PlsqlEditorView : UserControl
{
    private PlsqlEditorViewModel? _vm;

    public PlsqlEditorView()
    {
        InitializeComponent();
        // 단추도 명령(oranda.plsql.compile)과 같은 길: 오류가 있으면 컴파일 오류 창을 연다.
        CompileButton.Click += async (_, _) => await Run(async () =>
        {
            if (_vm is not null)
            {
                await _vm.CompileAsync();
                if (_vm.Errors.Count > 0)
                {
                    await Oranda.App.Shell.ShellCommandRunner.RunAsync("oranda.view.compileErrors");
                }
            }
        });
        ForceButton.Click += async (_, _) => await Run(async () => { if (_vm is not null) { await _vm.CompileAsync(force: true); } });
        DiffButton.Click += async (_, _) => await Run(async () => { if (_vm is not null) { await _vm.ShowServerDiffAsync(); } });
        CloseDiffButton.Click += (_, _) => _vm?.CloseDiff();
        ReloadButton.Click += async (_, _) =>
        {
            if (_vm is { Target: { } t, Session: { } s })
            {
                await Run(async () => { await _vm.OpenAsync(s, t); });
            }
        };
        SiblingButton.Click += async (_, _) => await Run(async () => { if (_vm is not null) { await _vm.OpenSiblingAsync(); } });
        GotFocus += (_, _) => Activate();
        EditorView.GotFocus += (_, _) => Activate();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
        }

        _vm = DataContext as PlsqlEditorViewModel;
        EditorView.DataContext = _vm?.Editor;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            UpdateBanner();
        }
    }

    private void Activate()
    {
        if (_vm is not null)
        {
            PlsqlEditorViewModel.Active = _vm;
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlsqlEditorViewModel.ServerSourceChanged) or nameof(PlsqlEditorViewModel.WarningText))
        {
            UpdateBanner();
        }
    }

    private void UpdateBanner()
    {
        if (_vm is null)
        {
            return;
        }

        BannerText.Text = _vm.ServerSourceChanged
            ? "편집하는 동안 다른 사람이 서버의 이 소스를 바꿨다. 「서버와 차이」로 확인한 뒤 덮어쓰거나 서버 소스를 다시 읽는다."
            : _vm.WarningText;
    }

    private static async System.Threading.Tasks.Task Run(Func<System.Threading.Tasks.Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
