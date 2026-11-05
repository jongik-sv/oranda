using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using Oranda.App.Features.Completion;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Language;
using Oranda.Core.Sessions;
using Oranda.Language.Completion;

namespace Oranda.App.Tests.Features.Completion;

/// <summary>테이블 이름 자리에서 입력 중 자동으로 목록 열기. 시간은 가짜 타이머로 손수 울려서 실제로 기다리지 않는다.</summary>
public sealed partial class CompletionTests
{
    private sealed class FakeTimers
    {
        public List<(TimeSpan Delay, Action Run, Handle Handle)> Items { get; } = [];

        public int Live => Items.Count(i => !i.Handle.Disposed);

        public Handle Schedule(TimeSpan delay, Action run)
        {
            var handle = new Handle();
            Items.Add((delay, run, handle));
            return handle;
        }

        /// <summary>취소되지 않은 마지막 타이머를 울린다.</summary>
        public void Fire()
        {
            var last = Items.Last(i => !i.Handle.Disposed);
            last.Handle.Dispose();
            last.Run();
        }
    }

    private sealed class Handle : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class CountingProvider(ICompletionProvider inner, Task? gate = null) : ICompletionProvider
    {
        public int Calls { get; private set; }

        public async Task<CompletionResult> GetCompletionsAsync(ISession? session, string text, int offset, CancellationToken ct)
        {
            Calls++;
            if (gate is not null)
            {
                await gate.ConfigureAwait(false);
            }

            return await inner.GetCompletionsAsync(session, text, offset, ct).ConfigureAwait(false);
        }
    }

    private sealed class AutoRig
    {
        public required Window Window { get; init; }

        public required SqlEditorView View { get; init; }

        public required FakeTimers Timers { get; init; }

        public required CountingProvider Provider { get; init; }

        public TextArea Area => View.TextEditorControl.TextArea;

        /// <summary>글자를 하나씩 친다(편집기가 문서에 넣고, 입력 처리기가 자동 열기를 건다).</summary>
        public void Type(string text)
        {
            foreach (var ch in text)
            {
                Area.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = ch.ToString() });
            }
        }

        public async Task FireAndSettleAsync()
        {
            Timers.Fire();
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task WithAutoAsync(string text, Func<AutoRig, Task> body, ISession? session = null, Task? gate = null, bool useSession = true)
    {
        var (window, view, _) = Show(text, text.Length);
        var shared = CompletionController.Shared;
        var saved = (shared.ProviderResolver, shared.MetadataResolver, shared.SessionResolver, shared.Schedule, shared.LanguageResolver, shared.AutoOpenOnTyping);
        var fake = Controller(session);
        var provider = new CountingProvider(fake.ProviderResolver()!, gate);
        var timers = new FakeTimers();
        shared.ProviderResolver = () => provider;
        shared.MetadataResolver = fake.MetadataResolver;
        shared.SessionResolver = useSession ? fake.SessionResolver : _ => null;
        shared.Schedule = (delay, run) => timers.Schedule(delay, run);
        shared.LanguageResolver = () => new Oranda.Language.LanguageService();
        shared.AutoOpenOnTyping = true;
        shared.CancelAutoOpen();
        try
        {
            await body(new AutoRig { Window = window, View = view, Timers = timers, Provider = provider });
        }
        finally
        {
            shared.CancelAutoOpen();
            shared.CloseCompletion();
            (shared.ProviderResolver, shared.MetadataResolver, shared.SessionResolver, shared.Schedule, shared.LanguageResolver, shared.AutoOpenOnTyping) = saved;
            window.Close();
        }
    }

    private static List<string> Labels(CompletionController c)
        => [.. c.CurrentWindow!.CompletionList.CompletionData.Cast<CompletionData>().Select(d => d.Text)];

    [Fact]
    public Task Second_letter_of_a_table_name_opens_the_list_after_the_pause_but_the_first_does_not() => HeadlessHost.Run(() => WithAutoAsync("SELECT * FROM ", async r =>
    {
        r.Type("e");
        Assert.Empty(r.Timers.Items);

        r.Type("m");
        var timer = Assert.Single(r.Timers.Items);
        Assert.Equal(TimeSpan.FromMilliseconds(150), timer.Delay);
        Assert.Null(CompletionController.Shared.CurrentWindow);
        Assert.Equal(0, r.Provider.Calls);

        await r.FireAndSettleAsync();
        Assert.Contains("EMP", Labels(CompletionController.Shared));
        Assert.Equal(1, r.Provider.Calls);
    }));

    [Fact]
    public Task More_typing_inside_the_pause_restarts_it_and_asks_once() => HeadlessHost.Run(() => WithAutoAsync("SELECT * FROM ", async r =>
    {
        r.Type("emp");
        Assert.Equal(2, r.Timers.Items.Count); // "m" 과 "p" 가 각각 걸었고 앞의 것은 취소됐다
        Assert.Equal(1, r.Timers.Live);

        await r.FireAndSettleAsync();
        Assert.Equal(1, r.Provider.Calls); // 후보가 EMP 하나뿐이고 입력한 이름과 같아 열리지는 않는다
    }));

    [Theory]
    [InlineData("SELECT ", "ename")]
    [InlineData("SELECT * FROM emp WHERE ", "sa")]
    [InlineData("SELECT * FROM hr.", "jo")]
    public Task Other_places_and_schema_qualified_names_do_not_open(string prefix, string typed) => HeadlessHost.Run(() => WithAutoAsync(prefix, async r =>
    {
        r.Type(typed);
        if (r.Timers.Items.Count > 0)
        {
            await r.FireAndSettleAsync();
        }

        Assert.Null(CompletionController.Shared.CurrentWindow);
        Assert.Equal(0, r.Provider.Calls);
    }));

    [Fact]
    public Task Nothing_is_scheduled_without_a_session_in_a_read_only_editor_with_extra_carets_or_in_a_column_block() => HeadlessHost.Run(async () =>
    {
        await WithAutoAsync("SELECT * FROM e", r =>
        {
            r.Type("m");
            Assert.Single(r.Timers.Items); // 대조: 막힘이 없으면 걸린다
            return Task.CompletedTask;
        });

        await WithAutoAsync("SELECT * FROM e", r =>
        {
            r.Type("m");
            Assert.Empty(r.Timers.Items);
            return Task.CompletedTask;
        }, useSession: false);

        await WithAutoAsync("SELECT * FROM e", r =>
        {
            r.View.TextEditorControl.IsReadOnly = true;
            r.Type("m");
            Assert.Empty(r.Timers.Items);
            return Task.CompletedTask;
        });

        await WithAutoAsync("SELECT * FROM e\nSELECT * FROM e", r =>
        {
            r.View.Carets!.AddCaret(5);
            Assert.True(r.View.Carets.HasExtra);
            r.Type("m");
            Assert.Empty(r.Timers.Items);
            return Task.CompletedTask;
        });

        await WithAutoAsync("SELECT * FROM e\nSELECT * FROM e", r =>
        {
            var area = r.Area;
            area.Selection = new RectangleSelection(area, new TextViewPosition(1, 15), new TextViewPosition(2, 15));
            r.Type("m");
            Assert.Empty(r.Timers.Items);
            return Task.CompletedTask;
        });
    });

    [Fact]
    public Task Input_that_changed_before_the_timer_fires_is_not_asked() => HeadlessHost.Run(() => WithAutoAsync("SELECT * FROM ", async r =>
    {
        r.Type("em");
        r.View.TextEditorControl.CaretOffset = 0; // 커서가 움직였다
        await r.FireAndSettleAsync();
        Assert.Null(CompletionController.Shared.CurrentWindow);
        Assert.Equal(0, r.Provider.Calls);
    }));

    [Fact]
    public Task A_late_answer_is_dropped_when_more_was_typed_meanwhile() => HeadlessHost.Run(async () =>
    {
        var gate = new TaskCompletionSource();
        await WithAutoAsync("SELECT * FROM ", async r =>
        {
            r.Type("em");
            r.Timers.Fire();
            await HeadlessHost.WaitUntil(() => r.Provider.Calls == 1);

            r.Type("p"); // 답이 오기 전에 더 쳤다
            gate.SetResult();
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(CompletionController.Shared.CurrentWindow);
        }, gate: gate.Task);
    });

    [Fact]
    public Task Escape_keeps_the_list_closed_for_the_rest_of_the_word_but_the_next_word_opens_again() => HeadlessHost.Run(() => WithAutoAsync("SELECT * FROM ", async r =>
    {
        r.Type("em");
        await r.FireAndSettleAsync();
        Assert.NotNull(CompletionController.Shared.CurrentWindow);

        r.Area.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = r.Area });
        Dispatcher.UIThread.RunJobs();
        Assert.Null(CompletionController.Shared.CurrentWindow);

        int scheduled = r.Timers.Items.Count;
        r.Type("p");
        Assert.Equal(scheduled, r.Timers.Items.Count); // 같은 단어라 다시 걸지 않는다

        r.Type(", jo"); // 새 테이블 이름 자리의 새 단어
        Assert.True(r.Timers.Items.Count > scheduled);
        await r.FireAndSettleAsync();
        Assert.Contains("JOBS", Labels(CompletionController.Shared));
    }));

    [Fact]
    public Task No_candidates_or_only_the_exact_name_keeps_the_list_closed() => HeadlessHost.Run(async () =>
    {
        await WithAutoAsync("SELECT * FROM zz", async r =>
        {
            r.Type("z");
            await r.FireAndSettleAsync();
            Assert.Equal(1, r.Provider.Calls);
            Assert.Null(CompletionController.Shared.CurrentWindow);
        });

        await WithAutoAsync("SELECT * FROM job", async r =>
        {
            r.Type("s"); // 후보가 JOBS 하나뿐이고 입력한 이름과 같다
            await r.FireAndSettleAsync();
            Assert.Equal(1, r.Provider.Calls);
            Assert.Null(CompletionController.Shared.CurrentWindow);
        });
    });

    [Fact]
    public Task Closing_the_editor_or_typing_a_separator_releases_the_pending_timer() => HeadlessHost.Run(async () =>
    {
        await WithAutoAsync("SELECT * FROM ", r =>
        {
            r.Type("em");
            Assert.Equal(1, r.Timers.Live);
            r.Window.Content = null; // 탭이 닫혀 편집기가 화면에서 빠진다
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, r.Timers.Live);
            Assert.False(CompletionController.Shared.HasPendingAutoOpen);
            return Task.CompletedTask;
        });

        await WithAutoAsync("SELECT * FROM ", r =>
        {
            r.Type("em");
            Assert.Equal(1, r.Timers.Live);
            r.Type(" ");
            Assert.Equal(0, r.Timers.Live);
            return Task.CompletedTask;
        });
    });

    [Fact]
    public Task A_run_key_or_losing_focus_while_waiting_drops_the_pending_open() => HeadlessHost.Run(async () =>
    {
        await WithAutoAsync("SELECT * FROM ", async r =>
        {
            r.Type("em");
            Assert.Equal(1, r.Timers.Live);

            var keys = new ContextKeyService();
            keys.SetFocusSlot("sqlText");
            var registry = new RecordingRegistry();
            var dispatcher = new KeyDispatcher(() => KeyBindingTable.Build(KeymapLoader.LoadPreset("default"), macOS: false), registry, keys);
            r.Window.AddHandler(InputElement.KeyDownEvent, (_, e) => TunnelLikeMainWindow(dispatcher, e), RoutingStrategies.Tunnel);
            r.Area.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F5, Source = r.Area });
            await dispatcher.LastRun!;

            Assert.Equal(["oranda.exec.runScript"], registry.Executed);
            Assert.Equal(0, r.Timers.Live); // 쿼리를 돌린 뒤 목록이 뒤늦게 뜨지 않는다
            Assert.False(CompletionController.Shared.HasPendingAutoOpen);
        });

        await WithAutoAsync("SELECT * FROM ", r =>
        {
            r.Type("em");
            Assert.Equal(1, r.Timers.Live);
            r.Area.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent) { Source = r.Area });
            Assert.Equal(0, r.Timers.Live);
            return Task.CompletedTask;
        });
    });

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Tab)]
    public Task Enter_and_tab_put_the_selected_item_in_an_automatically_opened_list(Key key) => HeadlessHost.Run(() => WithAutoAsync("SELECT * FROM ", async r =>
    {
        r.Type("em");
        await r.FireAndSettleAsync();
        var list = CompletionController.Shared.CurrentWindow!.CompletionList;
        list.SelectedItem = list.CompletionData.Cast<CompletionData>().First(d => d.Text == "EMP");

        // 실제 주 창 터널 순서를 거친다: Enter·Tab 은 키맵에 없어 디스패처가 삼키지 않고 목록으로 간다.
        var keys = new ContextKeyService();
        keys.SetFocusSlot("sqlText");
        var registry = new RecordingRegistry();
        var dispatcher = new KeyDispatcher(() => KeyBindingTable.Build(KeymapLoader.LoadPreset("default"), OperatingSystem.IsMacOS()), registry, keys);
        r.Window.AddHandler(InputElement.KeyDownEvent, (_, e) => TunnelLikeMainWindow(dispatcher, e), RoutingStrategies.Tunnel);

        r.Area.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = r.Area });
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(registry.Executed);
        Assert.Equal("SELECT * FROM emp", r.View.TextEditorControl.Text);
        Assert.Null(CompletionController.Shared.CurrentWindow);
    }));
}
