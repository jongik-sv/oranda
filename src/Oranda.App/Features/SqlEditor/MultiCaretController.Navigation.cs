using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit.Document;

namespace Oranda.App.Features.SqlEditor;

/// <summary>다중 커서가 처리하는 캐럿 이동 종류(AvaloniaEdit <c>CaretNavigationCommandHandler</c> 의 이동 계산이 internal 이라 직접 구현한다).</summary>
public enum CaretMove
{
    Left,
    Right,
    Up,
    Down,
    WordLeft,
    WordRight,
    LineStart,
    LineEnd,
    DocumentStart,
    DocumentEnd,
}

// MultiCaretController 의 일부: 방향키·Home·End·단어 이동. 추가 커서가 있을 때 모든 커서를 함께 옮기고, Shift 동반이면 커서마다 따로 선택한다.
public sealed partial class MultiCaretController
{
    /// <summary>
    /// 키를 이동 종류로 풀이한다. <paramref name="wordModifier"/> = 단어 단위 이동 수식키(Windows Control, macOS Option).
    /// macOS 에서는 Command+좌우 = 줄 처음·끝, Command+상하 = 문서 처음·끝. Shift 는 무시한다(선택 확장은 호출한 쪽이 따로 본다).
    /// </summary>
    internal static CaretMove? Interpret(Key key, KeyModifiers modifiers, KeyModifiers wordModifier, bool mac)
    {
        var m = modifiers & ~KeyModifiers.Shift;
        bool word = m != KeyModifiers.None && m == wordModifier;
        bool meta = mac && m == KeyModifiers.Meta;
        return key switch
        {
            Key.Left when m == KeyModifiers.None => CaretMove.Left,
            Key.Left when word => CaretMove.WordLeft,
            Key.Left when meta => CaretMove.LineStart,
            Key.Right when m == KeyModifiers.None => CaretMove.Right,
            Key.Right when word => CaretMove.WordRight,
            Key.Right when meta => CaretMove.LineEnd,
            Key.Up when m == KeyModifiers.None => CaretMove.Up,
            Key.Up when meta => CaretMove.DocumentStart,
            Key.Down when m == KeyModifiers.None => CaretMove.Down,
            Key.Down when meta => CaretMove.DocumentEnd,
            Key.Home when m == KeyModifiers.None => CaretMove.LineStart,
            Key.Home when m == KeyModifiers.Control => CaretMove.DocumentStart,
            Key.End when m == KeyModifiers.None => CaretMove.LineEnd,
            Key.End when m == KeyModifiers.Control => CaretMove.DocumentEnd,
            _ => null,
        };
    }

    private bool TryNavigate(KeyEventArgs e)
    {
        var word = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.WholeWordTextActionModifiers
            ?? (OperatingSystem.IsMacOS() ? KeyModifiers.Alt : KeyModifiers.Control);
        if (Interpret(e.Key, e.KeyModifiers, word, OperatingSystem.IsMacOS()) is not { } move)
        {
            return false;
        }

        MoveAll(move, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        return true;
    }

    /// <summary>모든 커서(주 커서 포함)를 <paramref name="move"/> 만큼 옮긴다. <paramref name="extend"/> 이면 커서마다 선택을 이어서 넓힌다.</summary>
    public void MoveAll(CaretMove move, bool extend)
    {
        var doc = _editor.Document;
        var moved = new List<CaretSlot>();
        var goals = new Dictionary<int, int>();
        bool vertical = move is CaretMove.Up or CaretMove.Down;
        foreach (var s in Slots())
        {
            int target = TargetOf(doc, s, move, extend, goals);
            int anchor = extend ? s.Anchor : target;
            moved.Add(new CaretSlot(Math.Min(anchor, target), Math.Max(anchor, target), target, s.IsMain));
        }

        _goals.Clear();
        if (vertical)
        {
            foreach (var (offset, column) in goals)
            {
                _goals[offset] = column;
            }
        }

        ApplySlots(MultiCaretMath.Merge(moved));
    }

    private int TargetOf(TextDocument doc, CaretSlot s, CaretMove move, bool extend, Dictionary<int, int> goals)
    {
        int caret = s.Caret;
        switch (move)
        {
            case CaretMove.Left:
                return !extend && !s.IsEmpty ? s.Start : Step(doc, caret, LogicalDirection.Backward, CaretPositioningMode.Normal);
            case CaretMove.Right:
                return !extend && !s.IsEmpty ? s.End : Step(doc, caret, LogicalDirection.Forward, CaretPositioningMode.Normal);
            case CaretMove.WordLeft:
                return Step(doc, caret, LogicalDirection.Backward, CaretPositioningMode.WordStartOrSymbol);
            case CaretMove.WordRight:
                return Step(doc, caret, LogicalDirection.Forward, CaretPositioningMode.WordStartOrSymbol);
            case CaretMove.Up:
            case CaretMove.Down:
                {
                    var line = doc.GetLineByOffset(caret);
                    int column = _goals.TryGetValue(caret, out int goal) ? goal : caret - line.Offset;
                    int number = line.LineNumber + (move == CaretMove.Up ? -1 : 1);
                    int result;
                    if (number < 1)
                    {
                        result = 0; // 첫 줄에서 위로 = 문서 처음
                    }
                    else if (number > doc.LineCount)
                    {
                        result = doc.TextLength; // 마지막 줄에서 아래로 = 문서 끝
                    }
                    else
                    {
                        var target = doc.GetLineByNumber(number);
                        result = target.Offset + Math.Min(column, target.Length);
                    }

                    goals[result] = column;
                    return result;
                }

            case CaretMove.LineStart:
                {
                    var line = doc.GetLineByOffset(caret);
                    int firstText = line.Offset;
                    while (firstText < line.EndOffset && doc.GetCharAt(firstText) is ' ' or '\t')
                    {
                        firstText++;
                    }

                    return caret == firstText ? line.Offset : firstText; // 공백 뒤 ↔ 줄 처음을 번갈아 간다(AvaloniaEdit 와 같다)
                }

            case CaretMove.LineEnd:
                return doc.GetLineByOffset(caret).EndOffset;
            case CaretMove.DocumentStart:
                return 0;
            default:
                return doc.TextLength;
        }
    }

    private static int Step(TextDocument doc, int offset, LogicalDirection direction, CaretPositioningMode mode)
    {
        if (mode == CaretPositioningMode.Normal)
        {
            return StepOneCharacter(doc, offset, direction);
        }

        int next = TextUtilities.GetNextCaretPosition(doc, offset, direction, mode);
        if (next < 0)
        {
            return offset;
        }

        // 단어 경계가 CR 과 LF 사이에 떨어지면 줄바꿈 바깥으로 옮긴다(캐럿은 줄바꿈 한가운데에 놓일 수 없다).
        if (next > 0 && next < doc.TextLength && doc.GetCharAt(next - 1) == '\r' && doc.GetCharAt(next) == '\n')
        {
            next += direction == LogicalDirection.Forward ? 1 : -1;
        }

        return next;
    }

    // 글자 하나 이동. CRLF 와 서로게이트 쌍은 하나의 경계로 건너뛴다(AvaloniaEdit 의 Normal 모드는 CR 과 LF 사이에서도 멈춘다).
    private static int StepOneCharacter(TextDocument doc, int offset, LogicalDirection direction)
    {
        int length = doc.TextLength;
        if (direction == LogicalDirection.Forward)
        {
            if (offset >= length)
            {
                return offset;
            }

            int next = offset + 1;
            if (next < length
                && ((doc.GetCharAt(offset) == '\r' && doc.GetCharAt(next) == '\n')
                    || (char.IsHighSurrogate(doc.GetCharAt(offset)) && char.IsLowSurrogate(doc.GetCharAt(next)))))
            {
                next++;
            }

            return next;
        }

        if (offset <= 0)
        {
            return offset;
        }

        int previous = offset - 1;
        if (previous > 0
            && ((doc.GetCharAt(previous) == '\n' && doc.GetCharAt(previous - 1) == '\r')
                || (char.IsLowSurrogate(doc.GetCharAt(previous)) && char.IsHighSurrogate(doc.GetCharAt(previous - 1)))))
        {
            previous--;
        }

        return previous;
    }
}
