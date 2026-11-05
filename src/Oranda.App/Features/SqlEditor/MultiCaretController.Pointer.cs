using Avalonia;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Editing;

namespace Oranda.App.Features.SqlEditor;

// MultiCaretController 의 일부: 마우스. Alt(macOS Option) 왼쪽 버튼은 제자리에서 떼면 커서 추가, 끌면 열 블록 선택이다.
// AvaloniaEdit 내장 사각 선택은 수식키가 OS 마다 달라(macOS 는 Control) 쓰지 않고 RectangleSelection 을 직접 만든다.
public sealed partial class MultiCaretController
{
    /// <summary>이 거리(화면 점)를 넘게 움직여야 끌기로 본다.</summary>
    internal const double DragThreshold = 4;

    private enum PressState
    {
        None,
        Pending,
        Dragging,
    }

    private PressState _press;
    private Point _pressPoint;
    private TextViewPosition _pressPosition;

    private bool TryGetPosition(PointerEventArgs e, out Point point, out TextViewPosition position)
    {
        var tv = _area.TextView;
        var doc = _editor.Document;
        point = e.GetPosition(tv);
        var at = point + tv.ScrollOffset;
        // 편집기 위·아래로 끌어도 보이는 첫·끝 줄에 붙여 블록이 멈추지 않게 하고(스크롤된 문서에서도 끝 줄로 튀지 않는다), 짧은 줄 끝을 넘은 자리(가상 공간)도 열로 센다.
        var line = tv.GetVisualLineFromVisualTop(at.Y);
        if (line is null && tv.VisualLinesValid && tv.VisualLines.Count > 0)
        {
            line = point.Y < 0 ? tv.VisualLines[0] : tv.VisualLines[^1];
        }

        if (line is null)
        {
            position = default;
            return false;
        }

        double y = Math.Clamp(at.Y, line.VisualTop, line.VisualTop + Math.Max(line.Height - 1, 0));
        position = line.GetTextViewPosition(new Point(Math.Max(at.X, 0), y), allowVirtualSpace: true);
        return true;
    }

    /// <summary>가상 공간 위치를 문서 오프셋으로 바꾼다(줄 끝을 넘으면 줄 끝).</summary>
    private int OffsetOf(TextViewPosition position)
    {
        var doc = _editor.Document;
        var line = doc.GetLineByNumber(Math.Clamp(position.Line, 1, doc.LineCount));
        return line.Offset + Math.Clamp(position.Column - 1, 0, line.Length);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(_area).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return; // 오른쪽·가운데 버튼은 건드리지 않는다.
        }

        if (e.KeyModifiers != KeyModifiers.Alt)
        {
            Clear(); // 일반 클릭(Shift 등 동반 포함)은 추가 커서를 해제한다. 선택은 AvaloniaEdit 가 이어서 처리한다.
            return;
        }

        if (!TryGetPosition(e, out var point, out var position))
        {
            return;
        }

        _press = PressState.Pending;
        _pressPoint = point;
        _pressPosition = position;
        _area.Focus(); // 터널 단계에서 Handled 를 걸면 TextArea 의 포커스 처리가 건너뛰어진다
        e.Pointer.Capture(_area);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press == PressState.None)
        {
            return;
        }

        if (!e.GetCurrentPoint(_area).Properties.IsLeftButtonPressed)
        {
            _press = PressState.None; // 놓기·캡처 상실 이벤트를 놓친 경우: 버튼이 없는데 끌기가 이어지지 않게 한다
            return;
        }

        if (!TryGetPosition(e, out var point, out var position))
        {
            return;
        }

        if (_press == PressState.Pending)
        {
            double dx = point.X - _pressPoint.X, dy = point.Y - _pressPoint.Y;
            if (Math.Sqrt((dx * dx) + (dy * dy)) < DragThreshold)
            {
                e.Handled = true;
                return;
            }

            _press = PressState.Dragging;
            Clear(); // 커서와 열 블록은 동시에 두지 않는다.
        }

        SelectBlock(_pressPosition, position);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press == PressState.None || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        var state = _press;
        _press = PressState.None;
        e.Pointer.Capture(null);
        if (state == PressState.Pending)
        {
            AddCaret(OffsetOf(_pressPosition));
        }

        e.Handled = true;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _press = PressState.None;

    /// <summary>두 위치를 모서리로 하는 열 블록(사각) 선택을 만든다. 캐럿은 끝 위치로 옮긴다.</summary>
    internal void SelectBlock(TextViewPosition start, TextViewPosition end)
    {
        _busy++;
        try
        {
            using (_area.AllowCaretOutsideSelection())
            {
                _area.Caret.Position = end;
                _area.Selection = new RectangleSelection(_area, start, end);
            }
        }
        finally
        {
            _busy--;
        }
    }
}
