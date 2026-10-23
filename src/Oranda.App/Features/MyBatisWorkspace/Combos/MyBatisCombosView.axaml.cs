using System.ComponentModel;
using Avalonia.Controls;
using Oranda.App.Features.MyBatisWorkspace.Editor.Highlighting;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

public partial class MyBatisCombosView : UserControl
{
    private readonly ComboErrorSpanTransformer _errorSpan = new();
    private MyBatisCombosViewModel? _vm;

    public MyBatisCombosView()
    {
        InitializeComponent();
        DetailSqlView.Loaded += (_, _) => AttachDetailColors();
        FixPreviewView.Loaded += (_, _) => AttachFixPreviewColors();
        DataContextChanged += (_, _) => Rebind();
    }

    // 고른 조합의 펼친 SQL: 코드 뷰가 건 SQL 강조 위에 오류 자리 변환기를 얹는다. 화면에서 빠지면 변환기를 뗀다.
    private void AttachDetailColors()
    {
        var view = DetailSqlView.TextEditorControl.TextArea.TextView;
        view.LineTransformers.Add(_errorSpan);
        void Remove(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        {
            DetailSqlView.Unloaded -= Remove;
            view.LineTransformers.Remove(_errorSpan);
        }

        DetailSqlView.Unloaded += Remove;
        ShowSelectedRow();
    }

    // 고친 원문은 MyBatis xml 이므로 코드 뷰가 건 SQL 강조를 떼고 MyBatis 강조를 건다. 화면에서 빠지면 정리한다.
    private void AttachFixPreviewColors()
    {
        var colors = FixPreviewView.UseMyBatisColors();
        void Remove(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        {
            FixPreviewView.Unloaded -= Remove;
            colors.Dispose();
        }

        FixPreviewView.Unloaded += Remove;
    }

    private void Rebind()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as MyBatisCombosViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
        }

        ShowSelectedRow();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MyBatisCombosViewModel.SelectedRow))
        {
            ShowSelectedRow();
        }
    }

    private void ShowSelectedRow()
    {
        var row = _vm?.SelectedRow;
        _errorSpan.Set(row?.SqlBefore.Length ?? 0, row?.SqlError.Length ?? 0);
        DetailSqlView.Text = row is null ? string.Empty : row.SqlBefore + row.SqlError + row.SqlAfter;
        DetailSqlView.TextEditorControl.TextArea.TextView.Redraw();
    }
}
