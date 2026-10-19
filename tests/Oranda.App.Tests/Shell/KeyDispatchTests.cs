using Avalonia.Input;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.Core.Shell;
using KeyBinding = Oranda.Core.Shell.KeyBinding;

namespace Oranda.App.Tests.Shell;

// TSK-03-01: 키맵 → 명령 연결(조회표·연속 키·포커스 범위).
public class KeyDispatchTests
{
    private static Keymap Map(params KeyBinding[] bindings) => new() { Id = "t", Name = "t", Bindings = bindings };

    private static KeyBinding B(string command, string[] keys, string? when = null, string[]? mac = null) =>
        new() { Command = command, Keys = keys, When = when, Mac = mac };

    private static (Key, KeyModifiers) K(Key key, KeyModifiers m = KeyModifiers.None) => (key, m);

    [Fact]
    public void Primary_is_ctrl_on_windows_and_cmd_on_mac()
    {
        var map = Map(B("oranda.view.commandPalette", ["Primary+Shift+P"]));
        var keys = new ContextKeyService();

        var win = KeyBindingTable.Build(map, macOS: false).Find([K(Key.P, KeyModifiers.Control | KeyModifiers.Shift)], keys);
        var mac = KeyBindingTable.Build(map, macOS: true).Find([K(Key.P, KeyModifiers.Meta | KeyModifiers.Shift)], keys);
        var wrong = KeyBindingTable.Build(map, macOS: true).Find([K(Key.P, KeyModifiers.Control | KeyModifiers.Shift)], keys);

        Assert.Equal(new KeyMatch(KeyMatchKind.Match, "oranda.view.commandPalette"), win);
        Assert.Equal(KeyMatchKind.Match, mac.Kind);
        Assert.Equal(KeyMatchKind.None, wrong.Kind);
    }

    [Fact]
    public void Mac_specific_keys_replace_the_default_keys_on_mac_only()
    {
        var map = Map(B("oranda.x.run", ["Primary+M"], mac: ["Primary+Shift+M"]));
        var keys = new ContextKeyService();

        Assert.Equal(KeyMatchKind.Match, KeyBindingTable.Build(map, false).Find([K(Key.M, KeyModifiers.Control)], keys).Kind);
        Assert.Equal(KeyMatchKind.None, KeyBindingTable.Build(map, true).Find([K(Key.M, KeyModifiers.Meta)], keys).Kind);
        Assert.Equal(KeyMatchKind.Match, KeyBindingTable.Build(map, true).Find([K(Key.M, KeyModifiers.Meta | KeyModifiers.Shift)], keys).Kind);
    }

    [Fact]
    public void Two_step_chords_report_prefix_then_match()
    {
        var table = KeyBindingTable.Build(Map(B("oranda.edit.commentLines", ["Primary+K Primary+C"])), macOS: false);
        var keys = new ContextKeyService();
        var ctrlK = K(Key.K, KeyModifiers.Control);
        var ctrlC = K(Key.C, KeyModifiers.Control);

        Assert.Equal(KeyMatchKind.Prefix, table.Find([ctrlK], keys).Kind);
        Assert.Equal(new KeyMatch(KeyMatchKind.Match, "oranda.edit.commentLines"), table.Find([ctrlK, ctrlC], keys));
        Assert.Equal(KeyMatchKind.None, table.Find([ctrlK, K(Key.X, KeyModifiers.Control)], keys).Kind);
    }

    [Fact]
    public void Same_key_with_different_focus_scopes_picks_the_active_scope()
    {
        var table = KeyBindingTable.Build(
            Map(
                B("oranda.exec.runScript", ["F5"], "sqlDocFocus"),
                B("oranda.schema.refresh", ["F5"], "schemaFocus"),
                B("oranda.global.f5", ["F5"])),
            macOS: false);
        var keys = new ContextKeyService();

        Assert.Equal("oranda.global.f5", table.Find([K(Key.F5)], keys).CommandId);

        keys.SetFocusSlot("sqlText");
        Assert.Equal("oranda.exec.runScript", table.Find([K(Key.F5)], keys).CommandId);

        keys.SetFocusSlot("schema");
        Assert.Equal("oranda.schema.refresh", table.Find([K(Key.F5)], keys).CommandId);
    }

    [Fact]
    public void Scoped_binding_does_not_fire_outside_its_scope()
    {
        var table = KeyBindingTable.Build(Map(B("oranda.exec.runScript", ["F5"], "sqlDocFocus")), macOS: false);
        var keys = new ContextKeyService();

        Assert.Equal(KeyMatchKind.None, table.Find([K(Key.F5)], keys).Kind);
    }

    [Fact]
    public void Shortcut_text_comes_from_the_first_binding_of_the_command()
    {
        var table = KeyBindingTable.Build(Map(B("oranda.x.a", ["Primary+Enter", "Primary+K"]), B("oranda.x.chord", ["Primary+K Primary+C"])), macOS: false);

        Assert.Equal("Ctrl+Enter", table.ShortcutText("oranda.x.a"));
        Assert.Equal("Ctrl+K Ctrl+C", table.ShortcutText("oranda.x.chord"));
        Assert.Null(table.ShortcutText("oranda.x.none"));
        Assert.Equal(new KeyGesture(Key.Enter, KeyModifiers.Control), table.SingleGesture("oranda.x.a"));
        Assert.Null(table.SingleGesture("oranda.x.chord"));
    }

    [Fact]
    public void Every_preset_loads_into_a_table_and_default_binds_the_shell_commands()
    {
        foreach (var id in KeymapLoader.PresetIds)
        {
            foreach (var mac in new[] { false, true })
            {
                Assert.NotNull(KeyBindingTable.Build(KeymapLoader.LoadPreset(id), mac));
            }
        }

        var table = KeyBindingTable.Build(KeymapLoader.LoadPreset("default"), macOS: false);
        Assert.Equal("Ctrl+Shift+P", table.ShortcutText("oranda.view.commandPalette"));
        Assert.Equal("Ctrl+W", table.ShortcutText("oranda.file.close"));
        Assert.Equal("Ctrl+,", table.ShortcutText("oranda.tools.settings"));
        Assert.Equal("Ctrl+0", table.ShortcutText("oranda.view.zoomReset"));
    }

    private sealed class Recorder : ICommandRegistry
    {
        public List<string> Executed { get; } = [];

        public IReadOnlyList<CommandDefinition> Commands => [];

        public event EventHandler? CommandsChanged
        {
            add { }
            remove { }
        }

        public CommandDefinition? Find(string idOrAlias) => null;

        public bool CanExecute(string idOrAlias, IContextKeys keys) => true;

        public Task<bool> ExecuteAsync(string idOrAlias, IContextKeys keys, object? parameter = null, CancellationToken cancellationToken = default)
        {
            Executed.Add(idOrAlias);
            return Task.FromResult(true);
        }

        public IDisposable Register(CommandDefinition definition, ICommandHandler handler) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Dispatcher_runs_the_bound_command_and_swallows_the_key()
    {
        var registry = new Recorder();
        var table = KeyBindingTable.Build(Map(B("oranda.x.a", ["Primary+Shift+A"])), macOS: false);
        var dispatcher = new KeyDispatcher(() => table, registry, new ContextKeyService());

        Assert.False(dispatcher.Handle(Key.A, KeyModifiers.None));
        Assert.False(dispatcher.Handle(Key.LeftShift, KeyModifiers.Shift));
        Assert.True(dispatcher.Handle(Key.A, KeyModifiers.Control | KeyModifiers.Shift));
        await dispatcher.LastRun!;

        Assert.Equal(["oranda.x.a"], registry.Executed);
    }

    [Fact]
    public async Task Dispatcher_waits_for_the_second_step_of_a_chord_and_restarts_on_a_wrong_key()
    {
        var registry = new Recorder();
        var table = KeyBindingTable.Build(Map(B("oranda.x.chord", ["Primary+K Primary+C"]), B("oranda.x.other", ["Primary+X"])), macOS: false);
        var dispatcher = new KeyDispatcher(() => table, registry, new ContextKeyService());

        Assert.True(dispatcher.Handle(Key.K, KeyModifiers.Control));
        Assert.Empty(registry.Executed);
        Assert.True(dispatcher.Handle(Key.C, KeyModifiers.Control));
        await dispatcher.LastRun!;
        Assert.Equal(["oranda.x.chord"], registry.Executed);

        // 첫 단 뒤 엉뚱한 키: 그 키를 새 첫 단으로 다시 본다.
        Assert.True(dispatcher.Handle(Key.K, KeyModifiers.Control));
        Assert.True(dispatcher.Handle(Key.X, KeyModifiers.Control));
        await dispatcher.LastRun!;
        Assert.Equal(["oranda.x.chord", "oranda.x.other"], registry.Executed);
    }

    [Fact]
    public async Task Unknown_command_ids_in_a_keymap_are_ignored_not_fatal()
    {
        var table = KeyBindingTable.Build(Map(B("oranda.gone.command", ["F9"])), macOS: false);
        var registry = new CommandRegistry([]);
        var dispatcher = new KeyDispatcher(() => table, registry, new ContextKeyService());

        Assert.True(dispatcher.Handle(Key.F9, KeyModifiers.None));
        await dispatcher.LastRun!; // 예외 없이 끝난다
    }

    [Theory]
    [InlineData(Key.A, KeyModifiers.Meta, true, true)]          // ⌘A: 입력 칸의 모두 선택
    [InlineData(Key.V, KeyModifiers.Control, false, true)]      // Ctrl+V
    [InlineData(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift, true, true)]
    [InlineData(Key.Left, KeyModifiers.Alt, true, true)]        // macOS 단어 이동
    [InlineData(Key.D5, KeyModifiers.None, true, true)]         // 글자
    [InlineData(Key.Back, KeyModifiers.None, false, true)]
    [InlineData(Key.Enter, KeyModifiers.Meta, true, false)]     // ⌘↵ 는 실행 명령으로
    [InlineData(Key.F9, KeyModifiers.None, false, false)]       // F 키는 명령으로
    [InlineData(Key.Escape, KeyModifiers.None, true, false)]
    [InlineData(Key.A, KeyModifiers.Control, true, false)]      // macOS 의 Ctrl+A 는 편집 키가 아니다
    public void Text_boxes_keep_their_editing_keys(Key key, KeyModifiers modifiers, bool macOS, bool editing)
        => Assert.Equal(editing, KeyDispatcher.IsTextEditingKey(key, modifiers, macOS));
}
