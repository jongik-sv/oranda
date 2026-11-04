using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Oranda.App.Features.Ai.Chat;

/// <summary>복사할 때 앞 글 칸과 이 글 칸 사이에 넣는 구분.</summary>
internal enum ChatCopyBreak
{
    /// <summary>빈 줄 하나(문단·제목·코드·표 사이).</summary>
    Blank,

    /// <summary>줄바꿈 하나(목록 항목·표 행 사이).</summary>
    Line,

    /// <summary>탭 하나(표 칸 사이).</summary>
    Tab,
}

/// <summary>
/// 대화 창 수준의 글 선택: 블록마다 따로 있는 <see cref="SelectableTextBlock"/> 을 가로질러 드래그로 고른다.
/// 누를 때 화면 순서의 글 칸 목록과 칸 위치를 한 번 모으고, 움직일 때는 시작 칸·끝 칸·사이 칸의 선택 범위만 맞춘다(시각적 트리를 다시 훑지 않는다).
/// 글을 한 덩어리로 합치지 않으므로 표·코드 상자 모양은 그대로이고, 한 칸 안의 선택·두 번 누르기 단어 선택은 칸이 스스로 처리한다.
/// 복사 글은 칸이 가진 선택이 아니라 이 컨트롤러가 기록한 범위로 만든다(칸이 초점을 잃으면 자기 선택을 지우기 때문).
/// 선택 중 포인터가 대화 영역 위아래 끝에 가까우면 자동으로 스크롤한다.
/// </summary>
internal sealed class ChatTextSelection
{
    /// <summary>복사 글에서 이 칸 글 앞에 붙는 말(목록 표지). 선택이 칸 처음부터일 때만 붙는다.</summary>
    public static readonly AttachedProperty<string?> LeadProperty = AvaloniaProperty.RegisterAttached<ChatTextSelection, Control, string?>("Lead");

    /// <summary>복사 글에서 앞 칸과 이 칸 사이의 구분.</summary>
    public static readonly AttachedProperty<ChatCopyBreak> BreakProperty = AvaloniaProperty.RegisterAttached<ChatTextSelection, Control, ChatCopyBreak>("Break");

    /// <summary>
    /// <see cref="MarkdownView"/> 가 같은 뷰 안에서 글을 다시 그릴 때 올리는 이벤트(그 안의 선택은 의미를 잃는다).
    /// 대화 창의 스트리밍은 항목을 통째로 바꿔 새 뷰를 만들므로 이 이벤트가 닿지 않는다: 그 경우는 <see cref="DropIfDetached"/> 가 맡는다.
    /// </summary>
    public static readonly RoutedEvent<RoutedEventArgs> RebuiltEvent = RoutedEvent.Register<ChatTextSelection, RoutedEventArgs>("Rebuilt", RoutingStrategies.Bubble);

    public static string? GetLead(Control control) => control.GetValue(LeadProperty);

    public static void SetLead(Control control, string? value) => control.SetValue(LeadProperty, value);

    public static ChatCopyBreak GetBreak(Control control) => control.GetValue(BreakProperty);

    public static void SetBreak(Control control, ChatCopyBreak value) => control.SetValue(BreakProperty, value);

    /// <summary>포인터가 위아래 끝에서 이만큼 안쪽에 들어오면 자동 스크롤을 시작한다.</summary>
    private const double EdgeZone = 28;

    /// <summary>눌린 자리에서 이만큼(픽셀) 움직여야 끌기로 보고 자동 스크롤을 켠다. 그냥 클릭·두 번 누름은 화면을 밀지 않는다.</summary>
    private const double DragThreshold = 4;

    private readonly ScrollViewer _scroll;
    private readonly Control _content;
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };

    private List<Slot> _slots = [];
    private int _anchor = -1;
    private int _anchorChar;
    private int _focus = -1;
    private int _focusChar;
    private int _litLo = -1;
    private int _litHi = -1;
    private bool _dragging;
    private Point _pointer; // 대화 스크롤 뷰 기준 마지막 포인터 위치
    private Point _pressed; // 대화 스크롤 뷰 기준 누른 자리
    private IPointer? _device;
    private double _extentAtCollect;

    private sealed record Slot(SelectableTextBlock Block, Rect Bounds);

    /// <param name="scroll">대화 스크롤 뷰.</param>
    /// <param name="content">스크롤 뷰 안 내용(글 칸의 좌표 기준).</param>
    public ChatTextSelection(ScrollViewer scroll, Control content)
    {
        _scroll = scroll;
        _content = content;
        scroll.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        scroll.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        scroll.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        scroll.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        scroll.PointerCaptureLost += (_, _) => EndDrag(); // 끌던 중 창을 바꾸는 등으로 포인터를 잃으면 끝낸다
        scroll.AddHandler(InputElement.LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        scroll.AddHandler(SelectableTextBlock.CopyingToClipboardEvent, OnCopying, RoutingStrategies.Bubble);
        scroll.AddHandler(RebuiltEvent, OnRebuilt, RoutingStrategies.Bubble, handledEventsToo: true);
        _autoScroll.Tick += (_, _) => OnAutoScrollTick();
        scroll.ScrollChanged += (_, _) =>
        {
            if (_dragging)
            {
                Update(); // 끌던 중 휠로 스크롤해도 끝 칸이 포인터를 따라간다
            }
        };
    }

    /// <summary>드래그 중인가(대화 창이 맨 아래 따라가기를 잠시 멈추는 데 쓴다).</summary>
    public bool IsDragging => _dragging;

    /// <summary>여러 칸에 걸친 선택이 있는가.</summary>
    public bool HasSpanSelection => _anchor >= 0 && _focus >= 0 && _anchor != _focus && !string.IsNullOrEmpty(SelectedText());

    /// <summary>선택을 모두 지우고 드래그도 끝낸다.</summary>
    public void Reset()
    {
        EndDrag();
        ClearLit();
        _slots = [];
        _anchor = _focus = -1;
    }

    // ───────────────────────── 포인터 ─────────────────────────

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_scroll);
        if (e.Source is not Visual source || IsChrome(source))
        {
            return; // 버튼·스크롤바 위에서 시작한 누름은 선택을 시작하지 않는다
        }

        if (e.Pointer.Type != PointerType.Mouse)
        {
            Reset(); // 터치·펜은 끌어 고르지 않는다. 다른 곳을 누르면 여러 칸 선택은 지운다
            return;
        }

        var target = source.FindAncestorOfType<SelectableTextBlock>(includeSelf: true);
        var targetIndex = target is null ? -1 : IndexOf(target);
        var kind = point.Properties.PointerUpdateKind; // 왼쪽을 누른 채 오른쪽을 누르는 경우를 가른다(IsLeftButtonPressed 는 계속 참이다)
        if (kind != PointerUpdateKind.LeftButtonPressed)
        {
            if (kind != PointerUpdateKind.RightButtonPressed)
            {
                return;
            }

            // 오른쪽 누름: 선택 안에 든 칸이면 그대로 두고(오른쪽 메뉴 「복사」), 밖이면 여러 칸 선택을 지운다.
            if (_anchor >= 0 && _focus >= 0 && !InsideRange(targetIndex, e.GetPosition(_scroll)))
            {
                Reset();
            }

            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Shift+누름 = 칸 안의 선택 넓히기(칸이 처리). 여러 칸 선택은 의미를 잃으므로 눌린 칸만 빼고 지운다.
            EndDrag();
            ClearLit(except: target);
            _anchor = _focus = -1;
            return;
        }

        // 칸이 방금 놓은 자기 캐럿(누른 자리)을 읽어 두고, 이전 선택을 지운다(눌린 칸은 칸 자신의 처리를 건드리지 않는다).
        var caret = target?.SelectionStart ?? 0;
        EndDrag();
        ClearLit(except: target);
        _anchor = _focus = -1;
        Collect();
        foreach (var slot in _slots)
        {
            if (slot.Block != target && slot.Block.SelectionStart != slot.Block.SelectionEnd)
            {
                Set(slot.Block, 0, 0); // 칸이 따로 가진 한 칸 선택도 지운다
            }
        }

        if (_slots.Count == 0 || e.ClickCount != 1)
        {
            return; // 두 번·세 번 누름(단어·모두 선택)은 칸이 처리하고, 여러 칸 드래그는 시작하지 않는다
        }

        _pointer = _pressed = e.GetPosition(_scroll);
        _device = e.Pointer;
        var at = ContentPoint();
        targetIndex = target is null ? -1 : IndexOf(target);
        if (targetIndex >= 0)
        {
            _anchor = targetIndex;
            _anchorChar = caret;
        }
        else
        {
            _anchor = Nearest(at);
            _anchorChar = CharAt(_anchor, at);
        }

        _focus = _anchor;
        _focusChar = _anchorChar;
        _dragging = true;

        // 시작 칸이 초점을 갖게 한다: 글자 위를 누를 때 칸이 스스로 받는 것과 같다. 복사 키가 이 영역을 지나고(macOS 시스템 메뉴 경로의
        // EditKeyScope 포함), 초점이 다른 곳으로 가면 선택이 함께 풀린다. 포인터는 눌린 요소가 암묵적으로 잡으므로 칸 밖으로 나가도 끌기가 이어진다.
        if (_slots[_anchor].Block is { IsFocused: false } anchorBlock)
        {
            anchorBlock.Focus();
        }
    }

    /// <summary>오른쪽 누름이 선택 안인가. 칸 위면 그 칸이 범위에 있는지, 칸 사이 틈·목록 표지면 범위의 위아래 안인지 본다.</summary>
    private bool InsideRange(int targetIndex, Point pointer)
    {
        var lo = Math.Min(_anchor, _focus);
        var hi = Math.Max(_anchor, _focus);
        if (targetIndex >= 0)
        {
            return targetIndex >= lo && targetIndex <= hi;
        }

        var y = (pointer + _scroll.Offset - new Point(_content.Margin.Left, _content.Margin.Top)).Y;
        return lo < _slots.Count && hi < _slots.Count && y >= _slots[lo].Bounds.Top && y <= _slots[hi].Bounds.Bottom;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        if (!e.GetCurrentPoint(_scroll).Properties.IsLeftButtonPressed)
        {
            EndDrag();
            return;
        }

        _pointer = e.GetPosition(_scroll);
        if (!_autoScroll.IsEnabled && new Vector(_pointer.X - _pressed.X, _pointer.Y - _pressed.Y).Length > DragThreshold)
        {
            _autoScroll.Start(); // 끌기가 시작된 뒤에만 위아래 끝에서 자동 스크롤한다
        }

        Update();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && e.InitialPressMouseButton == MouseButton.Left)
        {
            EndDrag();
        }
        else if (e.InitialPressMouseButton == MouseButton.Right && _anchor >= 0 && _focus >= 0 && _anchor != _focus)
        {
            Apply(); // 칸이 오른쪽 놓음에서 자기 선택을 접었으면 기록한 범위로 되돌린다(메뉴 「복사」 가 켜져 있도록)
        }
    }

    private void EndDrag()
    {
        _dragging = false;
        _autoScroll.Stop();
    }

    /// <summary>눌러서 시작한 칸이 초점을 잃으면(입력칸·다른 창으로 이동) 여러 칸 선택도 함께 지운다. 오른쪽 메뉴가 열려 있으면 둔다.</summary>
    private void OnLostFocus(object? sender, FocusChangedEventArgs e)
    {
        var index = e.Source is SelectableTextBlock block ? IndexOf(block) : -1;
        if (_anchor < 0 || _focus < 0 || index != _anchor)
        {
            return;
        }

        if (e.NewFocusedElement is SelectableTextBlock next && _scroll.IsVisualAncestorOf(next))
        {
            return; // 대화 안의 다른 글 칸으로 옮겨 간 것: 새 누름이 선택을 정리한다
        }

        var anchorBlock = _slots[_anchor].Block;
        if ((anchorBlock.ContextFlyout?.IsOpen ?? false) || (anchorBlock.ContextMenu?.IsOpen ?? false))
        {
            return;
        }

        Reset();
    }

    private void OnRebuilt(object? sender, RoutedEventArgs e)
    {
        if (_slots.Count == 0 || (_anchor < 0 && !_dragging))
        {
            return;
        }

        // 다시 그려지는 글 안에 든 칸이 선택(끌던 중이면 시작 칸)에 있을 때만 지운다(스트리밍 중 다른 글의 선택은 둔다).
        var affected = _dragging ? _slots.Skip(_anchor).Take(1) : RangeSlots();
        if (e.Source is not Visual view || affected.Any(s => view.IsVisualAncestorOf(s.Block)))
        {
            Reset();
        }
    }

    /// <summary>
    /// 대화 항목이 지워지거나 바뀐 뒤 부른다. 스트리밍은 마지막 항목을 토큰마다 바꾸므로(Replace) 바뀌었다는 것만으로는 지우지 않는다.
    /// 끌지 않는 중에는 선택 범위의 칸이 화면에서 떨어졌을 때만 지우고, 끌던 중에는 시작 칸이 떨어졌을 때만 끝낸다
    /// (아니면 칸 목록을 다시 모아 끌기를 이어 간다).
    /// </summary>
    public void DropIfDetached()
    {
        if (_anchor < 0)
        {
            return;
        }

        if (!_dragging)
        {
            if (RangeSlots().Any(s => TopLevel.GetTopLevel(s.Block) is null))
            {
                Reset();
            }

            return;
        }

        if (_anchor >= _slots.Count || TopLevel.GetTopLevel(_slots[_anchor].Block) is null)
        {
            Reset();
        }
        else if (_slots.Any(s => TopLevel.GetTopLevel(s.Block) is null) && Recollect())
        {
            Update();
        }
    }

    private static bool IsChrome(Visual source)
    {
        for (Visual? v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is Button or Avalonia.Controls.Primitives.ToggleButton or Avalonia.Controls.Primitives.ScrollBar)
            {
                return true;
            }
        }

        return false;
    }

    // ───────────────────────── 범위 계산 ─────────────────────────

    /// <summary>눌린 순간의 화면 순서 글 칸과 칸 위치(대화 내용 기준)를 모은다. 숨은 가지는 건너뛴다.</summary>
    private void Collect()
    {
        var list = new List<Slot>();
        Walk(_content, list);
        _slots = list;
        _extentAtCollect = _scroll.Extent.Height;
    }

    private void Walk(Visual parent, List<Slot> into)
    {
        foreach (var child in parent.GetVisualChildren())
        {
            if (!child.IsVisible)
            {
                continue;
            }

            if (child is SelectableTextBlock block)
            {
                if (block.TranslatePoint(default, _content) is { } origin)
                {
                    into.Add(new Slot(block, new Rect(origin, block.Bounds.Size)));
                }

                continue;
            }

            Walk(child, into);
        }
    }

    /// <summary>마지막 포인터 자리를 대화 내용 기준으로 바꾼다. 내용은 스크롤 양만큼 위로 밀려 놓이므로 Bounds 가 아니라 여백으로 계산한다(방금 바꾼 스크롤이 아직 배치에 반영되지 않았어도 맞는다).</summary>
    private Point ContentPoint() => _pointer + _scroll.Offset - new Point(_content.Margin.Left, _content.Margin.Top);

    private int IndexOf(SelectableTextBlock block) => _slots.FindIndex(s => s.Block == block);

    /// <summary>포인터(대화 내용 기준)가 있는 칸. 칸 밖이면 세로로 가까운 칸, 같은 줄이면 가로로 가까운 칸.</summary>
    private int Nearest(Point p)
    {
        var best = -1;
        var bestDy = double.MaxValue;
        var bestDx = double.MaxValue;
        for (var i = 0; i < _slots.Count; i++)
        {
            var r = _slots[i].Bounds;
            var dy = Math.Max(Math.Max(r.Top - p.Y, p.Y - r.Bottom), 0);
            var dx = Math.Max(Math.Max(r.Left - p.X, p.X - r.Right), 0);
            if (dy < bestDy || (dy == bestDy && dx < bestDx))
            {
                (best, bestDy, bestDx) = (i, dy, dx);
                if (dy == 0 && dx == 0)
                {
                    break;
                }
            }
        }

        return best;
    }

    /// <summary>칸 안에서 포인터가 가리키는 글자 자리: 칸 위쪽이면 처음, 아래쪽이면 끝.</summary>
    private int CharAt(int index, Point p)
    {
        var slot = _slots[index];
        var length = TextOf(slot.Block).Length;
        if (p.Y < slot.Bounds.Top)
        {
            return 0;
        }

        if (p.Y > slot.Bounds.Bottom)
        {
            return length;
        }

        var local = _content.TranslatePoint(p, slot.Block) ?? default;
        var layout = slot.Block.TextLayout;
        var x = Math.Clamp(local.X - slot.Block.Padding.Left, 0, Math.Max(slot.Bounds.Width - slot.Block.Padding.Left - slot.Block.Padding.Right, 0)); // 글 폭이 아니라 칸 폭(오른쪽·가운데 맞춤 글은 글이 칸 안쪽에서 시작한다)
        var y = Math.Clamp(local.Y - slot.Block.Padding.Top, 0, Math.Max(layout.Height, 0));
        return Math.Clamp(layout.HitTestPoint(new Point(x, y)).TextPosition, 0, length);
    }

    private void Update()
    {
        if (_anchor < 0 || _slots.Count == 0)
        {
            return;
        }

        if (Math.Abs(_scroll.Extent.Height - _extentAtCollect) > 0.5 && !Recollect())
        {
            return; // 끌던 사이 대화가 늘거나 접혀 칸 위치가 낡았다: 칸을 다시 모은다
        }

        var at = ContentPoint();
        _focus = Nearest(at);
        if (_focus < 0)
        {
            return;
        }

        _focusChar = CharAt(_focus, at);
        Apply();
    }

    /// <summary>드래그 중 레이아웃이 바뀌었을 때 칸 목록과 위치를 다시 모으고 시작 칸의 자리를 새 목록에서 찾는다. 시작 칸이 없으면 선택을 끝내고 false.</summary>
    private bool Recollect()
    {
        var anchorBlock = _anchor >= 0 && _anchor < _slots.Count ? _slots[_anchor].Block : null;
        ClearLit(except: anchorBlock);
        Collect();
        var index = anchorBlock is null ? -1 : IndexOf(anchorBlock);
        if (index < 0)
        {
            Reset();
            return false;
        }

        _anchor = index;
        _focus = index;
        _litLo = _litHi = -1;
        return true;
    }

    /// <summary>기록한 범위를 칸의 선택으로 옮긴다. 이전에 건드린 칸과 지금 범위의 합집합만 훑는다.</summary>
    private void Apply()
    {
        var lo = Math.Min(_anchor, _focus);
        var hi = Math.Max(_anchor, _focus);
        var from = _litLo < 0 ? lo : Math.Min(_litLo, lo);
        var to = _litHi < 0 ? hi : Math.Max(_litHi, hi);
        for (var i = from; i <= to && i < _slots.Count; i++)
        {
            var (s, e) = RangeOf(i);
            Set(_slots[i].Block, s, e);
        }

        _litLo = lo;
        _litHi = hi;
    }

    /// <summary>i 번째 칸의 선택 범위(시작, 끝). 시작은 누른 쪽이라 끝보다 클 수 있다. 범위 밖이면 (0, 0).</summary>
    private (int Start, int End) RangeOf(int i)
    {
        var lo = Math.Min(_anchor, _focus);
        var hi = Math.Max(_anchor, _focus);
        if (i < lo || i > hi)
        {
            return (0, 0);
        }

        if (_anchor == _focus)
        {
            return (_anchorChar, _focusChar);
        }

        var length = TextOf(_slots[i].Block).Length;
        var forward = _anchor < _focus;
        if (i == _anchor)
        {
            return (_anchorChar, forward ? length : 0);
        }

        if (i == _focus)
        {
            return forward ? (0, _focusChar) : (length, _focusChar);
        }

        return (0, length);
    }

    private IEnumerable<Slot> RangeSlots()
    {
        if (_anchor < 0 || _focus < 0)
        {
            yield break;
        }

        for (var i = Math.Min(_anchor, _focus); i <= Math.Max(_anchor, _focus) && i < _slots.Count; i++)
        {
            yield return _slots[i];
        }
    }

    private void ClearLit(SelectableTextBlock? except = null)
    {
        if (_litLo >= 0)
        {
            for (var i = _litLo; i <= _litHi && i < _slots.Count; i++)
            {
                if (_slots[i].Block != except)
                {
                    Set(_slots[i].Block, 0, 0);
                }
            }
        }

        _litLo = _litHi = -1;
    }

    private static void Set(SelectableTextBlock block, int start, int end)
    {
        if (block.SelectionStart != start)
        {
            block.SelectionStart = start;
        }

        if (block.SelectionEnd != end)
        {
            block.SelectionEnd = end;
        }
    }

    private static string TextOf(SelectableTextBlock block) => block.Inlines is { Count: > 0 } inlines ? inlines.Text ?? string.Empty : block.Text ?? string.Empty;

    // ───────────────────────── 자동 스크롤 ─────────────────────────

    private void OnAutoScrollTick()
    {
        if (!_dragging || _device is { Captured: null })
        {
            EndDrag(); // 포인터를 잃은 채 끌기가 남지 않게 한다
            return;
        }

        if (_anchor < 0 || _anchor >= _slots.Count || TopLevel.GetTopLevel(_slots[_anchor].Block) is null)
        {
            Reset(); // 눌러서 시작한 칸이 화면에서 사라졌다
            return;
        }

        var height = _scroll.Bounds.Height;
        double delta = 0;
        if (_pointer.Y < EdgeZone)
        {
            delta = -Speed(EdgeZone - _pointer.Y);
        }
        else if (_pointer.Y > height - EdgeZone)
        {
            delta = Speed(_pointer.Y - (height - EdgeZone));
        }

        if (delta == 0)
        {
            return;
        }

        var max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        var next = Math.Clamp(_scroll.Offset.Y + delta, 0, max);
        if (next != _scroll.Offset.Y)
        {
            _scroll.Offset = new Vector(_scroll.Offset.X, next);
            Update();
        }
    }

    /// <summary>끝에서 깊이 들어갈수록(영역 밖이면 더) 빨라진다.</summary>
    private static double Speed(double depth) => Math.Clamp((depth / 2) + 2, 2, 40);

    // ───────────────────────── 복사 ─────────────────────────

    /// <summary>
    /// 기록한 범위의 글을 화면 순서로 잇는다. 문단 사이는 빈 줄, 목록 항목은 표지를 붙여 줄바꿈, 표는 칸 사이 탭·행 사이 줄바꿈,
    /// 코드는 줄바꿈을 그대로 둔다(코드 상자의 언어 이름·복사 단추 글은 글 칸이 아니라 들어오지 않는다).
    /// </summary>
    public string SelectedText()
    {
        if (_anchor < 0 || _focus < 0 || _anchor == _focus)
        {
            return string.Empty;
        }

        var lo = Math.Min(_anchor, _focus);
        var hi = Math.Max(_anchor, _focus);
        var sb = new StringBuilder();
        var first = true;
        for (var i = lo; i <= hi && i < _slots.Count; i++)
        {
            var block = _slots[i].Block;
            if (TopLevel.GetTopLevel(block) is null || !block.IsEffectivelyVisible)
            {
                continue; // 선택한 뒤 접히거나 떨어진 칸의 글은 복사하지 않는다
            }

            var text = TextOf(block);
            var (s, e) = RangeOf(i);
            var from = Math.Clamp(Math.Min(s, e), 0, text.Length);
            var to = Math.Clamp(Math.Max(s, e), 0, text.Length);
            var partial = i == lo || i == hi;
            if (partial && to <= from)
            {
                continue; // 끝 칸에서 한 글자도 안 고른 경우
            }

            var piece = text[from..to];
            if (from == 0 && GetLead(block) is { Length: > 0 } lead)
            {
                piece = lead + piece;
            }

            if (!first)
            {
                sb.Append(GetBreak(block) switch { ChatCopyBreak.Line => "\n", ChatCopyBreak.Tab => "\t", _ => "\n\n" });
            }

            sb.Append(piece);
            first = false;
        }

        return sb.ToString();
    }

    private async void CopyToClipboard(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(_scroll)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // 클립보드를 쓰지 못해도 앱 전역 미처리 예외로 번지지 않게 한다(async void).
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// 대화 영역에 초점이 있을 때의 복사 키는 여기서 끝낸다(터널 단계). 여러 칸 선택이 있으면 그것을, 아니면 초점 칸이 가진 선택 글만 복사하고,
    /// 고른 글이 없으면 클립보드를 건드리지 않는다. 키를 처리한 것으로 표시해 시스템 메뉴 경로(<c>NativeMenuCommand</c> → <c>TryRoute</c>)가
    /// 이어서 다른 복사(칸 전체·편집기)를 하지 못하게 한다.
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsCopyKey(e))
        {
            return;
        }

        e.Handled = true;
        var block = e.Source as SelectableTextBlock;
        var own = block is not null && block.SelectionStart != block.SelectionEnd ? block.SelectedText : string.Empty;
        var text = OwnSelectionWins(e.Source) ? own : SelectedText().Length > 0 ? SelectedText() : own;
        if (text.Length > 0)
        {
            CopyToClipboard(text);
        }
    }

    /// <summary>초점을 가진 글 칸이 자기 선택을 따로 갖고 있으면(Tab 으로 들어가 그 칸만 다시 고른 경우) 그 요소의 복사가 먼저다.</summary>
    private bool OwnSelectionWins(object? source)
    {
        switch (source)
        {
            case SelectableTextBlock block when block.SelectionStart != block.SelectionEnd:
                var index = IndexOf(block);
                return index < 0 || RangeOf(index) != (block.SelectionStart, block.SelectionEnd);
            default:
                return false;
        }
    }

    /// <summary>
    /// 글 칸 오른쪽 클릭 메뉴(모든 칸이 한 개를 나눠 쓴다): 「복사」 하나. Avalonia 테마의 영어 「Copy」 메뉴 대신 쓰며,
    /// 눌리면 메뉴가 열린 칸의 <see cref="SelectableTextBlock.Copy"/> 를 부른다(여러 칸 선택이면 <c>CopyingToClipboard</c> 이벤트로 이 컨트롤러가 전체를 복사한다).
    /// </summary>
    public static MenuFlyout CopyFlyout { get; } = CreateCopyFlyout();

    /// <summary>
    /// 대화의 글 칸 하나를 꾸민다: 선택 바탕을 편집기 선택색으로(코드 칸의 파란 글자가 파란 바탕에 묻히지 않게), 오른쪽 메뉴를 공유 「복사」 로.
    /// 테마(ControlTheme)가 두 값을 더 높은 우선순위로 정하므로 스타일이 아니라 로컬 값으로 둔다.
    /// </summary>
    public static void Prepare(SelectableTextBlock block)
    {
        block.Bind(SelectableTextBlock.SelectionBrushProperty, block.GetResourceObservable("OrandaEditorSelectionBrush"));
        block.ContextFlyout = CopyFlyout;
    }

    private static MenuFlyout CreateCopyFlyout()
    {
        var item = new MenuItem { Header = "복사", InputGesture = new KeyGesture(Key.C, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) };
        var flyout = new MenuFlyout { Items = { item } };
        item.Click += (_, _) => (flyout.Target as SelectableTextBlock)?.Copy();
        flyout.Opening += (_, _) => item.IsEnabled = (flyout.Target as SelectableTextBlock)?.CanCopy == true;
        return flyout;
    }

    /// <summary>플랫폼 설정의 복사 단축키(Windows Ctrl+C·Ctrl+Insert, macOS Cmd+C). macOS 는 OS 기본 수식키+C 도 받는다(실행 이력 목록과 같다).</summary>
    private bool IsCopyKey(KeyEventArgs e) =>
        _scroll.GetPlatformSettings()?.HotkeyConfiguration.Copy.Any(g => g.Matches(e)) == true
        || (e.Key == Key.C && e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control));

    /// <summary>오른쪽 클릭 메뉴 「복사」: 칸 하나의 복사 대신 여러 칸 선택 전체를 복사한다.</summary>
    private void OnCopying(object? sender, RoutedEventArgs e)
    {
        if (SelectedText() is not { Length: > 0 } text)
        {
            return;
        }

        e.Handled = true;
        CopyToClipboard(text);
    }
}
