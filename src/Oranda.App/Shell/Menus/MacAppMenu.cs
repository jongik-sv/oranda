using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace Oranda.App.Shell.Menus;

/// <summary>
/// macOS 앱 메뉴(시스템 메뉴 막대 맨 왼쪽 「Oranda」)의 앞쪽 항목: 「Oranda 정보」·「설정…(⌘,)」.
/// 서비스·숨기기·종료는 Avalonia 가 이 메뉴 뒤에 기본 항목으로 붙인다(<c>PopulateStandardOSXMenuItems</c>).
/// </summary>
public sealed class MacAppMenu
{
    public const string AboutCommandId = "oranda.help.about";

    public const string SettingsCommandId = "oranda.tools.settings";

    private readonly NativeMenuItem _about = new("Oranda 정보");
    private readonly NativeMenuItem _settings = new("설정…") { Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta) };

    public MacAppMenu()
    {
        _about.Click += (_, _) => _ = ShellCommandRunner.RunAsync(AboutCommandId);
        _settings.Click += (_, _) => _ = ShellCommandRunner.RunAsync(SettingsCommandId);
        Menu = new NativeMenu { _about, new NativeMenuItemSeparator(), _settings };
    }

    public NativeMenu Menu { get; }

    /// <summary>항목 글자를 지금 언어로 다시 달고, 언어가 바뀔 때마다 다시 단다.</summary>
    public void Bind(IShellText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Apply(text);
        text.LanguageChanged += (_, _) => Apply(text);
    }

    private void Apply(IShellText text)
    {
        _about.Header = text.Lookup("Cmd." + AboutCommandId);
        _settings.Header = text.Lookup("Cmd." + SettingsCommandId) + "…";
    }
}
