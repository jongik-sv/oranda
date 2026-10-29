using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// 계획 트리 머리글 칸 오른쪽 경계의 끌기 손잡이. 끌면 그 칸의 폭(<see cref="PlanColumnWidths"/>)이 바뀌고,
/// 두 번 누르면 기본 폭으로 돌아간다. 놓을 때 한 번 저장한다. 폭은 <see cref="PlanViewModel.Columns"/> 에 있다(DataContext).
/// </summary>
public sealed class PlanColumnGrip : Control
{
    public static readonly StyledProperty<string?> KeyProperty =
        AvaloniaProperty.Register<PlanColumnGrip, string?>(nameof(Key));

    private double _startX;
    private double _startWidth;
    private bool _dragging;

    static PlanColumnGrip()
    {
        AffectsRender<PlanColumnGrip>(KeyProperty);
    }

    public PlanColumnGrip()
    {
        Width = 7;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
    }

    /// <summary>칸 키(<c>c-id</c> …).</summary>
    public string? Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    private PlanColumnWidths? Columns => (DataContext as PlanViewModel)?.Columns;

    /// <summary>보이지 않는 손잡이도 눌리게 투명 바탕을 깔고, 가운데에 옅은 세로선을 그린다.</summary>
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (this.TryFindResource("OrandaSeparatorBrush", ActualThemeVariant, out var res) && res is IBrush brush)
        {
            context.DrawLine(new Pen(brush, 1), new Point(Bounds.Width - 3.5, 3), new Point(Bounds.Width - 3.5, Bounds.Height - 3));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Key is not { } key || Columns is not { } columns || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            columns.Reset(key);
            columns.Save();
            return;
        }

        _startX = e.GetPosition(Parent as Visual).X;
        _startWidth = columns.Get(key);
        _dragging = true;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging && Key is { } key && Columns is { } columns)
        {
            columns.Set(key, _startWidth + (e.GetPosition(Parent as Visual).X - _startX));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            Columns?.Save();
        }
    }
}
