using Avalonia.Input;
using Oranda.App.Features.Flashback;
using Oranda.App.Features.GridEdit;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell;

// 맥에서 「결과 편집 모드로 실행」: ⌃↵ 추가(F9 유지) + 안내 문구에 실제 키 표시.
public class MacEditKeyTests
{
    private const string Command = "oranda.exec.runForEdit";

    private static readonly string[] Presets = ["default", "orange5", "plsqldev"];

    private static ContextKeyService SqlFocus()
    {
        var keys = new ContextKeyService();
        keys.Set("sqlDocFocus", true);
        keys.Set("editorFocus", true);
        return keys;
    }

    [Theory]
    [InlineData("default")]
    [InlineData("orange5")]
    [InlineData("plsqldev")]
    public void Mac_runs_for_edit_with_ctrl_enter_and_f9(string preset)
    {
        var table = KeyBindingTable.Build(KeymapLoader.LoadPreset(preset), macOS: true);
        var keys = SqlFocus();

        Assert.Equal(new KeyMatch(KeyMatchKind.Match, Command), table.Find([(Key.Return, KeyModifiers.Control)], keys));
        Assert.Equal(new KeyMatch(KeyMatchKind.Match, Command), table.Find([(Key.F9, KeyModifiers.None)], keys));
        Assert.NotEqual(Command, table.Find([(Key.Return, KeyModifiers.Meta)], keys).CommandId);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("orange5")]
    [InlineData("plsqldev")]
    public void Windows_keeps_only_f9(string preset)
    {
        var table = KeyBindingTable.Build(KeymapLoader.LoadPreset(preset), macOS: false);
        var keys = SqlFocus();

        Assert.Equal(new KeyMatch(KeyMatchKind.Match, Command), table.Find([(Key.F9, KeyModifiers.None)], keys));
        Assert.NotEqual(Command, table.Find([(Key.Return, KeyModifiers.Control)], keys).CommandId);
        Assert.Equal("F9", table.ShortcutText(Command));
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("orange5", true)]
    [InlineData("plsqldev", true)]
    [InlineData("sqldev", true)]
    [InlineData("default", false)]
    public void Presets_do_not_bind_one_key_to_two_commands_in_the_same_scope(string preset, bool mac)
    {
        var seen = new Dictionary<string, string>();
        foreach (var b in KeymapLoader.LoadPreset(preset).Bindings)
        {
            foreach (var text in b.KeysFor(mac))
            {
                var chord = string.Join(' ', KeyChord.Parse(text, mac).Select(KeyBindingTable.Display));
                var slot = chord + "@" + (b.When ?? "");
                if (seen.TryGetValue(slot, out var other) && other != b.Command)
                {
                    Assert.Fail($"{preset}(mac={mac}): {chord} [{b.When}] 이(가) {other} 와 {b.Command} 에 겹친다.");
                }

                seen[slot] = b.Command;
            }
        }
    }

    [Fact]
    public void Hint_text_shows_the_default_keymap_key_of_this_platform()
    {
        var expected = OperatingSystem.IsMacOS() ? KeyBindingTable.ForScreen("Ctrl+Enter") : "F9";

        Assert.Equal(expected, EditModeKey.Text);
        Assert.Equal($"편집 모드({expected})로 다시 실행하면 플래시백을 쓸 수 있습니다.", FlashbackTarget.NeedsEditMode);
        Assert.DoesNotContain("F9", ForMac("default"));
    }

    private static string ForMac(string preset) =>
        KeyBindingTable.ForScreen(KeyBindingTable.Build(KeymapLoader.LoadPreset(preset), macOS: true).ShortcutText(Command)) ?? string.Empty;
}
