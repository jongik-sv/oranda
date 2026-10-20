using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Oranda.App.Shell.Menus;

/// <summary>
/// macOS 앱 메뉴(시스템 메뉴 막대 맨 왼쪽 「Oranda」)의 앞쪽 항목: 「Oranda 정보」·「업데이트 확인…」·「설정…(⌘,)」.
/// 서비스·숨기기·종료는 Avalonia 가 이 메뉴 뒤에 기본 항목으로 붙인다(<c>PopulateStandardOSXMenuItems</c>).
/// </summary>
public sealed class MacAppMenu
{
    public const string AboutCommandId = "oranda.help.about";

    public const string UpdateCommandId = "oranda.help.checkUpdate";

    public const string SettingsCommandId = "oranda.tools.settings";

    private readonly NativeMenuItem _about = new("Oranda 정보");
    private readonly NativeMenuItem _update = new("업데이트 확인…");
    private readonly NativeMenuItem _settings = new("설정…") { Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta) };

    public MacAppMenu()
    {
        _about.Click += (_, _) => _ = ShellCommandRunner.RunAsync(AboutCommandId);
        _update.Click += (_, _) => _ = ShellCommandRunner.RunAsync(UpdateCommandId);
        _settings.Click += (_, _) => _ = ShellCommandRunner.RunAsync(SettingsCommandId);
        Menu = new NativeMenu { _about, _update, new NativeMenuItemSeparator(), _settings };
    }

    public NativeMenu Menu { get; }

    private enum StandardItem
    {
        Services,
        Hide,
        HideOthers,
        ShowAll,
        Quit,
    }

    /// <summary>Avalonia 가 앱 메뉴 뒤에 영어로 붙인 기본 항목(<c>PopulateStandardOSXMenuItems</c>)을 처음 본 영어 글자로 알아본 것.</summary>
    private readonly Dictionary<NativeMenuItem, StandardItem> _standard = [];

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
        _update.Header = text.Lookup("Cmd." + UpdateCommandId);
        _settings.Header = text.Lookup("Cmd." + SettingsCommandId) + "…";
        LocalizeStandardItems(text);
    }

    /// <summary>
    /// 서비스·숨기기·기타 가리기·모두 보기·종료를 지금 언어로 바꾼다. Avalonia 는 이 항목들을 영어로 붙이고 글자를 바꿀 방법을 주지 않지만,
    /// 항목은 우리가 넘긴 <see cref="Menu"/> 안에 들어 있어 <see cref="NativeMenuItem.Header"/> 를 바꾸면 된다.
    /// 「기타 가리기」 단축키도 표준(⌥⌘H)으로 바로잡는다(Avalonia 기본은 ⌥⌘Q).
    /// </summary>
    private void LocalizeStandardItems(IShellText text)
    {
        var name = Application.Current?.Name ?? "Oranda";
        foreach (var item in Menu.Items)
        {
            if (item is not NativeMenuItem native || native == _about || native == _update || native == _settings)
            {
                continue;
            }

            if (!_standard.TryGetValue(native, out var kind) && Classify(native.Header) is { } found)
            {
                _standard[native] = kind = found;
            }
            else if (!_standard.ContainsKey(native))
            {
                continue;
            }

            native.Header = kind switch
            {
                StandardItem.Services => text.Lookup("Menu.app.services"),
                StandardItem.Hide => string.Format(CultureInfo.CurrentCulture, text.Lookup("Menu.app.hide"), name),
                StandardItem.HideOthers => text.Lookup("Menu.app.hideOthers"),
                StandardItem.ShowAll => text.Lookup("Menu.app.showAll"),
                _ => string.Format(CultureInfo.CurrentCulture, text.Lookup("Menu.app.quit"), name),
            };
            if (kind == StandardItem.HideOthers)
            {
                native.Gesture = new KeyGesture(Key.H, KeyModifiers.Meta | KeyModifiers.Alt);
            }
        }
    }

    private static StandardItem? Classify(string? header) => header switch
    {
        "Services" => StandardItem.Services,
        "Hide Others" => StandardItem.HideOthers,
        "Show All" => StandardItem.ShowAll,
        "Quit" => StandardItem.Quit,
        not null when header.StartsWith("Hide ", StringComparison.Ordinal) => StandardItem.Hide,
        _ => null,
    };
}
