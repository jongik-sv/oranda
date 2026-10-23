using Avalonia.Media;

namespace Oranda.App.Shell.Appearance;

/// <summary>
/// 화면 곳곳의 고정폭 글꼴(설정과 무관한 고정 값). axaml 은 앱 리소스 <see cref="FixedKey"/>·<see cref="CodeKey"/>(App.axaml)를,
/// 코드로 만든 화면은 <see cref="Code"/> 를 쓴다. 설정을 따르는 그리드·편집기 글꼴은 <see cref="ThemeService"/> 의 리소스다.
/// </summary>
public static class MonoFonts
{
    /// <summary>값·DDL·로그 칸 글꼴 리소스 키.</summary>
    public const string FixedKey = "OrandaFixedFontFamily";

    /// <summary>SQL 코드·바인드·출력 칸 글꼴 리소스 키(Cascadia Mono 먼저).</summary>
    public const string CodeKey = "OrandaCodeFontFamily";

    public const string FixedName = "Consolas,Menlo,monospace";

    public const string CodeName = "Cascadia Mono,Consolas,Menlo,monospace";

    public static FontFamily Code { get; } = new(CodeName);
}
