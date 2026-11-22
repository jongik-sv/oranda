using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.AiSetup.TryIt;
using Oranda.App.Shell;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.AiSetup.TryIt;

/// <summary>B3 「해 보기」: 연습 탭 → SQL 설명 → 답 → 끝남, 3기능 안내, 고급 확인란 → 외부 에이전트 연결 창(가짜 편집기·가짜 런처).</summary>
public sealed class TryItStepTests
{
    private static ConnectionProfile Profile(string name, ConnectionEnvironment environment) =>
        new(name, "app", "db.example:1521/SAMPLE", Id: Guid.NewGuid(), Environment: environment);

    private sealed class FakeActions : ITryItActions
    {
        public List<(Guid ProfileId, string Title, string Sql)> Opened { get; } = [];

        public List<Guid> Explained { get; } = [];

        public int AgentOpens { get; private set; }

        public TryItExplainResult Result { get; set; } = new(true, "DUAL 은 한 행만 돌려주는 표입니다.");

        public Exception? ExplainFailure { get; set; }

        public bool OpenFails { get; set; }

        public string? Blocked { get; set; }

        /// <summary>처음 N 번의 설명 요청은 「탭이 닫혔다」 로 돌려준다.</summary>
        public int MissingTabs { get; set; }

        public string? CannotStartReason(ConnectionProfile profile, bool english) => Blocked;

        public Guid? OpenPracticeTab(ConnectionProfile profile, string title, string sql)
        {
            if (OpenFails)
            {
                return null;
            }

            Opened.Add((profile.Id, title, sql));
            return Guid.NewGuid();
        }

        public Task<TryItExplainResult> ExplainAsync(Guid tabId, CancellationToken ct)
        {
            Explained.Add(tabId);
            if (MissingTabs > 0)
            {
                MissingTabs--;
                return Task.FromResult(new TryItExplainResult(false, null, TabMissing: true));
            }

            return ExplainFailure is { } f ? Task.FromException<TryItExplainResult>(f) : Task.FromResult(Result);
        }

        public Task OpenExternalAgentAsync(CancellationToken ct)
        {
            AgentOpens++;
            return Task.CompletedTask;
        }
    }

    private static TryItViewModel Create(FakeActions actions, params ConnectionProfile[] enabled) =>
        new(actions, enabled, new ShellText(language: "ko"));

    [Fact]
    public void Connections_PutProductionLast_AndPickerOnlyWhenMany()
    {
        var prd = Profile("prd-erp", ConnectionEnvironment.Production);
        var dev = Profile("dev-erp", ConnectionEnvironment.Development);

        var many = Create(new FakeActions(), prd, dev);
        var one = Create(new FakeActions(), dev);

        Assert.Equal(["dev-erp", "prd-erp"], many.Connections.Select(c => c.Name));
        Assert.Equal("dev-erp", many.SelectedConnection?.Name);
        Assert.True(many.ShowPicker);
        Assert.False(one.ShowPicker);
    }

    [Fact]
    public void OpenTab_OpensPracticeTabWithDual_OnChosenConnection()
    {
        var actions = new FakeActions();
        var dev = Profile("dev-erp", ConnectionEnvironment.Development);
        var vm = Create(actions, dev);

        vm.OpenTab();

        var opened = Assert.Single(actions.Opened);
        Assert.Equal(dev.Id, opened.ProfileId);
        Assert.Equal("SELECT * FROM dual", opened.Sql);
        Assert.Contains("연습", opened.Title);
        Assert.True(vm.TabOpened);
        Assert.False(vm.IsComplete);
    }

    [Fact]
    public async Task Explain_CallsLauncherOnPracticeTab_AndAnswerCompletesStep()
    {
        var actions = new FakeActions();
        var vm = Create(actions, Profile("dev-erp", ConnectionEnvironment.Development));

        await vm.ExplainAsync(); // 탭을 아직 안 열었으면 먼저 연다

        Assert.Single(actions.Opened);
        Assert.Single(actions.Explained);
        Assert.True(vm.IsComplete);
        Assert.Contains("DUAL", vm.AnswerText);
        Assert.True(vm.ShowTips);
        Assert.Equal(3, vm.Tips.Count);
        Assert.Contains(vm.Tips, t => t.Title == "AI 튜닝");
        Assert.Contains(vm.Tips, t => t.Title == "오류 고치기");
        Assert.Contains(vm.Tips, t => t.Title == "AI 대화");
    }

    [Fact]
    public async Task NoAnswer_ShowsRetryHint_AndStepStaysOpen()
    {
        var actions = new FakeActions { Result = new TryItExplainResult(false, null) };
        var vm = Create(actions, Profile("dev-erp", ConnectionEnvironment.Development));

        await vm.ExplainAsync();

        Assert.False(vm.IsComplete);
        Assert.Contains("AI 대화", vm.StatusText);
        Assert.False(vm.IsWaiting);
    }

    [Fact]
    public async Task ExplainThrowing_IsShownAsFailure()
    {
        var actions = new FakeActions { ExplainFailure = new InvalidOperationException("boom (test)") };
        var vm = Create(actions, Profile("dev-erp", ConnectionEnvironment.Development));

        await vm.ExplainAsync();

        Assert.False(vm.IsComplete);
        Assert.False(vm.IsWaiting);
        Assert.NotEmpty(vm.StatusText);
    }

    [Fact]
    public async Task Finish_WithAdvancedChecked_OpensExternalAgentWindow()
    {
        var actions = new FakeActions();
        var vm = Create(actions, Profile("dev-erp", ConnectionEnvironment.Development));
        await vm.ExplainAsync();
        Assert.False(vm.AlsoExternalAgent); // 기본 꺼짐

        vm.AlsoExternalAgent = true;
        await vm.FinishAsync(default);

        Assert.Equal(1, actions.AgentOpens);
    }

    [Fact]
    public async Task Finish_WithoutAdvanced_DoesNotOpenAgentWindow()
    {
        var actions = new FakeActions();
        var vm = Create(actions, Profile("dev-erp", ConnectionEnvironment.Development));
        await vm.ExplainAsync();

        await vm.FinishAsync(default);

        Assert.Equal(0, actions.AgentOpens);
    }

    [Fact]
    public void NoConnections_IsSkipped()
    {
        var vm = Create(new FakeActions());

        Assert.True(vm.IsSkipped);
        Assert.False(vm.CanOpenTab);
    }

    [Fact]
    public void OpenTabFailure_ShowsHint()
    {
        var vm = Create(new FakeActions { OpenFails = true }, Profile("dev-erp", ConnectionEnvironment.Development));

        vm.OpenTab();

        Assert.False(vm.TabOpened);
        Assert.NotEmpty(vm.StatusText);
    }

    [Fact]
    public async Task CannotStart_ShowsReason_AndDoesNotCallLauncher()
    {
        var actions = new FakeActions { Blocked = "먼저 「dev-erp」 에 접속한 뒤 다시 눌러 주세요." };
        var vm = Create(actions, Profile("dev-erp", ConnectionEnvironment.Development));

        await vm.ExplainAsync();

        Assert.Empty(actions.Explained);
        Assert.Contains("접속한 뒤", vm.StatusText);
        Assert.False(vm.IsComplete);
    }

    [Fact]
    public async Task ClosedPracticeTab_IsReopened_NotSendingAnotherTab()
    {
        var actions = new FakeActions { MissingTabs = 1 };
        var vm = Create(actions, Profile("dev-erp", ConnectionEnvironment.Development));
        vm.OpenTab();

        await vm.ExplainAsync();

        Assert.Equal(2, actions.Opened.Count);
        Assert.Equal(2, actions.Explained.Count);
        Assert.NotEqual(actions.Explained[0], actions.Explained[1]);
        Assert.True(vm.IsComplete);
    }

    [Fact]
    public async Task RefusalReason_IsShownAsIs()
    {
        var actions = new FakeActions { Result = new TryItExplainResult(false, null, "이 접속은 AI 허용 수준이 「끔」 입니다.") };
        var vm = Create(actions, Profile("dev-erp", ConnectionEnvironment.Development));

        await vm.ExplainAsync();

        Assert.Contains("끔", vm.StatusText);
    }

    [Fact]
    public void AnswerReader_TakesTextAfterRequest_OnlyWhenCompleted()
    {
        ChatEntry sent = new ChatAssistantEntry("SQL 설명 — 보냈습니다");
        ChatEntry note = new ChatAssistantEntry("입력이 커서 주 모델로 보냈습니다");
        ChatEntry request = new ChatUserEntry("SQL 설명");

        Assert.Equal(new TryItExplainResult(true, "답"), TryItAnswerReader.Read([sent, request, new ChatAssistantEntry("답")], completed: true));
        Assert.False(TryItAnswerReader.Read([sent, request, new ChatAssistantEntry("반쪽 답")], completed: false).Answered); // 멈춤
        Assert.False(TryItAnswerReader.Read([sent, note, request], completed: true).Answered); // 도구 카드만, 글 없음: 안내 줄을 답으로 읽지 않는다
        var error = TryItAnswerReader.Read([sent, request, new ChatAssistantEntry("오류: 로그인이 필요합니다")], completed: false);
        Assert.False(error.Answered);
        Assert.Contains("로그인", error.Reason);
        Assert.False(TryItAnswerReader.Read([], completed: true).Answered);
    }

    [Fact]
    public async Task FinishThrowing_IsSwallowed()
    {
        var vm = new TryItViewModel(new ThrowingAgent(), [Profile("dev-erp", ConnectionEnvironment.Development)], new ShellText(language: "ko"));
        vm.AlsoExternalAgent = true;

        await vm.FinishAsync(default); // 던지지 않는다
    }

    private sealed class ThrowingAgent : ITryItActions
    {
        public Guid? OpenPracticeTab(ConnectionProfile profile, string title, string sql) => Guid.NewGuid();

        public Task<TryItExplainResult> ExplainAsync(Guid tabId, CancellationToken ct) => Task.FromResult(new TryItExplainResult(true, "답"));

        public Task OpenExternalAgentAsync(CancellationToken ct) => throw new InvalidOperationException("no owner (test)");
    }
}
