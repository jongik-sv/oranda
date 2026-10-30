using Avalonia.Input;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.Core.Shell;
using KeyBinding = Oranda.Core.Shell.KeyBinding;

namespace Oranda.App.Tests.Shell;

// mac 편집기에서 Option+문자 바인딩이 명령으로 가고 글자로 들어가지 않는지(KeyDown → TextInput 순서를 가정한 시험).
public class OptionTextGuardTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);

    private static (OptionTextGuard Guard, Recorder Registry, KeyDispatcher Dispatcher, Func<DateTime> SetNow, Action<TimeSpan> Advance) Create()
    {
        var registry = new Recorder();
        var map = new Keymap
        {
            Id = "t",
            Name = "t",
            Bindings =
            [
                new KeyBinding { Command = "oranda.nav.describeObject", Keys = ["F4"], Mac = ["F4", "Alt+C", "Ctrl+Alt+C"], When = "editorFocus" },
            ],
        };
        var table = KeyBindingTable.Build(map, macOS: true);
        var keys = new ContextKeyService();
        keys.Set("editorFocus", true);
        var dispatcher = new KeyDispatcher(() => table, registry, keys);
        var now = T0;
        var guard = new OptionTextGuard(dispatcher, () => now);
        return (guard, registry, dispatcher, () => now, d => now += d);
    }

    [Fact]
    public async Task Bound_option_letter_runs_the_command_and_swallows_the_following_text()
    {
        var (guard, registry, dispatcher, _, _) = Create();

        var handled = dispatcher.Handle(Key.C, KeyModifiers.Alt);
        guard.NoteKeyDown(Key.C, KeyModifiers.Alt, handled);
        await dispatcher.LastRun!;

        Assert.True(guard.ShouldSwallowText("c"));
        Assert.Equal(["oranda.nav.describeObject"], registry.Executed);
        Assert.False(guard.ShouldSwallowText("c"));
    }

    [Fact]
    public async Task Unbound_option_letter_types_its_character_and_none_key_falls_back_to_the_typed_letter()
    {
        var (guard, registry, dispatcher, _, _) = Create();

        var handled = dispatcher.Handle(Key.D, KeyModifiers.Alt);
        guard.NoteKeyDown(Key.D, KeyModifiers.Alt, handled);

        Assert.False(guard.ShouldSwallowText("∂"));
        Assert.Empty(registry.Executed);

        // 입력기가 키를 None 으로 넘겨 KeyDown 이 맞지 못해도 글자로 되짚어 명령을 실행한다.
        guard.NoteKeyDown(Key.None, KeyModifiers.Alt, false);
        Assert.True(guard.ShouldSwallowText("c"));
        await dispatcher.LastRun!;
        Assert.Equal(["oranda.nav.describeObject"], registry.Executed);
    }

    [Fact]
    public void Text_typed_right_after_option_escape_is_not_swallowed()
    {
        var (guard, _, _, _, advance) = Create();

        // Option+Esc(자동완성 열기)는 글자를 만들지 않으므로, 이어서 곧바로 친 글자는 편집기에 들어가야 한다.
        guard.NoteKeyDown(Key.Escape, KeyModifiers.Alt, handled: true);
        advance(TimeSpan.FromMilliseconds(50));

        Assert.False(guard.ShouldSwallowText("."));
    }

    [Fact]
    public void Text_long_after_the_option_key_is_not_swallowed()
    {
        var (guard, _, dispatcher, _, advance) = Create();

        guard.NoteKeyDown(Key.C, KeyModifiers.Alt, dispatcher.Handle(Key.C, KeyModifiers.Alt));
        advance(OptionTextGuard.Window + TimeSpan.FromMilliseconds(1));

        Assert.False(guard.ShouldSwallowText("c"));
    }

    [Fact]
    public void Plain_typing_without_option_is_never_swallowed()
    {
        var (guard, _, _, _, _) = Create();

        guard.NoteKeyDown(Key.C, KeyModifiers.None, false);
        guard.NoteKeyDown(Key.C, KeyModifiers.Meta | KeyModifiers.Alt, true);

        Assert.False(guard.ShouldSwallowText("c"));
    }

    [Theory]
    [InlineData(Key.ImeProcessed, PhysicalKey.C, KeyModifiers.Alt, Key.C)]
    [InlineData(Key.ImeProcessed, PhysicalKey.C, KeyModifiers.Control | KeyModifiers.Alt, Key.C)]
    [InlineData(Key.ImeProcessed, PhysicalKey.C, KeyModifiers.None, Key.ImeProcessed)]
    [InlineData(Key.ImeProcessed, PhysicalKey.C, KeyModifiers.Shift, Key.ImeProcessed)]
    [InlineData(Key.D, PhysicalKey.C, KeyModifiers.Alt, Key.D)]
    public void Ime_processed_key_with_modifier_resolves_from_the_physical_key(Key key, PhysicalKey physical, KeyModifiers mods, Key expected)
        => Assert.Equal(expected, KeyDispatcher.EffectiveKey(key, physical, mods));

    [Fact]
    public async Task Korean_ime_option_c_runs_the_bound_command_and_swallows_the_text()
    {
        var (guard, registry, dispatcher, _, _) = Create();

        // 실앱 기록: KeyDown Key=ImeProcessed Physical=C Mods=Alt 뒤에 TextInput "c"
        var key = KeyDispatcher.EffectiveKey(Key.ImeProcessed, PhysicalKey.C, KeyModifiers.Alt);
        var handled = dispatcher.Handle(key, KeyModifiers.Alt);
        guard.NoteKeyDown(key, KeyModifiers.Alt, handled);
        await dispatcher.LastRun!;

        Assert.True(handled);
        Assert.True(guard.ShouldSwallowText("c"));
        Assert.Equal(["oranda.nav.describeObject"], registry.Executed);
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
}
