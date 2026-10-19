using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// 다중 커서(T-D03, D-007). AvaloniaEdit 를 포크하지 않고 공개 확장점만 쓴다 — TSK-01-05 시제품 이식.
/// 보조 커서 = <see cref="TextAnchor"/>(편집에 따라 자동 이동), 그리기 = <see cref="IBackgroundRenderer"/>(Caret 층),
/// 입력 = TextArea 의 TextInput·KeyDown 을 터널 단계에서 가로채 모든 커서에 같은 편집을 한 번의 갱신(= 실행 취소 1단계)으로 적용.
/// 범위(1.0): 문자 입력, Backspace, Delete, Enter, Esc(해제), Ctrl+Alt+↑/↓(줄에 커서 추가), Alt+클릭(커서 추가).
/// 열 선택은 AvaloniaEdit 내장 Alt+드래그(사각 선택)를 쓴다.
/// </summary>
public sealed class MultiCaretController : IBackgroundRenderer
{
    private static readonly IBrush CaretBrush = Brushes.Gray;

    private readonly TextEditor _editor;
    private readonly TextArea _area;
    private readonly List<TextAnchor> _extra = [];

    public MultiCaretController(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        _area = editor.TextArea;
        _area.TextView.BackgroundRenderers.Add(this);
        _area.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        _area.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _area.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>주 커서를 포함한 커서 개수.</summary>
    public int Count => _extra.Count(a => !a.IsDeleted) + 1;

    public KnownLayer Layer => KnownLayer.Caret;

    public void Detach()
    {
        Clear();
        _area.TextView.BackgroundRenderers.Remove(this);
        _area.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        _area.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _area.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
    }

    public void AddCaret(int offset)
    {
        if (offset == _area.Caret.Offset || _extra.Exists(a => !a.IsDeleted && a.Offset == offset))
        {
            return;
        }

        var anchor = _editor.Document.CreateAnchor(offset);
        anchor.MovementType = AnchorMovementType.AfterInsertion;
        _extra.Add(anchor);
        _area.TextView.InvalidateLayer(KnownLayer.Caret);
    }

    public void Clear()
    {
        _extra.Clear();
        _area.TextView.InvalidateLayer(KnownLayer.Caret);
    }

    /// <summary>모든 커서 바로 위(-1)·아래(+1) 줄 같은 열에 커서를 더한다.</summary>
    public void AddCaretVertical(int direction)
    {
        var doc = _editor.Document;
        var all = AllOffsets();
        int pivot = direction > 0 ? all.Max() : all.Min();
        var loc = doc.GetLocation(pivot);
        int target = loc.Line + direction;
        if (target < 1 || target > doc.LineCount)
        {
            return;
        }

        var line = doc.GetLineByNumber(target);
        AddCaret(line.Offset + Math.Min(loc.Column - 1, line.Length));
    }

    public IReadOnlyList<int> AllOffsets()
    {
        var list = new List<int>(_extra.Count + 1) { _area.Caret.Offset };
        list.AddRange(_extra.Where(a => !a.IsDeleted).Select(a => a.Offset));
        return list;
    }

    /// <summary>모든 커서에 같은 편집을 적용한다. <paramref name="edit"/>: 커서 위치 → (지울 시작, 지울 길이, 넣을 글).</summary>
    private void ApplyAll(Func<int, (int Start, int Length, string Text)?> edit)
    {
        var doc = _editor.Document;
        var main = doc.CreateAnchor(_area.Caret.Offset);
        main.MovementType = AnchorMovementType.AfterInsertion;
        var targets = AllOffsets().Distinct().OrderByDescending(o => o).ToList();
        doc.BeginUpdate();
        try
        {
            foreach (int off in targets)
            {
                if (edit(off) is { } e && (e.Length > 0 || e.Text.Length > 0))
                {
                    doc.Replace(e.Start, e.Length, e.Text);
                }
            }
        }
        finally
        {
            doc.EndUpdate();
        }

        _area.Caret.Offset = main.Offset;
        var seen = new HashSet<int> { _area.Caret.Offset };
        _extra.RemoveAll(a => a.IsDeleted || !seen.Add(a.Offset));
        _area.Caret.BringCaretToView();
        _area.TextView.InvalidateLayer(KnownLayer.Caret);
    }

    public void TypeText(string text) => ApplyAll(off => (off, 0, text));

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_extra.Count == 0 || string.IsNullOrEmpty(e.Text) || _area.IsReadOnly)
        {
            return;
        }

        TypeText(e.Text);
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt))
        {
            AddCaretVertical(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (_extra.Count == 0 || _area.IsReadOnly && e.Key != Key.Escape)
        {
            return;
        }

        var doc = _editor.Document;
        switch (e.Key)
        {
            case Key.Escape:
                Clear();
                e.Handled = true;
                break;
            case Key.Back:
                ApplyAll(off => off > 0 && doc.GetLineByOffset(off).Offset < off ? (off - 1, 1, string.Empty) : null);
                e.Handled = true;
                break;
            case Key.Delete:
                ApplyAll(off => off < doc.TextLength && doc.GetLineByOffset(off).EndOffset > off ? (off, 1, string.Empty) : null);
                e.Handled = true;
                break;
            case Key.Enter:
                TypeText(doc.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
                e.Handled = true;
                break;
            default:
                break;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Alt)
        {
            return;
        }

        var tv = _area.TextView;
        var point = e.GetPosition(tv) + tv.ScrollOffset;
        var pos = tv.GetPosition(point);
        if (pos is null)
        {
            return;
        }

        AddCaret(_editor.Document.GetOffset(pos.Value.Location));
        e.Handled = true;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_extra.Count == 0 || !textView.VisualLinesValid)
        {
            return;
        }

        var doc = _editor.Document;
        foreach (var a in _extra)
        {
            if (a.IsDeleted)
            {
                continue;
            }

            var loc = doc.GetLocation(a.Offset);
            if (textView.GetVisualLine(loc.Line) is null)
            {
                continue; // 화면 밖
            }

            var pos = new TextViewPosition(loc);
            var top = textView.GetVisualPosition(pos, VisualYPosition.LineTop) - textView.ScrollOffset;
            var bottom = textView.GetVisualPosition(pos, VisualYPosition.LineBottom) - textView.ScrollOffset;
            drawingContext.FillRectangle(CaretBrush, new Rect(top.X, top.Y, 1.5, bottom.Y - top.Y));
        }
    }
}
