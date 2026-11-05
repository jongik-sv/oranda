using Avalonia.Input;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Oranda.App.Features.SqlEditor;

// MultiCaretController 의 일부: 커서·선택 슬롯과 모든 커서에 같은 편집을 한 번에 적용하는 길.
public sealed partial class MultiCaretController
{
    /// <summary>한 커서의 편집: <paramref name="Start"/> 부터 <paramref name="Length"/> 글자를 <paramref name="Text"/> 로 바꾼다.</summary>
    private readonly record struct Edit(int Start, int Length, string Text);

    // 세로 이동의 목표 열(커서 위치 → 열). 세로 이동 직후의 위치에서만 유효하고, 그 밖의 이동·편집·해제로 지운다.
    private readonly Dictionary<int, int> _goals = [];

    /// <summary>주 커서의 슬롯. 사각 선택은 슬롯로 보지 않는다(커서만).</summary>
    private CaretSlot MainSlot()
    {
        var sel = _area.Selection;
        int caret = _area.Caret.Offset;
        if (sel.IsEmpty || sel is RectangleSelection)
        {
            return new CaretSlot(caret, caret, caret, true);
        }

        var seg = sel.SurroundingSegment;
        if (caret < seg.Offset || caret > seg.EndOffset)
        {
            return new CaretSlot(caret, caret, caret, true);
        }

        return new CaretSlot(seg.Offset, seg.EndOffset, caret, true);
    }

    /// <summary>주 커서와 살아 있는 추가 커서의 슬롯을 문서 순서로, 겹치는 것은 합쳐서 돌려준다.</summary>
    private List<CaretSlot> Slots()
    {
        var list = new List<CaretSlot>(_extra.Count + 1) { MainSlot() };
        foreach (var x in _extra.Where(e => e.IsAlive))
        {
            int c = x.Caret.Offset;
            int a = x.Anchor?.Offset ?? c;
            list.Add(new CaretSlot(Math.Min(a, c), Math.Max(a, c), c, false));
        }

        return MultiCaretMath.Merge(list);
    }

    /// <summary>슬롯을 컨트롤에 반영한다: 주 커서(선택 포함)를 옮기고 추가 커서를 다시 만든다.</summary>
    private void ApplySlots(IReadOnlyList<CaretSlot> slots)
    {
        _busy++;
        try
        {
            var main = slots.FirstOrDefault(s => s.IsMain);
            if (slots.Any(s => s.IsMain))
            {
                using (_area.AllowCaretOutsideSelection())
                {
                    _area.Caret.Offset = main.Caret;
                    if (main.IsEmpty)
                    {
                        _area.ClearSelection();
                    }
                    else
                    {
                        _area.Selection = Selection.Create(_area, main.Anchor, main.Caret);
                    }
                }
            }

            _extra.Clear();
            foreach (var s in slots.Where(s => !s.IsMain))
            {
                _extra.Add(NewExtra(s.Caret, s.IsEmpty ? null : s.Anchor));
            }

            _area.Caret.BringCaretToView();
        }
        finally
        {
            _busy--;
        }

        Changed();
    }

    /// <summary>
    /// 모든 커서에 편집을 적용한다(한 번의 문서 갱신 = 실행 취소 1단계). <paramref name="edit"/>: (커서 슬롯, 문서 순서 번호) → 편집(없으면 null).
    /// 끝나면 모든 커서는 선택 없이 편집된 글 뒤에 놓이고, 같은 자리의 커서는 하나로 합쳐진다.
    /// </summary>
    private void EditAll(Func<CaretSlot, int, Edit?> edit)
    {
        var doc = _editor.Document;
        var slots = Slots();
        var plans = new List<(CaretSlot Slot, Edit? Edit)>(slots.Count);
        for (int i = 0; i < slots.Count; i++)
        {
            plans.Add((slots[i], edit(slots[i], i)));
        }

        _goals.Clear();
        _busy++;
        try
        {
            _area.ClearSelection();
            doc.BeginUpdate();
            try
            {
                foreach (var e in plans.Where(p => p.Edit is { } x && (x.Length > 0 || x.Text.Length > 0)).Select(p => p.Edit!.Value).OrderByDescending(x => x.Start))
                {
                    doc.Replace(e.Start, e.Length, e.Text);
                }
            }
            finally
            {
                doc.EndUpdate();
            }

            var after = new List<CaretSlot>(plans.Count);
            int delta = 0;
            foreach (var (slot, e) in plans)
            {
                int pos;
                if (e is { } x)
                {
                    pos = x.Start + x.Text.Length + delta;
                    delta += x.Text.Length - x.Length;
                }
                else
                {
                    pos = slot.Caret + delta;
                }

                after.Add(new CaretSlot(pos, pos, pos, slot.IsMain));
            }

            ApplySlots(MultiCaretMath.Merge(after));
        }
        finally
        {
            _busy--;
        }
    }

    public void TypeText(string text)
    {
        text = MultiCaretMath.FilterTypedText(text);
        if (text.Length == 0)
        {
            return;
        }

        EditAll((s, _) => new Edit(s.Start, s.Length, text));
    }

    /// <summary>Backspace: 선택이 있으면 선택을, 없으면 줄 안의 앞 글자 하나를 지운다.</summary>
    public void DeleteBackward()
    {
        var doc = _editor.Document;
        EditAll((s, _) =>
        {
            if (!s.IsEmpty)
            {
                return new Edit(s.Start, s.Length, string.Empty);
            }

            if (s.Start == 0 || doc.GetLineByOffset(s.Start).Offset >= s.Start)
            {
                return null;
            }

            int previous = Step(doc, s.Start, LogicalDirection.Backward, CaretPositioningMode.Normal); // 서로게이트 쌍은 한꺼번에 지운다
            return new Edit(previous, s.Start - previous, string.Empty);
        });
    }

    /// <summary>Delete: 선택이 있으면 선택을, 없으면 줄 안의 뒤 글자 하나를 지운다.</summary>
    public void DeleteForward()
    {
        var doc = _editor.Document;
        EditAll((s, _) =>
        {
            if (!s.IsEmpty)
            {
                return new Edit(s.Start, s.Length, string.Empty);
            }

            if (s.Start >= doc.TextLength || doc.GetLineByOffset(s.Start).EndOffset <= s.Start)
            {
                return null;
            }

            int next = Step(doc, s.Start, LogicalDirection.Forward, CaretPositioningMode.Normal);
            return new Edit(s.Start, next - s.Start, string.Empty);
        });
    }

    public void InsertNewLine()
        => TypeText(_editor.Document.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");

    /// <summary>Tab: 모든 커서(선택은 대체)에 그 열에 맞는 들여쓰기 글자를 넣는다.</summary>
    public void InsertTab()
    {
        var doc = _editor.Document;
        EditAll((s, _) =>
        {
            int column = s.Start - doc.GetLineByOffset(s.Start).Offset + 1;
            return new Edit(s.Start, s.Length, _editor.Options.GetIndentationString(column));
        });
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (!HasExtra || string.IsNullOrEmpty(e.Text) || _area.IsReadOnly)
        {
            return;
        }

        TypeText(e.Text);
        e.Handled = true; // 제어 문자만 들어와 걸러졌어도 AvaloniaEdit 가 주 커서에만 넣지 않게 막는다.
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool rectangle = _area.Selection is RectangleSelection { IsEmpty: false };
        if (!HasExtra && !rectangle)
        {
            return;
        }

        if (!_area.StackedInputHandlers.IsEmpty)
        {
            return; // 자동 완성 등이 입력을 쥐고 있다.
        }

        var plain = e.KeyModifiers & ~KeyModifiers.Shift;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && HasExtra)
        {
            Clear();
            e.Handled = true;
            return;
        }

        if (HasExtra && TryNavigate(e))
        {
            e.Handled = true;
            return;
        }

        if (_area.IsReadOnly)
        {
            return;
        }

        if (e.Key == Key.Tab && plain == KeyModifiers.None)
        {
            if (e.KeyModifiers == KeyModifiers.Shift)
            {
                OutdentLines();
            }
            else if (HasExtra)
            {
                InsertTab();
            }
            else
            {
                InsertTabInRectangle();
            }

            e.Handled = true;
            return;
        }

        if (!HasExtra || plain != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Back:
                DeleteBackward();
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteForward();
                e.Handled = true;
                break;
            case Key.Enter:
                InsertNewLine();
                e.Handled = true;
                break;
            default:
                break;
        }
    }

    /// <summary>열 블록 선택에 Tab: 줄 전체가 아니라 블록의 각 줄 왼쪽 열 위치에 들여쓰기 글자를 넣는다.</summary>
    private void InsertTabInRectangle()
    {
        if (_area.Selection is not RectangleSelection rs)
        {
            return;
        }

        // ReplaceSelectionWithText 는 블록 글자를 지우고 바꾸므로 쓰지 않는다: 줄마다 블록 왼쪽 가장자리에 끼워 넣기만 한다.
        int left = LeftVisualColumn(rs);
        string indent = _editor.Options.GetIndentationString(left + 1);
        var doc = _editor.Document;
        _busy++;
        try
        {
            doc.BeginUpdate();
            try
            {
                foreach (var segment in rs.Segments.OrderByDescending(x => x.StartOffset))
                {
                    doc.Insert(segment.StartOffset, indent);
                }
            }
            finally
            {
                doc.EndUpdate();
            }
        }
        finally
        {
            _busy--;
        }
    }

    /// <summary>사각 선택의 왼쪽 가장자리 시각 열(0부터). 위치에 시각 열이 없으면(-1) 글자 열로 대신한다.</summary>
    private static int LeftVisualColumn(RectangleSelection rs)
    {
        int visual = Math.Min(rs.StartPosition.VisualColumn, rs.EndPosition.VisualColumn);
        return visual >= 0 ? visual : Math.Min(rs.StartPosition.Column, rs.EndPosition.Column) - 1;
    }

    /// <summary>Shift+Tab: 커서·선택·열 블록이 걸친 줄마다 앞의 들여쓰기를 한 단계 걷어낸다(탭 하나 또는 들여쓰기 칸 수만큼의 공백).</summary>
    private void OutdentLines()
    {
        var doc = _editor.Document;
        var lines = new SortedSet<int>();
        if (_area.Selection is RectangleSelection rs)
        {
            var seg = rs.SurroundingSegment;
            AddLines(lines, seg.Offset, seg.EndOffset);
        }
        else
        {
            foreach (var s in Slots())
            {
                AddLines(lines, s.Start, s.End);
            }
        }

        int size = Math.Max(1, _editor.Options.IndentationSize);
        _busy++;
        try
        {
            doc.BeginUpdate();
            try
            {
                foreach (int number in lines.Reverse())
                {
                    var line = doc.GetLineByNumber(number);
                    int remove = 0;
                    if (line.Length > 0 && doc.GetCharAt(line.Offset) == '\t')
                    {
                        remove = 1;
                    }
                    else
                    {
                        while (remove < size && remove < line.Length && doc.GetCharAt(line.Offset + remove) == ' ')
                        {
                            remove++;
                        }
                    }

                    if (remove > 0)
                    {
                        doc.Remove(line.Offset, remove);
                    }
                }
            }
            finally
            {
                doc.EndUpdate();
            }
        }
        finally
        {
            _busy--;
        }

        Changed();

        void AddLines(SortedSet<int> set, int start, int end)
        {
            for (int n = doc.GetLineByOffset(start).LineNumber; n <= doc.GetLineByOffset(end).LineNumber; n++)
            {
                set.Add(n);
            }
        }
    }
}
