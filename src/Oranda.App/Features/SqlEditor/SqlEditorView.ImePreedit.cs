using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using AvaloniaEdit.Editing;

namespace Oranda.App.Features.SqlEditor;

// SqlEditorView 의 일부: macOS 한글 조합 중인 글자를 캐럿 자리에 바로 보여 준다(조합 표시는 ImePreedit.cs).
//
// AvaloniaEdit 의 입력기 클라이언트는 SupportsPreedit=false 고정이라 macOS 에서는 조합 중인 글자가 확정 전까지 보이지 않는다.
// AvaloniaEdit 클래스 처리기 뒤에 인스턴스 처리기로 e.Client 를 감싼 클라이언트로 바꿔(포크 없음) 조합 글자를 받는다.
public partial class SqlEditorView
{
    /// <summary>켜짐 여부. macOS 입력기만 확인했으므로 그 밖의 플랫폼(Windows MS IME 등)은 클라이언트를 바꾸지 않는다. 시험이 바꾼다.</summary>
    internal static bool PreeditEnabled { get; set; } = OperatingSystem.IsMacOS();

    private PreeditPresenter? _preedit;
    private PreeditInputMethodClient? _preeditClient;

    /// <summary>조합 글자 표시(시험용).</summary>
    internal PreeditPresenter Preedit => _preedit!;

    private void InstallImePreedit()
    {
        var area = Editor.TextArea;
        _preedit = new PreeditPresenter(area, PreeditOffsets);
        _preedit.Install();
        // 클래스 처리기(AvaloniaEdit)가 e.Client 를 세운 뒤에 보도록 버블 단계의 인스턴스 처리기로 단다.
        area.AddHandler(InputElement.TextInputMethodClientRequestedEvent, OnTextInputMethodClientRequested, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnTextInputMethodClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        // 읽기 전용이면 AvaloniaEdit 가 클라이언트를 주지 않는다(null): 지금처럼 입력하지 않는다.
        if (!PreeditEnabled || _preedit is null || e.Client is not { } client || client is PreeditInputMethodClient)
        {
            return;
        }

        if (_preeditClient is null || !ReferenceEquals(_preeditClient.Inner, client))
        {
            _preeditClient?.Release();
            _preeditClient = new PreeditInputMethodClient(client, _preedit);
        }

        e.Client = _preeditClient;
    }

    /// <summary>조합 글자를 그릴 자리: 열 블록이면 줄마다 블록 왼쪽 끝, 아니면 주 커서와 추가 커서.</summary>
    private IReadOnlyList<int> PreeditOffsets()
    {
        var area = Editor.TextArea;
        if (area.Selection is RectangleSelection rs)
        {
            var starts = rs.Segments.Select(x => x.StartOffset).Distinct().Order().ToList();
            if (starts.Count > 0)
            {
                return starts;
            }
        }

        return _carets?.AllOffsets() ?? [area.Caret.Offset];
    }
}
