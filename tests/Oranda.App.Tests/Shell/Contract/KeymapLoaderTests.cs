using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Input;
using Oranda.App.Shell.Keymaps;
using Oranda.Core.Shell;
using KeyBinding = Oranda.Core.Shell.KeyBinding;

namespace Oranda.App.Tests.Shell.Contract;

public partial class KeymapLoaderTests
{
    public static TheoryData<string> Presets
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var id in KeymapLoader.PresetIds)
            {
                data.Add(id);
            }

            return data;
        }
    }

    public static TheoryData<string, bool> PresetsByPlatform
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var id in KeymapLoader.PresetIds)
            {
                data.Add(id, false);
                data.Add(id, true);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Preset_parses_with_valid_ids_and_focus_keys(string id)
    {
        var keymap = KeymapLoader.LoadPreset(id);

        Assert.Equal(id, keymap.Id);
        Assert.Null(keymap.BasedOn);
        Assert.NotEmpty(keymap.Bindings);
        foreach (var b in keymap.Bindings)
        {
            Assert.Matches(CommandId(), b.Command);
            Assert.True(b.When is null || FocusScopes.Keys.Contains(b.When), $"{b.Command}: 모르는 when '{b.When}'");
            Assert.NotEmpty(b.Keys);
        }
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Preset_keys_are_normalized_and_parse_on_both_platforms(string id)
    {
        foreach (var b in KeymapLoader.LoadPreset(id).Bindings)
        {
            foreach (var k in b.Keys)
            {
                Assert.True(KeyChord.IsNormalized(k), $"{b.Command}: '{k}' 수식키 순서");
                Assert.DoesNotContain("Ctrl+", k, StringComparison.Ordinal); // Ctrl 은 mac 안에서만
                _ = KeyChord.Parse(k, macOS: false);
            }

            foreach (var k in b.Mac ?? [])
            {
                Assert.True(KeyChord.IsNormalized(k), $"{b.Command}: mac '{k}' 수식키 순서");
                _ = KeyChord.Parse(k, macOS: true);
            }
        }
    }

    [Theory]
    [MemberData(nameof(PresetsByPlatform))]
    public void Preset_has_no_key_conflict_in_overlapping_focus_scope(string id, bool macOS)
    {
        var entries = KeymapLoader.LoadPreset(id).Bindings
            .SelectMany(b => b.KeysFor(macOS).Select(k => (b.Command, b.When, Key: k, Gesture: KeyChord.Parse(k, macOS))))
            .ToList();

        var conflicts = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            for (var j = i + 1; j < entries.Count; j++)
            {
                var (a, b) = (entries[i], entries[j]);
                if (a.Gesture.SequenceEqual(b.Gesture) && FocusScopes.Overlaps(a.When, b.When))
                {
                    conflicts.Add($"{a.Key}: {a.Command}({a.When ?? "전역"}) ↔ {b.Command}({b.When ?? "전역"})");
                }
            }
        }

        Assert.True(conflicts.Count == 0, string.Join(Environment.NewLine, conflicts));
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Preset_declares_mac_keys_where_rule_requires(string id)
    {
        foreach (var b in KeymapLoader.LoadPreset(id).Bindings.Where(b => b.Mac is null))
        {
            foreach (var g in b.Keys.SelectMany(k => KeyChord.Parse(k, macOS: true)))
            {
                Assert.False(g.Key == Key.Cancel, $"{b.Command}: Ctrl+Break 는 맥에 없다 → mac 필요");
                Assert.False(IsOptionCharacter(g), $"{b.Command}: Option+문자는 맥에서 특수 문자 입력 → mac 필요");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Preset_mac_keys_avoid_reserved_system_keys(string id)
    {
        foreach (var b in KeymapLoader.LoadPreset(id).Bindings)
        {
            foreach (var g in b.KeysFor(macOS: true).SelectMany(k => KeyChord.Parse(k, macOS: true)))
            {
                var settingsKey = g.Equals(new KeyGesture(Key.OemComma, KeyModifiers.Meta)) && b.Command == "oranda.tools.settings";
                Assert.False(!settingsKey && IsMacReserved(g), $"{b.Command}: '{g}' 는 macOS 예약 키");
            }
        }
    }

    /// <summary>외래키 따라가기(GRID-11): F4 부모 행, Shift+F4 자식 행. 4개 프리셋 모두 그리드 포커스에서 두 열(Windows·mac)이 같다.</summary>
    [Fact]
    public void Every_preset_binds_f4_and_shift_f4_to_foreign_key_navigation_in_the_grid()
    {
        foreach (var id in KeymapLoader.PresetIds)
        {
            var bindings = KeymapLoader.LoadPreset(id).Bindings;
            var follow = Assert.Single(bindings, b => b.Command == "oranda.grid.followForeignKey");
            var children = Assert.Single(bindings, b => b.Command == "oranda.grid.showReferencingRows");
            Assert.Equal("gridFocus", follow.When);
            Assert.Equal("gridFocus", children.When);
            Assert.Equal(["F4"], follow.KeysFor(macOS: false));
            Assert.Equal(["F4"], follow.KeysFor(macOS: true));
            Assert.Equal(["Shift+F4"], children.KeysFor(macOS: false));
            Assert.Equal(["Shift+F4"], children.KeysFor(macOS: true));
        }
    }

    [Fact]
    public void Primary_maps_to_ctrl_on_windows_and_cmd_on_mac()
    {
        Assert.Equal(new KeyGesture(Key.Enter, KeyModifiers.Control | KeyModifiers.Shift), KeyChord.Parse("Primary+Shift+Enter", macOS: false)[0]);
        Assert.Equal(new KeyGesture(Key.Enter, KeyModifiers.Meta | KeyModifiers.Shift), KeyChord.Parse("Primary+Shift+Enter", macOS: true)[0]);
        Assert.Equal(new KeyGesture(Key.D1, KeyModifiers.Control), KeyChord.Parse("Ctrl+D1", macOS: true)[0]);
        Assert.Equal(2, KeyChord.Parse("Primary+K Primary+C", macOS: false).Count);
        Assert.Throws<InvalidDataException>(() => KeyChord.Parse("Primary+Nope", macOS: false));
        Assert.Throws<InvalidDataException>(() => KeyChord.Parse("Hyper+A", macOS: false));
        Assert.False(KeyChord.IsNormalized("Shift+Primary+A"));
    }

    [Fact]
    public void Mac_field_overrides_keys_only_on_mac()
    {
        var b = new KeyBinding { Command = "oranda.edit.redo", Keys = ["Primary+Y"], Mac = ["Primary+Shift+Z"] };

        Assert.Equal(["Primary+Y"], b.KeysFor(macOS: false));
        Assert.Equal(["Primary+Shift+Z"], b.KeysFor(macOS: true));
        Assert.Equal(["Primary+N"], new KeyBinding { Command = "x", Keys = ["Primary+N"] }.KeysFor(macOS: true));
    }

    [Fact]
    public void Keymap_round_trips_and_rejects_unknown_version()
    {
        var original = KeymapLoader.LoadPreset("default");
        var json = KeymapLoader.Serialize(original);

        Assert.DoesNotContain("\"basedOn\"", json, StringComparison.Ordinal);
        var again = KeymapLoader.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(original.Bindings.Count, again.Bindings.Count);
        Assert.Equal(original.Bindings[0].Keys, again.Bindings[0].Keys);

        var future = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => KeymapLoader.Parse(new MemoryStream(Encoding.UTF8.GetBytes(future))));
        Assert.Throws<ArgumentException>(() => KeymapLoader.LoadPreset("nope"));
    }

    private static bool IsOptionCharacter(KeyGesture g) =>
        g.KeyModifiers.HasFlag(KeyModifiers.Alt)
        && (g.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) == 0
        && (g.Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 || g.Key.ToString().StartsWith("Oem", StringComparison.Ordinal));

    private static bool IsMacReserved(KeyGesture g)
    {
        const KeyModifiers Cmd = KeyModifiers.Meta, Opt = KeyModifiers.Alt, Ctrl = KeyModifiers.Control, Shift = KeyModifiers.Shift;
        KeyGesture[] reserved =
        [
            new(Key.Q, Cmd), new(Key.H, Cmd), new(Key.H, Cmd | Opt), new(Key.M, Cmd), new(Key.M, Cmd | Opt),
            new(Key.Tab, Cmd), new(Key.Space, Cmd), new(Key.Space, Ctrl), new(Key.OemTilde, Cmd), new(Key.Escape, Cmd | Opt),
            new(Key.D3, Cmd | Shift), new(Key.D4, Cmd | Shift), new(Key.D5, Cmd | Shift),
            new(Key.Left, Ctrl), new(Key.Right, Ctrl), new(Key.Up, Ctrl), new(Key.Down, Ctrl), new(Key.F11, KeyModifiers.None),
            new(Key.F, Cmd | Ctrl), new(Key.Q, Cmd | Ctrl), new(Key.Space, Cmd | Ctrl), new(Key.OemComma, Cmd),
        ];
        return reserved.Contains(g);
    }

    [GeneratedRegex("^oranda\\.[a-z]+(\\.[a-z][A-Za-z0-9]*)+$")]
    private static partial Regex CommandId();
}
