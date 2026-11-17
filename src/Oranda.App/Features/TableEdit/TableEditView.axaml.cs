using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 객체 상세 편집 모드의 편집 표(열·제약·인덱스·설명 페이지). DataContext = <see cref="TableEditSession"/>.
/// 줄 안 칸에 포커스가 오면 그 줄을 고른다(도구줄 「－」「↺」「↑↓」가 지금 고치는 줄에 걸리게). 칸 글은 칸을 벗어날 때 모델에 들어가므로,
/// 닫기·끄기·실행 앞에서 <see cref="TableEditSession.FlushPendingInput"/> 로 포커스된 칸 글을 먼저 넣는다.
/// </summary>
public partial class TableEditView : UserControl
{
    private TableEditSession? _session;

    public TableEditView()
    {
        InitializeComponent();
        AddHandler(GotFocusEvent, OnCellFocused, RoutingStrategies.Bubble, handledEventsToo: true);
        DataContextChanged += (_, _) =>
        {
            if (_session is not null && _session.FlushPendingInput == FlushFocusedCell)
            {
                _session.FlushPendingInput = null;
            }

            _session = DataContext as TableEditSession;
            if (_session is not null)
            {
                _session.FlushPendingInput = FlushFocusedCell;
            }
        };
    }

    /// <summary>「신원 없애기」(속성을 못 읽은 신원 열, 설계 18.5).</summary>
    private void OnDropIdentityClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ColumnRowViewModel row })
        {
            row.DropIdentity();
        }
    }

    /// <summary>칸에 포커스가 오면 그 칸의 줄을 고른다.</summary>
    private void OnCellFocused(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } item
            || item.FindAncestorOfType<ListBox>() is not { } list || ReferenceEquals(list.SelectedItem, item.DataContext))
        {
            return;
        }

        list.SelectedItem = item.DataContext;
    }

    /// <summary>포커스된 칸(이 화면 안)의 글을 지금 모델에 넣는다(LostFocus 를 기다리지 않음).</summary>
    internal void FlushFocusedCell()
    {
        // 형식 칸(AutoCompleteBox) 목록이 열려 있으면 포커스는 팝업에 있다 — 열린 칸은 따로 넣는다.
        foreach (var open in this.GetVisualDescendants().OfType<AutoCompleteBox>().Where(a => a.IsDropDownOpen || a.IsKeyboardFocusWithin))
        {
            BindingOperations.GetBindingExpressionBase(open, AutoCompleteBox.TextProperty)?.UpdateSource();
        }

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Control focused || !focused.GetVisualAncestors().Contains(this))
        {
            return;
        }

        var box = focused as TextBox ?? focused.FindAncestorOfType<TextBox>();
        if (box is not null)
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
        }

        if ((focused as AutoCompleteBox ?? focused.FindAncestorOfType<AutoCompleteBox>()) is { } auto)
        {
            BindingOperations.GetBindingExpressionBase(auto, AutoCompleteBox.TextProperty)?.UpdateSource();
        }
    }
}
