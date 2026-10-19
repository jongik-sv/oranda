using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace EditorSpike.MultiCaret;

/// <summary>
/// AvaloniaEdit 를 포크하지 않고 공개 확장점만으로 만든 다중 커서 시제품.
/// - 보조 커서 = TextAnchor(문서 편집에 따라 자동 이동)
/// - 그리기 = IBackgroundRenderer(KnownLayer.Caret)
/// - 입력 = TextArea 의 TextInput/KeyDown 을 터널 단계에서 가로채 모든 커서에 같은 편집을 한 번의 RunUpdate(=undo 1단계)로 적용
/// 범위: 문자 입력, Backspace, Delete, Enter, Esc(해제), Ctrl+Alt+↑/↓(위·아래 줄에 커서 추가), Alt+클릭(커서 추가).
/// 미구현: 커서별 선택 영역, 커서별 클립보드, 좌우·단어 이동, IME 조합, 커서 깜빡임.
/// </summary>
public sealed class MultiCaretController : IBackgroundRenderer
{
    readonly TextEditor _editor;
    readonly TextArea _area;
    readonly List<TextAnchor> _extra = new();

    public MultiCaretController(TextEditor editor)
    {
        _editor = editor;
        _area = editor.TextArea;
        _area.TextView.BackgroundRenderers.Add(this);
        _area.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel, handledEventsToo: false);
        _area.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: false);
        _area.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public int Count => _extra.Count + 1;
    public IReadOnlyList<int> ExtraOffsets => _extra.Select(a => a.Offset).ToList();

    public void AddCaret(int offset)
    {
        if (offset == _area.Caret.Offset || _extra.Any(a => a.Offset == offset)) return;
        var a = _editor.Document.CreateAnchor(offset);
        a.MovementType = AnchorMovementType.AfterInsertion;
        _extra.Add(a);
        _area.TextView.InvalidateLayer(KnownLayer.Caret);
    }

    public void Clear() { _extra.Clear(); _area.TextView.InvalidateLayer(KnownLayer.Caret); }

    /// <summary>주 커서 기준으로 아래(+1)/위(-1) 줄 같은 열에 커서 추가.</summary>
    public void AddCaretVertical(int dir)
    {
        var doc = _editor.Document;
        var all = AllOffsets();
        int pivot = dir > 0 ? all.Max() : all.Min();
        var loc = doc.GetLocation(pivot);
        int target = loc.Line + dir;
        if (target < 1 || target > doc.LineCount) return;
        var line = doc.GetLineByNumber(target);
        AddCaret(line.Offset + Math.Min(loc.Column - 1, line.Length));
    }

    List<int> AllOffsets()
    {
        var l = new List<int>(_extra.Count + 1) { _area.Caret.Offset };
        foreach (var a in _extra) l.Add(a.Offset);
        return l;
    }

    /// <summary>모든 커서에 편집 적용. edit(offset) → (지울 시작, 지울 길이, 넣을 글자).</summary>
    void ApplyAll(Func<int, (int start, int len, string text)?> edit)
    {
        var doc = _editor.Document;
        var main = doc.CreateAnchor(_area.Caret.Offset);
        main.MovementType = AnchorMovementType.AfterInsertion;
        // 뒤쪽부터 적용하면 앞쪽 오프셋이 흔들리지 않는다(앵커라 순서와 무관하게도 맞지만 겹침 판단이 쉬움).
        var targets = AllOffsets().Distinct().OrderByDescending(o => o).ToList();
        doc.BeginUpdate();
        try
        {
            foreach (var off in targets)
            {
                var e = edit(off);
                if (e is { } x && (x.len > 0 || x.text.Length > 0)) doc.Replace(x.start, x.len, x.text);
            }
        }
        finally { doc.EndUpdate(); }
        _area.Caret.Offset = main.Offset;
        // 겹친 커서 정리
        var seen = new HashSet<int> { _area.Caret.Offset };
        _extra.RemoveAll(a => a.IsDeleted || !seen.Add(a.Offset));
        _area.Caret.BringCaretToView();
        _area.TextView.InvalidateLayer(KnownLayer.Caret);
    }

    public void TypeText(string text) => ApplyAll(off => (off, 0, text));

    void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_extra.Count == 0 || string.IsNullOrEmpty(e.Text) || _area.IsReadOnly) return;
        TypeText(e.Text);
        e.Handled = true;
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        if (e.Key is Key.Down or Key.Up && mods == (KeyModifiers.Control | KeyModifiers.Alt))
        {
            AddCaretVertical(e.Key == Key.Down ? 1 : -1); e.Handled = true; return;
        }
        if (_extra.Count == 0) return;
        var doc = _editor.Document;
        switch (e.Key)
        {
            case Key.Escape: Clear(); e.Handled = true; break;
            case Key.Back:
                ApplyAll(off => off > 0 && doc.GetLineByOffset(off).Offset < off ? (off - 1, 1, "") : null);
                e.Handled = true; break;
            case Key.Delete:
                ApplyAll(off => off < doc.TextLength && doc.GetLineByOffset(off).EndOffset > off ? (off, 1, "") : null);
                e.Handled = true; break;
            case Key.Enter:
                TypeText(_editor.Options.NewLineCharacter()); e.Handled = true; break;
        }
    }

    void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Alt) return;
        var tv = _area.TextView;
        var p = e.GetPosition(tv) + tv.ScrollOffset;
        var pos = tv.GetPosition(p);
        if (pos is null) return;
        AddCaret(_editor.Document.GetOffset(pos.Value.Location));
        e.Handled = true;
    }

    // ---- IBackgroundRenderer
    public KnownLayer Layer => KnownLayer.Caret;

    static readonly IBrush CaretBrush = Brushes.Black;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (_extra.Count == 0 || !textView.VisualLinesValid) return;
        var doc = _editor.Document;
        foreach (var a in _extra)
        {
            if (a.IsDeleted) continue;
            var loc = doc.GetLocation(a.Offset);
            var pos = new TextViewPosition(loc);
            var vl = textView.GetVisualLine(loc.Line);
            if (vl is null) continue; // 화면 밖
            var top = textView.GetVisualPosition(pos, VisualYPosition.LineTop) - textView.ScrollOffset;
            var bottom = textView.GetVisualPosition(pos, VisualYPosition.LineBottom) - textView.ScrollOffset;
            dc.FillRectangle(CaretBrush, new Rect(top.X, top.Y, 1.5, bottom.Y - top.Y));
        }
    }
}

static class OptionsExt
{
    public static string NewLineCharacter(this TextEditorOptions _) => "\n";
}
