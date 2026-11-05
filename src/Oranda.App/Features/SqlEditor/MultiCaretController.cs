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
/// 다중 커서(T-D03, D-007, D-041). AvaloniaEdit 를 포크하지 않고 공개 확장점만 쓴다 — TSK-01-05 시제품 이식.
/// 추가 커서 = <see cref="TextAnchor"/> 쌍(커서·선택 앵커, 편집에 따라 자동 이동), 그리기 = <see cref="IBackgroundRenderer"/>(Caret·Selection 층),
/// 입력 = TextArea 의 TextInput·KeyDown·Pointer 를 터널 단계에서 가로채 모든 커서에 같은 편집을 한 번의 갱신(= 실행 취소 1단계)으로 적용.
/// 범위: 문자 입력, Backspace, Delete, Enter, Tab, 방향키·Home·End·단어 이동(Shift 동반 = 커서별 선택), Esc(해제),
/// Alt(macOS Option) 왼쪽 버튼 제자리 누름 = 커서 추가 / 끌기 = 열 블록 선택(<see cref="RectangleSelection"/> 을 직접 만든다), 일반 클릭 = 추가 커서 해제.
/// 추가 커서와 열 블록 선택은 동시에 두지 않는다(드래그를 시작하면 커서를 지우고, 커서를 더하면 사각 선택을 지운다).
/// </summary>
public sealed partial class MultiCaretController : IBackgroundRenderer
{
    private readonly TextEditor _editor;
    private readonly TextArea _area;
    private readonly List<Extra> _extra = [];
    private readonly SelectionLayer _selectionLayer;

    // 이 컨트롤러가 직접 옮기는 동안 0 보다 크다: 그 밖의 캐럿 이동(PageUp·찾기 이동·줄 이동 명령 등)은 추가 커서를 해제한다.
    private int _busy;

    public MultiCaretController(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        _area = editor.TextArea;
        _selectionLayer = new SelectionLayer(this);
        _area.TextView.BackgroundRenderers.Add(this);
        _area.TextView.BackgroundRenderers.Add(_selectionLayer);
        _area.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        _area.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _area.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _area.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        _area.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _area.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
        _area.Caret.PositionChanged += OnMainCaretMoved;
    }

    /// <summary>주 커서를 포함한 커서 개수.</summary>
    public int Count => _extra.Count(a => a.IsAlive) + 1;

    /// <summary>추가 커서가 하나라도 있는가.</summary>
    public bool HasExtra => _extra.Exists(a => a.IsAlive);

    /// <summary>선택을 가진 추가 커서가 있는가.</summary>
    public bool HasExtraSelection => _extra.Exists(a => a.HasSelection);

    /// <summary>추가 커서·선택이 바뀌었다(커서 수·선택 유무를 보여 주는 쪽이 갱신한다).</summary>
    public event EventHandler? StateChanged;

    public KnownLayer Layer => KnownLayer.Caret;

    public void Detach()
    {
        Clear();
        _area.Caret.PositionChanged -= OnMainCaretMoved;
        _area.TextView.BackgroundRenderers.Remove(this);
        _area.TextView.BackgroundRenderers.Remove(_selectionLayer);
        _area.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        _area.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _area.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _area.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
        _area.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        _area.RemoveHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost);
    }

    public void AddCaret(int offset)
    {
        offset = Math.Clamp(offset, 0, _editor.Document.TextLength);
        if (_area.Selection is RectangleSelection)
        {
            _area.ClearSelection(); // 커서와 열 블록은 동시에 두지 않는다.
        }

        if (offset == _area.Caret.Offset || _extra.Exists(a => a.IsAlive && a.Caret.Offset == offset))
        {
            return;
        }

        _extra.Add(NewExtra(offset, null));
        Changed();
    }

    public void Clear()
    {
        bool had = _extra.Count > 0;
        _extra.Clear();
        _goals.Clear();
        if (had)
        {
            Changed();
        }
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
        list.AddRange(_extra.Where(a => a.IsAlive).Select(a => a.Caret.Offset));
        return list;
    }

    /// <summary>모든 커서(주 커서 포함)의 선택 범위를 문서 순서로 돌려준다. 선택이 없는 커서는 길이 0.</summary>
    public IReadOnlyList<(int Start, int End, int Caret)> Ranges()
        => [.. Slots().Select(s => (s.Start, s.End, s.Caret))];

    /// <summary>
    /// 선택된 글의 조각들(문서 순서, 빈 조각 제외). 열 블록 선택이면 줄마다 한 조각, 아니면 선택을 가진 커서(주 커서 포함)마다 한 조각.
    /// </summary>
    public IReadOnlyList<Oranda.Core.Text.TextRange> SelectedRanges()
    {
        if (_area.Selection is RectangleSelection { IsEmpty: false } rs)
        {
            return [.. rs.Segments.Where(x => x.Length > 0).OrderBy(x => x.StartOffset).Select(x => new Oranda.Core.Text.TextRange(x.StartOffset, x.Length))];
        }

        return [.. Slots().Where(s => !s.IsEmpty).Select(s => new Oranda.Core.Text.TextRange(s.Start, s.Length))];
    }

    private void Changed()
    {
        _area.TextView.InvalidateLayer(KnownLayer.Caret);
        _area.TextView.InvalidateLayer(KnownLayer.Selection);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMainCaretMoved(object? sender, EventArgs e)
    {
        if (_busy == 0 && _extra.Count > 0)
        {
            Clear(); // 컨트롤러 밖에서 캐럿이 움직였다(PageUp·찾기·줄 이동·클릭 등): 추가 커서를 해제한다.
        }
    }

    private Extra NewExtra(int caret, int? anchor)
    {
        var doc = _editor.Document;
        var c = doc.CreateAnchor(caret);
        c.MovementType = AnchorMovementType.AfterInsertion;
        TextAnchor? a = null;
        if (anchor is { } x && x != caret)
        {
            a = doc.CreateAnchor(x);
        }

        return new Extra(c, a);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_extra.Count == 0 || !textView.VisualLinesValid)
        {
            return;
        }

        var doc = _editor.Document;
        var brush = _area.CaretBrush ?? _area.Foreground ?? Brushes.Gray;
        foreach (var a in _extra)
        {
            if (!a.IsAlive)
            {
                continue;
            }

            var loc = doc.GetLocation(a.Caret.Offset);
            if (textView.GetVisualLine(loc.Line) is null)
            {
                continue; // 화면 밖
            }

            var pos = new TextViewPosition(loc);
            var top = textView.GetVisualPosition(pos, VisualYPosition.LineTop) - textView.ScrollOffset;
            var bottom = textView.GetVisualPosition(pos, VisualYPosition.LineBottom) - textView.ScrollOffset;
            drawingContext.FillRectangle(brush, new Rect(top.X, top.Y, 1.5, bottom.Y - top.Y));
        }
    }

    /// <summary>추가 커서 하나: 커서 앵커와 (선택이 있으면) 선택 반대쪽 앵커.</summary>
    private sealed class Extra(TextAnchor caret, TextAnchor? anchor)
    {
        public TextAnchor Caret { get; } = caret;

        public TextAnchor? Anchor { get; } = anchor;

        public bool IsAlive => !Caret.IsDeleted && (Anchor is null || !Anchor.IsDeleted);

        public bool HasSelection => IsAlive && Anchor is { } a && a.Offset != Caret.Offset;
    }

    /// <summary>추가 커서의 선택을 <see cref="KnownLayer.Selection"/> 층에 칠한다(주 커서 선택은 AvaloniaEdit 가 칠한다).</summary>
    private sealed class SelectionLayer(MultiCaretController owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (owner._extra.Count == 0 || !textView.VisualLinesValid)
            {
                return;
            }

            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true };
            bool any = false;
            foreach (var x in owner._extra.Where(e => e.HasSelection))
            {
                int a = x.Anchor!.Offset, c = x.Caret.Offset;
                builder.AddSegment(textView, new TextSegment { StartOffset = Math.Min(a, c), EndOffset = Math.Max(a, c) });
                any = true;
            }

            if (any && builder.CreateGeometry() is { } geometry)
            {
                drawingContext.DrawGeometry(owner._area.SelectionBrush, owner._area.SelectionBorder, geometry);
            }
        }
    }
}
