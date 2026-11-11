using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Keymaps;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>
/// PL/SQL 편집 탭 화면. 논리는 <see cref="PlsqlEditorViewModel"/> 에 있다.
/// 패키지·타입은 쪽(명세·본문)마다 SQL 편집기 화면을 하나씩 두고 보이는 쪽만 보인다(쪽마다 스크롤·되돌리기·접기를 잃지 않게, PKG1).
/// </summary>
public partial class PlsqlEditorView : UserControl, Oranda.App.Shell.Docking.IFocusableDocumentView
{
    private PlsqlEditorViewModel? _vm;

    /// <summary>다른 쪽 편집기 화면(쌍이 생길 때 만든다). 없으면 null.</summary>
    private SqlEditorView? _second;

    public PlsqlEditorView()
    {
        InitializeComponent();
        // 단추도 명령(oranda.plsql.compile)과 같은 길: 오류가 있으면 컴파일 오류 창을 연다.
        CompileButton.Click += async (_, _) => await Run(() => CompileAndShowAsync(both: false));
        CompileSideButton.Click += async (_, _) => await Run(() => CompileAndShowAsync(both: false));
        CompileBothButton.Click += async (_, _) => await Run(() => CompileAndShowAsync(both: true));
        ForceButton.Click += async (_, _) => await Run(async () => { if (_vm is not null) { await _vm.CompileAsync(force: true); } });
        DiffButton.Click += async (_, _) => await Run(async () => { if (_vm is not null) { await _vm.ShowServerDiffAsync(); } });
        CloseDiffButton.Click += (_, _) => _vm?.CloseDiff();
        ReloadButton.Click += async (_, _) => await Run(async () => { if (_vm is not null) { await _vm.ReloadAsync(); } });
        CreateBodyButton.Click += (_, _) =>
        {
            if (_vm?.CreateBody() == true)
            {
                CurrentEditorView.FocusEditor();
            }
        };
        MembersList.Tapped += (_, e) => GoToMember(e.Source);
        MembersList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && MembersList.SelectedItem is PlsqlMemberEntry entry && _vm is not null)
            {
                _vm.GoToMember(entry);
                e.Handled = true;
            }
        };
        GotFocus += (_, _) => Activate();
        EditorView.GotFocus += (_, _) => Activate();
    }

    /// <summary>보이는 쪽의 편집기 화면.</summary>
    private SqlEditorView CurrentEditorView => _second is not null && _vm is not null && ReferenceEquals(_second.DataContext, _vm.Editor) ? _second : EditorView;

    /// <summary>문서로 포커스를 옮길 때(스키마 브라우저에서 두 번 누르기·Enter) 보이는 쪽의 글자 편집 영역에 준다.</summary>
    public bool FocusContent()
    {
        var view = CurrentEditorView;
        view.FocusEditor();
        return view.IsKeyboardFocusWithin;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
        }

        _vm = DataContext as PlsqlEditorViewModel;
        EditorView.DataContext = _vm?.PrimaryEditor;
        SyncEditors(refocus: false);
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            UpdateBanner();
        }

        UpdateDiff();
    }

    private async System.Threading.Tasks.Task CompileAndShowAsync(bool both)
    {
        if (_vm is null)
        {
            return;
        }

        if (both)
        {
            await _vm.CompileBothAsync();
        }
        else
        {
            await _vm.CompileAsync();
        }

        if (_vm.Errors.Count > 0)
        {
            await Oranda.App.Shell.ShellCommandRunner.RunAsync("oranda.view.compileErrors");
        }
    }

    private void GoToMember(object? source)
    {
        if (_vm is not null && (source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is PlsqlMemberEntry entry)
        {
            _vm.GoToMember(entry);
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
        else if (e.PropertyName is nameof(PlsqlEditorViewModel.DiffText) or nameof(PlsqlEditorViewModel.DiffRows))
        {
            UpdateDiff();
        }
        else if (e.PropertyName is nameof(PlsqlEditorViewModel.Editor) or nameof(PlsqlEditorViewModel.SecondaryEditor))
        {
            SyncEditors(refocus: IsKeyboardFocusWithin);
        }
    }

    /// <summary>
    /// 쪽마다의 편집기 화면을 맞춘다: 다른 쪽이 생기면 둘째 화면을 만들고, 보이는 쪽 편집기 화면만 보인다.
    /// <paramref name="refocus"/> 면(포커스가 이 탭 안에 있었으면) 새로 보이는 편집기로 포커스를 옮긴다.
    /// </summary>
    private void SyncEditors(bool refocus)
    {
        var secondary = _vm?.SecondaryEditor;
        if (secondary is not null && _second is null)
        {
            _second = new SqlEditorView { IsVisible = false };
            FocusSlot.SetName(_second, "plsql");
            _second.GotFocus += (_, _) => Activate();
            EditorStack.Children.Insert(1, _second); // 「본문 만들기」 안내 아래
        }

        if (_second is not null && !ReferenceEquals(_second.DataContext, secondary))
        {
            _second.DataContext = secondary;
        }

        var showSecond = _second is not null && secondary is not null && ReferenceEquals(_vm?.Editor, secondary);
        EditorView.IsVisible = !showSecond;
        if (_second is not null)
        {
            _second.IsVisible = showSecond;
        }

        if (refocus)
        {
            Dispatcher.UIThread.Post(() => CurrentEditorView.FocusEditor(), DispatcherPriority.Background);
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
            // 열 때 편집기 1 : 차이 1(차이는 다시 두 반쪽으로 나뉘어 넓게 준다). 닫으면 차이 칸을 0 으로(끌어 바꾼 너비는 다음에 열 때 처음 비율로 돌아간다).
            BodyGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            BodyGrid.ColumnDefinitions[3].Width = open ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
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
