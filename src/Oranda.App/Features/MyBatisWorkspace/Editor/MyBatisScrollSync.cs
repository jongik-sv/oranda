using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Oranda.App.Features.MyBatisWorkspace.Expanded;

namespace Oranda.App.Features.MyBatisWorkspace.Editor;

/// <summary>
/// MyBatis 쿼리 탭의 원문 편집기 ↔ 펼친 SQL 세로 스크롤 맞춤(분할 단추 줄의 「스크롤 맞춤」 토글). 두 칸의 위치는
/// 「맨 위 문서 줄 + 그 줄 안 비율」(0 기준 소수 줄)로 재고 <see cref="MyBatisLineMap"/> 으로 옮긴다. 원문은 접기를 쓰므로 줄 수 × 줄 높이로 계산하지 않는다.
/// 되먹임은 세 겹으로 막는다: (1) 마지막 사용자 입력(휠·누름·키·포커스)을 받은 칸만 상대를 움직인다, (2) 맞춘 직후 그 칸의 스크롤 알림이
/// 기대한 오프셋 0.5 DIP 안이면 무시한다, (3) 펼친 SQL 글이 바뀌면(<c>SqlCodeView</c> 가 맨 위로 보낸다) 레이아웃 뒤에 원문 기준으로 다시 맞추고
/// 그동안 펼친 쪽 알림은 전파하지 않는다.
/// </summary>
public sealed class MyBatisScrollSync
{
    private const double Tolerance = 0.5;

    // 칸의 입력으로 치는 이벤트: 휠·누름·키·포커스.
    private static readonly RoutedEvent[] InputEvents = [InputElement.PointerWheelChangedEvent, InputElement.PointerPressedEvent, InputElement.KeyDownEvent, InputElement.GotFocusEvent];

    private readonly Control _sourcePane;
    private readonly TextEditor _source;
    private readonly Control _expandedPane;
    private readonly TextEditor _expanded;
    private TextDocument? _sourceDocument;
    private TextDocument? _expandedDocument;
    private MyBatisLineMap? _map;
    private Pane _leader;
    private double? _expectSource;
    private double? _expectExpanded;
    private int _expandedPending; // 펼친 SQL 글이 바뀐 뒤 다시 맞추기를 기다리는 수(0 이 될 때까지 펼친 쪽 알림을 전파하지 않는다)
    private bool _attached;
    private bool _enabled = true;

    /// <param name="sourcePane">원문 쪽 입력을 받는 칸(찾기 막대·스크롤 막대 포함).</param>
    /// <param name="source">원문 편집기.</param>
    /// <param name="expandedPane">펼친 SQL 쪽 입력을 받는 칸.</param>
    /// <param name="expanded">펼친 SQL 편집기(읽기 전용).</param>
    public MyBatisScrollSync(Control sourcePane, TextEditor source, Control expandedPane, TextEditor expanded)
    {
        ArgumentNullException.ThrowIfNull(sourcePane);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expandedPane);
        ArgumentNullException.ThrowIfNull(expanded);
        _sourcePane = sourcePane;
        _source = source;
        _expandedPane = expandedPane;
        _expanded = expanded;
    }

    private enum Pane
    {
        None,
        Source,
        Expanded,
    }

    /// <summary>맞춤을 켠다(기본). 켜면 레이아웃 뒤에 펼친 SQL 을 원문 위치에 맞춘다(숨었다 보인 펼친 SQL 은 그때야 크기가 정해진다).</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            if (value && _attached)
            {
                RequestAlign();
            }
        }
    }

    /// <summary>지금 대응표(시험·계측용). 두 글 중 하나가 바뀌면 다음에 쓸 때 다시 만든다.</summary>
    internal MyBatisLineMap Map => _map ??= MyBatisLineMap.Build(_source.Document?.Text ?? string.Empty, _expanded.Document?.Text ?? string.Empty);

    /// <summary>원문 편집기의 맨 위 위치(0 기준 소수 줄, 시험·계측용).</summary>
    internal double SourceTopLine => TopLine(_source);

    /// <summary>펼친 SQL 의 맨 위 위치(0 기준 소수 줄, 시험·계측용).</summary>
    internal double ExpandedTopLine => TopLine(_expanded);

    /// <summary>구독을 건다(탭이 화면에 붙을 때).</summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        Listen(_sourcePane, OnSourceInput, add: true);
        Listen(_expandedPane, OnExpandedInput, add: true);
        _source.TextArea.TextView.ScrollOffsetChanged += OnSourceScrolled;
        _expanded.TextArea.TextView.ScrollOffsetChanged += OnExpandedScrolled;
        _source.DocumentChanged += OnDocumentChanged;
        _expanded.DocumentChanged += OnDocumentChanged;
        UseDocuments();
    }

    /// <summary>구독을 끊는다(탭이 화면에서 떨어지거나 다른 탭으로 바뀔 때).</summary>
    public void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        Listen(_sourcePane, OnSourceInput, add: false);
        Listen(_expandedPane, OnExpandedInput, add: false);
        _source.TextArea.TextView.ScrollOffsetChanged -= OnSourceScrolled;
        _expanded.TextArea.TextView.ScrollOffsetChanged -= OnExpandedScrolled;
        _source.DocumentChanged -= OnDocumentChanged;
        _expanded.DocumentChanged -= OnDocumentChanged;
        SetDocuments(null, null);
        _leader = Pane.None;
        _expectSource = _expectExpanded = null;
    }

    // 휠·누름·키·포커스를 터널·버블 모두, 처리된 것까지 받는다(스크롤 막대 끌기·찾기 막대 누름도 그 칸의 입력이다).
    private static void Listen(Control pane, EventHandler<RoutedEventArgs> handler, bool add)
    {
        foreach (var routed in InputEvents)
        {
            if (add)
            {
                pane.AddHandler(routed, handler, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            }
            else
            {
                pane.RemoveHandler(routed, handler);
            }
        }
    }

    private void OnSourceInput(object? sender, RoutedEventArgs e) => _leader = Pane.Source;

    private void OnExpandedInput(object? sender, RoutedEventArgs e) => _leader = Pane.Expanded;

    private void OnDocumentChanged(object? sender, EventArgs e) => UseDocuments();

    private void UseDocuments() => SetDocuments(_source.Document, _expanded.Document);

    private void SetDocuments(TextDocument? source, TextDocument? expanded)
    {
        if (_sourceDocument is not null)
        {
            _sourceDocument.TextChanged -= OnSourceTextChanged;
        }

        if (_expandedDocument is not null)
        {
            _expandedDocument.TextChanged -= OnExpandedTextChanged;
        }

        _sourceDocument = source;
        _expandedDocument = expanded;
        if (source is not null)
        {
            source.TextChanged += OnSourceTextChanged;
        }

        if (expanded is not null)
        {
            expanded.TextChanged += OnExpandedTextChanged;
        }

        _map = null;
    }

    private void OnSourceTextChanged(object? sender, EventArgs e) => _map = null;

    // 펼친 SQL 글이 바뀌면 SqlCodeView 가 맨 위로 보낸다. 원문 기준으로 다시 맞춘다.
    private void OnExpandedTextChanged(object? sender, EventArgs e)
    {
        _map = null;
        RequestAlign();
    }

    // 레이아웃이 끝난 뒤(Background) 펼친 SQL 을 원문 기준으로 맞추고, 그 맞춤이 낸 스크롤 알림까지 지나간 다음(한 번 더 Background)
    // 펼친 쪽 전파를 다시 연다. 그사이 펼친 쪽 알림(맨 위로 감·끝에서 잘림)이 원문을 끌어가지 않는다.
    private void RequestAlign()
    {
        _expandedPending++;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_attached && _enabled)
                {
                    AlignExpandedToSource();
                }

                Dispatcher.UIThread.Post(() => _expandedPending = Math.Max(0, _expandedPending - 1), DispatcherPriority.Background);
            },
            DispatcherPriority.Background);
    }

    private void OnSourceScrolled(object? sender, EventArgs e)
    {
        if (Settle(ref _expectSource, _source) || !_enabled || _leader != Pane.Source)
        {
            return;
        }

        AlignExpandedToSource();
    }

    private void OnExpandedScrolled(object? sender, EventArgs e)
    {
        if (Settle(ref _expectExpanded, _expanded) || !_enabled || _leader != Pane.Expanded || _expandedPending > 0)
        {
            return;
        }

        var target = OffsetOf(_source, Map.ToSource(TopLine(_expanded)));
        _expectSource = target;
        ScrollTo(_source, target);
    }

    /// <summary>펼친 SQL 을 원문 위치에 맞춘다(전파하지 않는다).</summary>
    private void AlignExpandedToSource()
    {
        var target = OffsetOf(_expanded, Map.ToExpanded(TopLine(_source)));
        _expectExpanded = target;
        ScrollTo(_expanded, target);
    }

    /// <summary>
    /// 편집기를 세로 오프셋 <paramref name="offset"/> 으로 옮긴다. AvaloniaEdit 12 의 <c>TextEditor.ScrollToVerticalOffset</c> 은 오프셋을 바꾸지 않아(헤드리스 실측)
    /// 편집기 템플릿 안 ScrollViewer(내용 = TextArea)의 Offset 을 직접 정한다.
    /// </summary>
    internal static void ScrollTo(TextEditor editor, double offset)
    {
        if (editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(v => v.Content is TextArea) is { } viewer)
        {
            viewer.Offset = new Vector(viewer.Offset.X, offset);
        }
    }

    // 이 칸을 우리가 맞춘 직후라 알림이 기대 오프셋 근처면 그 알림을 삼킨다(되먹임 차단). 기대값은 한 번 쓰면 지운다.
    private static bool Settle(ref double? expected, TextEditor editor)
    {
        if (expected is not { } value)
        {
            return false;
        }

        expected = null;
        return Math.Abs(editor.TextArea.TextView.ScrollOffset.Y - value) < Tolerance;
    }

    // 맨 위 위치 = 맨 위 문서 줄(0 기준) + 그 줄 안에서 내려간 비율. 접힌 줄처럼 높이가 0 이면 기본 줄 높이로 잰다.
    private static double TopLine(TextEditor editor)
    {
        var view = editor.TextArea.TextView;
        var y = view.ScrollOffset.Y;
        if (view.Document is null || view.GetDocumentLineByVisualTop(y) is not { } line)
        {
            return 0;
        }

        var top = view.GetVisualTopByDocumentLine(line.LineNumber);
        var height = LineHeight(view, line);
        return line.LineNumber - 1 + Math.Clamp((y - top) / height, 0, 1);
    }

    // 소수 줄 위치 → 그 편집기의 세로 오프셋.
    private static double OffsetOf(TextEditor editor, double position)
    {
        var view = editor.TextArea.TextView;
        if (view.Document is not { LineCount: > 0 } document || !double.IsFinite(position))
        {
            return 0;
        }

        var index = (int)Math.Floor(Math.Max(0, position));
        var fraction = position - index;
        if (index >= document.LineCount)
        {
            (index, fraction) = (document.LineCount - 1, 1);
        }

        // 접힌 줄은 높이가 0 이라 비율을 버린다: 접힌 구간을 지나는 동안 원문이 접힌 자리에 머문다(떨지 않는다).
        var line = document.GetLineByNumber(index + 1);
        return view.GetVisualTopByDocumentLine(line.LineNumber) + (fraction * VisualHeight(view, line));
    }

    // 맨 위 줄 안 비율을 잴 때의 줄 높이: 0 이면(접힘) 기본 줄 높이.
    private static double LineHeight(TextView view, DocumentLine line)
    {
        var height = VisualHeight(view, line);
        return height > Tolerance ? height : view.DefaultLineHeight;
    }

    // 다음 줄 맨 위까지의 실제 높이(접힌 줄 = 0). 마지막 줄은 기본 줄 높이.
    private static double VisualHeight(TextView view, DocumentLine line)
    {
        var top = view.GetVisualTopByDocumentLine(line.LineNumber);
        return line.NextLine is { } following ? view.GetVisualTopByDocumentLine(following.LineNumber) - top : view.DefaultLineHeight;
    }
}
