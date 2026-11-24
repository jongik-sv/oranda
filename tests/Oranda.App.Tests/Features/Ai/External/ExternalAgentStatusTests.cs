using Avalonia.Controls;
using Avalonia.Interactivity;
using Oranda.App.Features.Ai.External.Connect;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Contracts;
using Oranda.Platform.Ai.Skills;

namespace Oranda.App.Tests.Features.Ai.External;

/// <summary>상태 표시줄 「에이전트 연결됨」 띠와 최근 호출 목록(설계서 §3.1·§6.4·§6.5). 연결 원천은 가짜다(계약 머지 전).</summary>
public sealed class ExternalAgentStatusTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(9));

    private static readonly Oranda.App.Shell.ShellContributions App =
        Oranda.App.Shell.ShellContributions.Scan(typeof(ExternalAgentStatusViewModel).Assembly);

    private static (ExternalAgentStatusViewModel Vm, FakeLink Link, FakeClock Time) Create(FakeSkills? skills = null)
    {
        var time = new FakeClock(Start);
        var link = new FakeLink();
        var vm = ExternalAgentStatusViewModel.For(link, skills, time, post: a => a());
        vm.SkillCheck.GetAwaiter().GetResult();
        return (vm, link, time);
    }

    [Fact]
    public void Status_item_is_registered_with_a_view()
    {
        var item = Assert.Single(App.StatusBarItems, i => i.Id == "oranda.status.externalAgent");

        Assert.Equal(typeof(ExternalAgentStatusViewModel), item.ItemType);
        Assert.Equal(0, item.CollapseRank);
        Assert.NotNull(typeof(ExternalAgentStatusViewModel).Assembly.GetType(typeof(ExternalAgentStatusViewModel).FullName!.Replace("ViewModel", "View", StringComparison.Ordinal)));
    }

    [Fact]
    public void Hidden_without_a_link_or_while_not_connected()
    {
        using var none = ExternalAgentStatusViewModel.For(post: a => a());
        Assert.False(none.IsVisible);

        var (vm, _, _) = Create();
        Assert.False(vm.IsVisible);
        Assert.Equal(string.Empty, vm.Text);
        vm.Dispose();
    }

    [Fact]
    public void Connected_band_shows_client_last_call_and_time_left()
    {
        var (vm, link, time) = Create();

        link.Connect(["Claude Code"], Start.AddHours(8));
        Assert.True(vm.IsVisible);
        Assert.Equal("밖의 AI 에이전트 연결됨 · Claude Code · 호출 없음 · 8시간 0분 남음", vm.Text);

        time.Advance(TimeSpan.FromMinutes(20));
        link.Call("list_tabs", AiToolJudgement.Allowed, 812);
        time.Advance(TimeSpan.FromSeconds(12));

        Assert.Equal("밖의 AI 에이전트 연결됨 · Claude Code · 최근 호출 12초 전 · 7시간 40분 남음", vm.Text);
        vm.Dispose();
    }

    [Fact]
    public void Several_clients_are_joined_and_no_expiry_means_no_time_left()
    {
        var (vm, link, _) = Create();

        link.Connect(["Claude Code", "Codex"], expiresAt: null);

        Assert.Equal("밖의 AI 에이전트 연결됨 · Claude Code, Codex · 호출 없음", vm.Text);
        Assert.False(vm.CanExtend);
        vm.Dispose();
    }

    [Fact]
    public void Recent_calls_keep_the_newest_twenty_newest_first()
    {
        var (vm, link, time) = Create();
        link.Connect(["Codex"], Start.AddHours(8));

        for (var i = 0; i < 25; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            link.Call("tool_" + i, i == 24 ? AiToolJudgement.Denied : AiToolJudgement.Allowed, i * 10);
        }

        Assert.Equal(20, vm.RecentCalls.Count);
        Assert.Equal("tool_24", vm.RecentCalls[0].Tool);
        Assert.Equal("tool_5", vm.RecentCalls[^1].Tool);
        Assert.Equal("거부", vm.RecentCalls[0].JudgementText);
        Assert.Equal("240자", vm.RecentCalls[0].SizeText);
        Assert.Equal("09:00:25", vm.RecentCalls[0].TimeText);
        vm.Dispose();
    }

    [Fact]
    public void Extend_is_offered_only_in_the_last_ten_minutes_and_calls_the_link()
    {
        var (vm, link, time) = Create();
        link.Connect(["Claude Code"], Start.AddHours(8));
        Assert.False(vm.CanExtend);

        time.Advance(TimeSpan.FromHours(8) - TimeSpan.FromMinutes(10));
        Assert.True(vm.CanExtend);
        Assert.Contains("10분 남음", vm.Text, StringComparison.Ordinal);

        vm.Extend();
        Assert.Equal(1, link.Extended);
        Assert.False(vm.CanExtend);
        vm.Dispose();
    }

    [Fact]
    public void Disconnect_calls_the_link_and_the_band_disappears_and_calls_are_cleared_on_the_next_connection()
    {
        var (vm, link, _) = Create();
        link.Connect(["Claude Code"], Start.AddHours(8));
        link.Call("get_status", AiToolJudgement.Allowed, 100);

        vm.Disconnect();

        Assert.Equal(1, link.Disconnected);
        Assert.False(vm.IsVisible);

        link.Connect(["Codex"], Start.AddHours(8));
        Assert.Empty(vm.RecentCalls);
        vm.Dispose();
    }

    [Fact]
    public void Calls_after_disconnect_are_ignored()
    {
        var (vm, link, _) = Create();
        link.Connect(["Claude Code"], Start.AddHours(8));
        link.Disconnect();

        link.Call("late", AiToolJudgement.Allowed, 1);

        Assert.Empty(vm.RecentCalls);
        vm.Dispose();
    }

    [Fact]
    public async Task Outdated_skill_shows_a_reinstall_band_even_when_not_connected_and_reinstall_updates_only_outdated_targets()
    {
        var skills = new FakeSkills(AgentSkillState.Outdated, AgentSkillState.Foreign, AgentSkillState.Current);
        var (vm, _, _) = Create(skills);

        Assert.True(vm.IsVisible);
        Assert.True(vm.SkillReinstallNeeded);
        Assert.Equal("밖의 AI 에이전트 스킬 다시 설치 필요", vm.Text);

        await vm.ReinstallSkillsAsync();

        Assert.Equal([AgentSkillTarget.Claude], skills.Installed);
        Assert.False(vm.SkillReinstallNeeded);
        Assert.False(vm.IsVisible);
        vm.Dispose();
    }

    [Fact]
    public void Connected_band_adds_the_reinstall_notice_to_the_tooltip()
    {
        var skills = new FakeSkills(AgentSkillState.Outdated, AgentSkillState.NotInstalled, AgentSkillState.NotInstalled);
        var (vm, link, _) = Create(skills);

        link.Connect(["Claude Code"], Start.AddHours(8));

        Assert.StartsWith("밖의 AI 에이전트 연결됨", vm.Text, StringComparison.Ordinal);
        Assert.Contains("스킬", vm.ToolTip, StringComparison.Ordinal);
        vm.Dispose();
    }

    [Fact]
    public Task View_shows_the_band_and_the_details_list_and_buttons_act_on_the_link() => HeadlessHost.Run(() =>
    {
        var (vm, link, time) = Create();
        link.Connect(["Claude Code"], Start.AddHours(8));
        link.Call("list_tabs", AiToolJudgement.Allowed, 812);
        link.Call("query", AiToolJudgement.Denied, 64);
        var view = new ExternalAgentStatusView { DataContext = vm, Height = 22 };
        var window = new Window { Width = 640, Height = 420, Content = new Border { Child = view, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom } };
        try
        {
            window.Show();
            HeadlessHost.Settle(window);
            Assert.Equal(vm.Text, Avalonia.Automation.AutomationProperties.GetName(view));

            view.ShowDetails();
            HeadlessHost.Pump();
            var panel = Assert.IsType<StackPanel>(view.DetailsFlyout!.Content);
            Assert.Equal(ExternalAgentStatusViewModel.DetailsIntro, Assert.IsType<TextBlock>(panel.Children[0]).Text); // 맨 위 = 이 띠가 무엇인지
            Assert.Equal(380, panel.Width); // 플라이아웃 틀보다 좁게 고정(넘으면 글이 잘렸다)
            var rows = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(panel.Children[2]).Content);
            Assert.Equal(2, rows.Children.Count);
            var buttons = Assert.IsType<StackPanel>(panel.Children[3]);
            var disconnect = Assert.IsType<Button>(Assert.Single(buttons.Children));
            Assert.Equal("끊기", disconnect.Content);

            disconnect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessHost.Pump();
            Assert.Equal(1, link.Disconnected);
            Assert.False(vm.IsVisible);

            link.Connect(["Claude Code"], Start.AddHours(8));
            time.Advance(TimeSpan.FromHours(8) - TimeSpan.FromMinutes(5));
            view.ShowDetails();
            HeadlessHost.Pump();
            var flyoutPanel = Assert.IsType<StackPanel>(view.DetailsFlyout!.Content);
            var buttons2 = Assert.IsType<StackPanel>(flyoutPanel.Children[^2]);
            Assert.Equal(["연장", "끊기"], buttons2.Children.OfType<Button>().Select(b => (string)b.Content!));

            // 맨 아래 링크: 설정 「AI」 탭 ③ 「밖의 AI 에이전트」 로 간다(ai-settings-one-place).
            var opened = 0;
            view.OpenSettings = () => opened++;
            var settingsLink = Assert.IsType<HyperlinkButton>(flyoutPanel.Children[^1]);
            Assert.Equal("설정 › AI 의 「밖의 AI 에이전트」 열기", settingsLink.Content);
            settingsLink.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, opened);
        }
        finally
        {
            view.DetailsFlyout?.Hide();
            window.Close();
            vm.Dispose();
        }
    });

    [Fact]
    public Task Production_in_the_grant_turns_the_band_to_the_warning_colour_and_says_so() => HeadlessHost.Run(() =>
    {
        var (vm, link, time) = Create();
        var view = new ExternalAgentStatusView { DataContext = vm, Height = 22 };
        var window = new Window { Width = 640, Height = 120, Content = view };
        try
        {
            window.Show();
            link.Connect(["Codex"], Start.AddHours(8));
            HeadlessHost.Settle(window);
            Assert.False(vm.IncludesProduction);
            Assert.DoesNotContain("운영 포함", vm.Text, StringComparison.Ordinal);
            Assert.Equal("OrandaEnvDevelopmentBrush", view.DotBrushKey);

            link.Connect(["Codex"], Start.AddHours(8), production: true);
            HeadlessHost.Settle(window);
            Assert.True(vm.IncludesProduction);
            Assert.StartsWith("밖의 AI 에이전트 연결됨 · 운영 포함 · Codex", vm.Text, StringComparison.Ordinal);
            Assert.Equal("OrandaEnvProductionBrush", view.DotBrushKey);

            // 만료 임박이어도 운영 색이 앞선다(「N분 남음」·「연장」은 그대로).
            time.Advance(TimeSpan.FromHours(8) - TimeSpan.FromMinutes(5));
            link.Connect(["Codex"], Start.AddHours(8), production: true);
            HeadlessHost.Settle(window);
            Assert.True(vm.CanExtend);
            Assert.Equal("OrandaEnvProductionBrush", view.DotBrushKey);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    });

    [Theory]
    [InlineData(5, "5초 전")]
    [InlineData(59, "59초 전")]
    [InlineData(60, "1분 전")]
    [InlineData(3599, "59분 전")]
    [InlineData(3600 + 180, "1시간 3분 전")]
    public void Ago_text(int seconds, string expected)
    {
        Assert.Equal(expected, ExternalAgentStatusViewModel.AgoText(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(30, "1분 미만 남음")]
    [InlineData(600, "10분 남음")]
    [InlineData(7 * 3600 + 40 * 60, "7시간 40분 남음")]
    [InlineData(7 * 3600 + 39 * 60 + 48, "7시간 40분 남음")]
    [InlineData(59 * 60 + 30, "1시간 0분 남음")]
    public void Left_text(int seconds, string expected)
    {
        Assert.Equal(expected, ExternalAgentStatusViewModel.LeftText(TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>시험이 돌리는 시계(한국 시간대). 타이머는 아무것도 하지 않는다: 띠 글은 읽을 때 지금 시각으로 만든다.</summary>
    internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private static readonly TimeZoneInfo Seoul = TimeZoneInfo.CreateCustomTimeZone("test+9", TimeSpan.FromHours(9), "test+9", "test+9");

        private DateTimeOffset _now = start.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => Seoul;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();

        public void Advance(TimeSpan by) => _now += by;

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeLink : IExternalAgentLink
    {
        public ExternalAgentLinkState? Current { get; private set; }

        public int Extended { get; private set; }

        public int Disconnected { get; private set; }

        public event EventHandler? Changed;

        public event EventHandler<ExternalAgentCall>? CallCompleted;

        public void Connect(IReadOnlyList<string> clients, DateTimeOffset? expiresAt, bool production = false)
        {
            Current = new ExternalAgentLinkState(clients, expiresAt, production);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Call(string tool, AiToolJudgement judgement, int size)
            => CallCompleted?.Invoke(this, new ExternalAgentCall(Current is { Clients.Count: > 0 } c ? c.Clients[0] : null, tool, judgement, size));

        public void Disconnect()
        {
            Disconnected++;
            Current = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Extend()
        {
            Extended++;
            Current = Current! with { ExpiresAt = Current.ExpiresAt!.Value.AddHours(8) };
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal sealed class FakeSkills(params AgentSkillState[] states) : IAgentSkillInstaller
    {
        private readonly AgentSkillState[] _states = states;

        public List<AgentSkillTarget> Installed { get; } = [];

        public string OrandactlPath => "/opt/Oranda/orandactl";

        public IReadOnlyList<AgentSkillStatus> Inspect()
            => [.. AgentSkillTargets.All.Select((t, i) => new AgentSkillStatus(t, "/h/" + t, _states[i]))];

        public IReadOnlyList<AgentSkillInstallResult> Install(IEnumerable<AgentSkillTarget> targets, bool overwriteForeign = false)
        {
            var list = targets.ToList();
            Installed.AddRange(list);
            var results = new List<AgentSkillInstallResult>();
            foreach (var t in list)
            {
                var before = _states[(int)t];
                if (before == AgentSkillState.Foreign && !overwriteForeign)
                {
                    results.Add(new(t, "/h/" + t, AgentSkillInstallOutcome.SkippedForeign));
                    continue;
                }

                _states[(int)t] = AgentSkillState.Current;
                results.Add(new(t, "/h/" + t, before == AgentSkillState.Outdated ? AgentSkillInstallOutcome.Updated : AgentSkillInstallOutcome.Installed));
            }

            return results;
        }

        public List<AgentSkillTarget> Removed { get; } = [];

        public IReadOnlyList<AgentSkillInstallResult> Uninstall(IEnumerable<AgentSkillTarget> targets)
        {
            var results = new List<AgentSkillInstallResult>();
            foreach (var t in targets)
            {
                var before = _states[(int)t];
                if (before is AgentSkillState.Current or AgentSkillState.Outdated)
                {
                    _states[(int)t] = AgentSkillState.NotInstalled;
                    Removed.Add(t);
                    results.Add(new(t, "/h/" + t, AgentSkillInstallOutcome.Removed));
                }
                else
                {
                    results.Add(new(t, "/h/" + t, before == AgentSkillState.Foreign ? AgentSkillInstallOutcome.SkippedForeign : AgentSkillInstallOutcome.NotInstalled));
                }
            }

            return results;
        }

        public bool NeedsReinstall() => _states.Contains(AgentSkillState.Outdated);

        public HashSet<AgentSkillTarget> Present { get; } = [];

        public bool IsClientPresent(AgentSkillTarget target) => Present.Contains(target);

        public void SetState(AgentSkillTarget target, AgentSkillState state) => _states[(int)target] = state;
    }
}
