using System.Diagnostics;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Oranda.App.Features.SqlEditor;

// SqlEditorView 의 일부: macOS 한글 조합 중 Enter 한 번으로 확정과 줄 바꾸기를 함께 한다.
//
// macOS 입력기는 조합 중인 Enter 를 먼저 받아 KeyDown 을 Key.Return 이 아니라 Key.ImeProcessed(PhysicalKey.Enter)로 올리고,
// 그 뒤에 확정 글자를 TextInput 으로 보낸다. Return 이 오지 않으므로 AvaloniaEdit 의 Enter 바인딩이 동작하지 않아 글자만 확정되고 줄은 그대로다.
// 이 순서(ImeProcessed+Enter KeyDown → TextInput)를 보면 확정 글자가 들어간 직후 일반 Return KeyDown 을 한 번 다시 올려, 자동 들여쓰기·선택 바꾸기·
// 추가 커서·열 블록·읽기 전용·자동 완성 확정까지 일반 Return 과 같은 길을 지나게 한다(새 줄바꿈 처리를 만들지 않는다).
public partial class SqlEditorView
{
    /// <summary>조합 중 Enter 표시가 유효한 시간. 확정 글자는 같은 입력 처리 안에서 바로 이어 오므로 넉넉한 상한일 뿐이다.</summary>
    private static readonly TimeSpan DefaultImeEnterWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>켜짐 여부. macOS 입력기만 확인했으므로 그 밖의 플랫폼(Windows MS IME 등)은 동작을 바꾸지 않는다. 시험이 바꾼다.</summary>
    internal static bool ImeEnterEnabled { get; set; } = OperatingSystem.IsMacOS();

    /// <summary>표시가 유효한 시간(시험이 바꾼다).</summary>
    internal TimeSpan ImeEnterWindow { get; set; } = DefaultImeEnterWindow;

    // 조합 중 Enter 를 본 시각(Stopwatch 틱). 0 = 표시 없음.
    private long _imeEnterAt;

    private void InstallImeEnter()
    {
        var area = Editor.TextArea;
        // 앞선 터널 핸들러가 KeyDown 을 Handled 로 만들어도 이전 표시는 지워야 하므로 처리된 이벤트도 받는다.
        area.AddHandler(InputElement.KeyDownEvent, OnImeEnterKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        // 확정 글자는 편집기 안쪽(AvaloniaEdit·MultiCaretController)이 Handled 로 처리하므로 처리된 이벤트도 받는다. 글자가 이미 문서에 들어간 뒤(버블)에 본다.
        area.AddHandler(InputElement.TextInputEvent, OnImeEnterTextInput, RoutingStrategies.Bubble, handledEventsToo: true);
        area.AddHandler(InputElement.PointerPressedEvent, (_, _) => _imeEnterAt = 0, RoutingStrategies.Tunnel, handledEventsToo: true);
        area.LostFocus += (_, _) => _imeEnterAt = 0;
    }

    /// <summary>확정 글자의 마지막 문자가 한글인가: 음절(U+AC00-D7A3), 자모(U+1100-11FF·A960-A97F·D7B0-D7FF), 호환 자모(U+3130-318F).</summary>
    internal static bool EndsWithHangul(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        char c = text[^1];
        return c is >= '가' and <= '힣'
            or >= 'ᄀ' and <= 'ᇿ'
            or >= 'ꥠ' and <= '꥿'
            or >= 'ힰ' and <= '퟿'
            or >= '㄰' and <= '㆏';
    }

    private void OnImeEnterKeyDown(object? sender, KeyEventArgs e)
    {
        // 어떤 KeyDown 이든 이전 표시는 버린다. 조합 중 Enter 일 때만 새로 세운다(수식키가 있는 Enter 는 실행 단축키 등이므로 건드리지 않는다).
        bool composingEnter = ImeEnterEnabled
            && e.Key == Key.ImeProcessed
            && e.PhysicalKey is PhysicalKey.Enter or PhysicalKey.NumPadEnter
            && e.KeyModifiers == KeyModifiers.None;
        _imeEnterAt = composingEnter ? Stopwatch.GetTimestamp() : 0;
    }

    private void OnImeEnterTextInput(object? sender, TextInputEventArgs e)
    {
        if (_imeEnterAt == 0)
        {
            return;
        }

        long at = _imeEnterAt;
        _imeEnterAt = 0; // 되돌린 Return 이 다시 이 길로 들어와도 두 번 바뀌지 않게 먼저 지운다.
        if (string.IsNullOrEmpty(e.Text)
            || e.Text.Contains('\n')
            || e.Text.Contains('\r')
            || !EndsWithHangul(e.Text) // 한자 후보·이모지 선택기·일본어/중국어 입력기의 Enter 는 원래 확정만 하는 키다.
            || Stopwatch.GetElapsedTime(at) > ImeEnterWindow
            || Editor.IsReadOnly)
        {
            return;
        }

        var area = Editor.TextArea;
        area.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Return,
            PhysicalKey = PhysicalKey.Enter,
            KeyModifiers = KeyModifiers.None,
            Source = area,
        });
    }
}
