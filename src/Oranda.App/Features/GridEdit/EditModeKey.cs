using Oranda.App.Features.Localization;
using Oranda.App.Shell.Keymaps;

namespace Oranda.App.Features.GridEdit;

/// <summary>
/// 안내 문구에 보일 「결과 편집 모드로 실행」(<c>oranda.exec.runForEdit</c>)의 실제 단축키.
/// 키맵(프리셋·사용자 정의)에서 읽으므로 macOS 는 ⌃ 기호, 키를 바꾸면 바뀐 키가, 키가 없으면 괄호째 빠진다.
/// </summary>
public static class EditModeKey
{
    public const string CommandId = "oranda.exec.runForEdit";

    private static readonly Lazy<KeyBindingTable> DefaultTable =
        new(() => KeyBindingTable.Build(KeymapLoader.LoadPreset("default"), OperatingSystem.IsMacOS()));

    /// <summary>셸이 키맵 서비스를 묶기 전(시험·시작 직후)에는 기본 키맵의 키를 쓴다.</summary>
    private static Func<string?> _source = static () => DefaultTable.Value.ShortcutText(CommandId);

    /// <summary>키맵에서 명령의 단축키 글(<c>KeymapService.ShortcutText</c>)을 읽는 함수를 묶는다. 셸이 시작할 때 한 번 부른다.</summary>
    public static void Install(Func<string?> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>화면에 보일 단축키 글. 바인딩이 없으면 빈 글.</summary>
    public static string Text => KeyBindingTable.ForScreen(_source()) ?? string.Empty;

    /// <summary>문구 끝에 붙일 「(키)」. 바인딩이 없으면 빈 글.</summary>
    public static string Suffix => Text is { Length: > 0 } t ? "(" + t + ")" : string.Empty;

    /// <summary>
    /// 지금 언어에 맞는 템플릿을 골라 <c>{0}</c> 자리에 「(키)」를 끼운다.
    /// 문구가 키 글을 품고 있어 <see cref="LiteralTranslator"/> 의 통문장 대응표로는 번역할 수 없으므로 한/영을 함께 받는다.
    /// </summary>
    public static string Localize(string ko, string en) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, LiteralTranslator.Language == "en" ? en : ko, Suffix);
}
