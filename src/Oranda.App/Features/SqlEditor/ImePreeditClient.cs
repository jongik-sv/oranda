using Avalonia;
using Avalonia.Input.TextInput;
using Avalonia.Media;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// AvaloniaEdit 가 주는 입력기 클라이언트(조합 글자를 받지 않는다)를 감싸, 조합 글자를 <see cref="PreeditPresenter"/> 에 넘기는 클라이언트.
/// 캐럿 위치·주변 글·선택 같은 값은 안쪽 클라이언트에 그대로 위임하므로 AvaloniaEdit 와 같은 값을 낸다(포크·리플렉션 없음).
/// 바뀌는 것은 <see cref="SupportsPreedit"/> 와 SetPreeditText 둘뿐이다.
/// </summary>
internal sealed class PreeditInputMethodClient : TextInputMethodClient
{
    private readonly PreeditPresenter _presenter;

    public PreeditInputMethodClient(TextInputMethodClient inner, PreeditPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(presenter);
        Inner = inner;
        _presenter = presenter;
        inner.TextViewVisualChanged += OnInnerTextViewVisualChanged;
        inner.CursorRectangleChanged += OnInnerCursorRectangleChanged;
        inner.SurroundingTextChanged += OnInnerSurroundingTextChanged;
        inner.SelectionChanged += OnInnerSelectionChanged;
        inner.ResetRequested += OnInnerResetRequested;
        inner.InputPaneActivationRequested += OnInnerInputPaneActivationRequested;
    }

    /// <summary>감싼 클라이언트.</summary>
    public TextInputMethodClient Inner { get; }

    public override Visual TextViewVisual => Inner.TextViewVisual;

    public override bool SupportsPreedit => true;

    public override bool SupportsSurroundingText => Inner.SupportsSurroundingText;

    public override string SurroundingText => Inner.SurroundingText;

    public override Rect CursorRectangle => Inner.CursorRectangle;

    public override TextSelection Selection
    {
        get => Inner.Selection;
        set => Inner.Selection = value;
    }

    public override void SetPreeditText(string? text) => _presenter.Set(text);

    public override void SetPreeditText(string? text, int? cursorPos) => _presenter.Set(text);

    public override void ExecuteContextMenuAction(ContextMenuAction action) => Inner.ExecuteContextMenuAction(action);

    /// <summary>안쪽 클라이언트의 이벤트 구독을 끊는다.</summary>
    public void Release()
    {
        Inner.TextViewVisualChanged -= OnInnerTextViewVisualChanged;
        Inner.CursorRectangleChanged -= OnInnerCursorRectangleChanged;
        Inner.SurroundingTextChanged -= OnInnerSurroundingTextChanged;
        Inner.SelectionChanged -= OnInnerSelectionChanged;
        Inner.ResetRequested -= OnInnerResetRequested;
        Inner.InputPaneActivationRequested -= OnInnerInputPaneActivationRequested;
    }

    private void OnInnerTextViewVisualChanged(object? sender, EventArgs e) => RaiseTextViewVisualChanged();

    private void OnInnerCursorRectangleChanged(object? sender, EventArgs e) => RaiseCursorRectangleChanged();

    private void OnInnerSurroundingTextChanged(object? sender, EventArgs e) => RaiseSurroundingTextChanged();

    private void OnInnerSelectionChanged(object? sender, EventArgs e) => RaiseSelectionChanged();

    private void OnInnerResetRequested(object? sender, EventArgs e) => RequestReset();

    private void OnInnerInputPaneActivationRequested(object? sender, EventArgs e) => RaiseInputPaneActivationRequested();
}
