using System;
using System.ComponentModel;
using Avalonia.Controls;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>PL/SQL 편집 탭 화면. 논리는 <see cref="PlsqlEditorViewModel"/> 에 있다.</summary>
public partial class PlsqlEditorView : UserControl, Oranda.App.Shell.Docking.IFocusableDocumentView
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

    /// <summary>문서로 포커스를 옮길 때(스키마 브라우저에서 두 번 누르기·Enter) 글자 편집 영역에 준다.</summary>
    public bool FocusContent()
    {
        EditorView.FocusEditor();
        return EditorView.IsKeyboardFocusWithin;
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

        UpdateDiff();
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
        else if (e.PropertyName is nameof(PlsqlEditorViewModel.DiffText) or nameof(PlsqlEditorViewModel.DiffRows))
        {
            UpdateDiff();
        }
    }

    /// <summary>차이 보기를 열고 닫는다. 나란히 보기 행이 있으면 그것을, 없으면(「차이 없음」 등) 글자를 보인다.</summary>
    private void UpdateDiff()
    {
        var open = !string.IsNullOrEmpty(_vm?.DiffText);
        var rows = _vm?.DiffRows ?? [];
        var wasOpen = DiffPanel.IsVisible;
        DiffPanel.IsVisible = open;
        DiffSplitter.IsVisible = open;
        DiffView.Rows = rows;
        DiffView.IsVisible = rows.Count > 0;
        DiffHeads.IsVisible = rows.Count > 0;
        DiffTextScroll.IsVisible = rows.Count == 0;
        if (open != wasOpen)
        {
            // 열 때 편집기 3 : 차이 2. 닫으면 차이 줄을 0 으로(끌어 바꾼 높이는 다음에 열 때 처음 비율로 돌아간다).
            BodyGrid.RowDefinitions[0].Height = open ? new GridLength(3, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
            BodyGrid.RowDefinitions[2].Height = open ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
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
