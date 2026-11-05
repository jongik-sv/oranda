using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Oranda.App.Features.Completion;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Completion;

/// <summary>
/// 목록이 열린 채로 누른 키가 어디로 가는지 고정한다. 실행 키는 목록보다 먼저 주 창 터널 처리기(키맵 → 명령)로 가서 쿼리를 실행하고,
/// 글자를 넣는 Enter·Tab 은 키맵에 없어 목록으로 간다.
/// </summary>
public sealed partial class CompletionTests
{
    /// <summary>실행 키로 실제 명령이 불리는 키(default 키맵, when = sqlDocFocus). macOS 는 Primary = Cmd.</summary>
    public static TheoryData<bool, Key, KeyModifiers, string> RunKeys => new()
    {
        { false, Key.Enter, KeyModifiers.Control, "oranda.exec.runStatement" },
        { false, Key.F5, KeyModifiers.None, "oranda.exec.runScript" },
        { false, Key.F9, KeyModifiers.None, "oranda.exec.runForEdit" },
        { false, Key.Enter, KeyModifiers.Control | KeyModifiers.Shift, "oranda.exec.runStep" },
        { true, Key.Enter, KeyModifiers.Meta, "oranda.exec.runStatement" },
        { true, Key.Enter, KeyModifiers.Control, "oranda.exec.runForEdit" },
        { true, Key.F5, KeyModifiers.None, "oranda.exec.runScript" },
        { true, Key.F9, KeyModifiers.None, "oranda.exec.runForEdit" },
    };

    [Theory]
    [MemberData(nameof(RunKeys))]
    public Task Run_keys_execute_the_query_even_while_the_list_is_open(bool macOS, Key key, KeyModifiers modifiers, string commandId) => HeadlessHost.Run(async () =>
    {
        var (window, view, _) = Show("SELECT e. FROM emp e", 9);
        var c = CompletionController.Shared;
        var (p, m, s) = (c.ProviderResolver, c.MetadataResolver, c.SessionResolver);
        var fake = Controller();
        (c.ProviderResolver, c.MetadataResolver, c.SessionResolver) = (fake.ProviderResolver, fake.MetadataResolver, fake.SessionResolver);
        var area = view.TextEditorControl.TextArea;
        var keys = new ContextKeyService();
        keys.SetFocusSlot("sqlText");
        var registry = new RecordingRegistry();
        var dispatcher = new KeyDispatcher(() => KeyBindingTable.Build(KeymapLoader.LoadPreset("default"), macOS), registry, keys);
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => TunnelLikeMainWindow(dispatcher, e), RoutingStrategies.Tunnel);
        try
        {
            Assert.True(await c.ShowCompletionAsync(area, null, automatic: true));
            Assert.NotNull(c.CurrentWindow);
            Assert.True(area.IsKeyboardFocusWithin); // 목록 팝업이 포커스를 가져가면 sqlDocFocus 가 풀려 실행 키가 명령에 닿지 않는다
            string before = view.TextEditorControl.Text;

            var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = area };
            area.RaiseEvent(args);
            await dispatcher.LastRun!;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([commandId], registry.Executed);
            Assert.True(args.Handled);
            Assert.Equal(before, view.TextEditorControl.Text); // 목록이 Enter 로 항목을 넣지 않았다
            Assert.Null(c.CurrentWindow);
        }
        finally
        {
            c.CloseCompletion();
            (c.ProviderResolver, c.MetadataResolver, c.SessionResolver) = (p, m, s);
            window.Close();
        }
    });

    /// <summary><c>MainWindow.OnKeyDownTunnel</c> 과 같은 순서(유효 키 → 디스패처 → 처리됨 표시). 그 메서드는 private 이라 같은 줄을 둔다.</summary>
    private static void TunnelLikeMainWindow(KeyDispatcher dispatcher, KeyEventArgs e)
    {
        var key = KeyDispatcher.EffectiveKey(e.Key, e.PhysicalKey, e.KeyModifiers);
        if (!e.Handled && dispatcher.Handle(key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    private sealed class RecordingRegistry : ICommandRegistry
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
