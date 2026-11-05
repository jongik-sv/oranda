using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// 입력기가 조합 중인 글자(preedit)를 편집기에 보여 준다. macOS 입력기는 조합 중인 글자를 앱에 보내지 않고 입력기 클라이언트의 SetPreeditText 로만 알려 주므로,
/// AvaloniaEdit 기본 클라이언트(SupportsPreedit=false)로는 확정 전까지 아무것도 보이지 않는다.
/// 조합 글자는 문서에 넣지 않는다. 캐럿 자리(추가 커서·열 블록은 줄마다)에 문서 길이 0 인 시각 요소를 끼워 넣어 편집기 글꼴과 밑줄로 그린다.
/// 뒤 글자는 오른쪽으로 밀려 겹치지 않고, 스크롤·접기·긴 줄은 AvaloniaEdit 가 평소처럼 처리한다. 문서·실행 취소·수정 표시·파서·자동 완성에는 영향이 없다.
/// </summary>
internal sealed class PreeditPresenter
{
    private readonly TextArea _area;
    private readonly Func<IReadOnlyList<int>> _offsets;
    private readonly PreeditGenerator _generator;
    private List<TextAnchor> _anchors = [];
    private bool _installed;

    public PreeditPresenter(TextArea area, Func<IReadOnlyList<int>> offsets)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(offsets);
        _area = area;
        _offsets = offsets;
        _generator = new PreeditGenerator(this);
    }

    /// <summary>지금 보여 주는 조합 글자. 없으면 빈 문자열.</summary>
    public string Text { get; private set; } = string.Empty;

    public bool IsActive => Text.Length > 0 && _anchors.Count > 0;

    /// <summary>조합 글자를 그리는 문서 오프셋(문서 순서, 중복 없음).</summary>
    public IReadOnlyList<int> Offsets
        => [.. _anchors.Where(a => !a.IsDeleted).Select(a => a.Offset).Distinct().Order()];

    /// <summary>조합 글자를 바꾼다. 비었거나 편집기가 읽기 전용이면 지운다.</summary>
    public void Set(string? text)
    {
        if (string.IsNullOrEmpty(text) || _area.IsReadOnly || _area.Document is not { } doc)
        {
            Clear();
            return;
        }

        EnsureFirstGenerator();
        Text = text;
        _anchors = [.. _offsets().Distinct().Select(o => doc.CreateAnchor(Math.Clamp(o, 0, doc.TextLength)))];
        _area.TextView.Redraw();
    }

    /// <summary>조합 글자를 지운다. 보이는 것이 없으면 아무것도 하지 않는다(키마다 불린다).</summary>
    public void Clear()
    {
        if (Text.Length == 0 && _anchors.Count == 0)
        {
            return;
        }

        Text = string.Empty;
        _anchors = [];
        _area.TextView.Redraw();
    }

    /// <summary>시각 요소 생성기와 조합이 끝나는 신호(확정·Esc·포커스 이탈·포인터·캐럿 이동·문서 교체)를 편집기에 단다.</summary>
    public void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        EnsureFirstGenerator();
        // 확정 글자·Esc 는 다른 처리기가 Handled 로 만들어도 지워야 하고, 문서에 글자가 들어가기 전(터널)에 지워야 이중으로 보이지 않는다.
        _area.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        _area.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _area.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _area.LostFocus += OnLostFocus;
        _area.DetachedFromVisualTree += OnDetached;
        _area.Caret.PositionChanged += OnCaretMoved;
        _area.DocumentChanged += OnDocumentChanged;
    }

    /// <summary>
    /// 생성기를 맨 앞에 둔다. 같은 오프셋에서 앞선 생성기(링크·메일·접기)가 문서 길이 있는 요소를 만들면 뒤 생성기는 차례가 오지 않는다.
    /// 접기 생성기는 View 를 붙일 때마다 맨 앞에 다시 들어오므로 조합 글자를 올릴 때마다 확인한다. 문서 길이 0 요소는 뒤 생성기를 막지 않는다.
    /// </summary>
    private void EnsureFirstGenerator()
    {
        var generators = _area.TextView.ElementGenerators;
        if (generators.Count > 0 && ReferenceEquals(generators[0], _generator))
        {
            return;
        }

        generators.Remove(_generator);
        generators.Insert(0, _generator);
    }

    private void OnTextInput(object? sender, TextInputEventArgs e) => Clear();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.PhysicalKey == PhysicalKey.Escape)
        {
            Clear();
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => Clear();

    private void OnLostFocus(object? sender, RoutedEventArgs e) => Clear();

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Clear();

    private void OnCaretMoved(object? sender, EventArgs e) => Clear();

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e) => Clear();

    /// <summary>끼워 넣을 요소를 만드는 생성기. 문서 길이 0 요소는 같은 오프셋에서 다시 묻기 때문에 이미 만든 오프셋을 기억해 건너뛴다.</summary>
    private sealed class PreeditGenerator(PreeditPresenter owner) : VisualLineElementGenerator
    {
        private readonly HashSet<int> _built = [];

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);
            _built.Clear();
        }

        public override void FinishGeneration()
        {
            _built.Clear();
            base.FinishGeneration();
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!owner.IsActive)
            {
                return -1;
            }

            int end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
            int best = -1;
            foreach (int offset in owner.Offsets)
            {
                if (offset >= startOffset && offset <= end && !_built.Contains(offset) && (best < 0 || offset < best))
                {
                    best = offset;
                }
            }

            return best;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            if (!owner.IsActive || !_built.Add(offset))
            {
                return null;
            }

            return new PreeditElement(owner.Text);
        }
    }

    /// <summary>조합 글자 한 덩어리. 문서 길이가 0 이라 문서 위치 계산에 끼지 않고, 화면에서만 글자 수만큼 자리를 차지한다.</summary>
    internal sealed class PreeditElement(string text) : VisualLineElement(text.Length, 0)
    {
        public string Text { get; } = text;

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            var properties = TextRunProperties ?? new VisualLineElementTextRunProperties(context.GlobalTextRunProperties);
            properties.SetTextDecorations(TextDecorations.Underline);
            return new TextCharacters(Text.AsMemory(Math.Clamp(startVisualColumn - VisualColumn, 0, Text.Length)), properties); // 줄 바꿈으로 나뉘어 중간부터 요청될 수 있다
        }
    }
}
