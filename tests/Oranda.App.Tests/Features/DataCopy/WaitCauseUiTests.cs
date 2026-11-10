using Oranda.App.Features.DataCopy;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>
/// 대기 원인(WH1) 화면 쪽: 진행 줄 멈춤 표시 글이 두 언어 모두 원인을 덧붙이고, 데이터 복사 창이 연 세션 쌍이 감시 세션 여는 길(<see cref="CopySessions.OpenMonitor"/>)을
/// 실행기에 넘기며, <see cref="DataCopyServices.OpenMonitorAsync"/> 는 시험 갈고리(<see cref="DataCopyServices.MonitorOpener"/>)를 쓰고 앱 서비스가 없으면 null 이다.
/// 화면 언어(<see cref="LiteralTranslator"/>)는 프로세스에 하나라 언어 묶음에서 따로 돈다.
/// </summary>
[Collection("UiLanguage")]
public sealed class WaitCauseUiTests : IDisposable
{
    private const string Cause = "행 잠금(enq: TX) · 막는 세션 123 (APPUSER@was01, JDBC Thin Client)";

    private readonly IShellText? _previousText = UiText.Current;

    public void Dispose() => UiText.Current = _previousText;

    // ---- 멈춤 표시 글 ----

    [Fact]
    public Task Wait_text_appends_the_cause_in_korean_and_in_english() => HeadlessHost.Run(() =>
    {
        var batch = new CopyWait(CopyWaitSide.Target, 2, TimeSpan.FromSeconds(75)) { Cause = Cause };
        var delete = new CopyWait(CopyWaitSide.Target, null, TimeSpan.FromSeconds(40)) { Step = CopyWaitStep.Delete, Cause = "테이블 잠금(enq: TM)" };
        try
        {
            UseLanguage("ko");
            Assert.Equal("배치 2 쓰는 중 · 2분째 대기 · " + Cause, DataCopyText.WaitText(batch));
            Assert.Equal("대상 정리(DELETE) 중 · 1분째 대기 · 테이블 잠금(enq: TM)", DataCopyText.WaitText(delete));

            UseLanguage("en");
            // 원인 글은 실행기가 만든 그대로 붙는다(DB 이벤트 이름·세션 정보가 대부분이다).
            Assert.Equal("Writing batch 2 · waiting, minute 2 · " + Cause, DataCopyText.WaitText(batch));
            Assert.Equal("Deleting target rows (DELETE) · waiting, minute 1 · 테이블 잠금(enq: TM)", DataCopyText.WaitText(delete));
        }
        finally
        {
            UseLanguage("ko");
        }
    });

    [Fact]
    public Task Wait_text_without_a_cause_is_unchanged_in_both_languages() => HeadlessHost.Run(() =>
    {
        var wait = new CopyWait(CopyWaitSide.Target, 2, TimeSpan.FromSeconds(75)) { Cause = " " };
        try
        {
            UseLanguage("ko");
            Assert.Equal("배치 2 쓰는 중 · 2분째 대기", DataCopyText.WaitText(wait));
            Assert.Equal("배치 2 쓰는 중 · 2분째 대기", DataCopyText.WaitText(wait with { Cause = null }));

            UseLanguage("en");
            Assert.Equal("Writing batch 2 · waiting, minute 2", DataCopyText.WaitText(wait));
            Assert.Equal("Reading source · minute 1", DataCopyText.WaitText(new CopyWait(CopyWaitSide.Source, null, TimeSpan.FromSeconds(20))));
        }
        finally
        {
            UseLanguage("ko");
        }
    });

    // ---- 감시 세션 열기 ----

    [Fact]
    public async Task Open_monitor_uses_the_monitor_opener_hook_when_it_is_set()
    {
        var h = new DataCopyHarness();
        var services = h.Services();

        var monitor = await services.OpenMonitorAsync(h.Target, default);

        Assert.Same(Assert.Single(h.OpenedMonitors), monitor);
        Assert.Equal(h.Target, Assert.Single(h.MonitorOpened));
        Assert.Empty(h.Opened); // 전용 세션 열기(비밀번호를 물을 수 있는 길)는 쓰지 않는다
    }

    [Fact]
    public async Task Open_monitor_without_the_hook_or_an_app_service_provider_returns_null()
    {
        var services = new DataCopyServices();

        Assert.Null(await services.OpenMonitorAsync(TestData.Profile(), default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => services.OpenMonitorAsync(null!, default));
    }

    [Fact]
    public async Task Sessions_the_window_opens_for_a_run_carry_a_monitor_opener_that_opens_with_the_session_profile()
    {
        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(TestJobs.Job("대기 원인", rules: TestJobs.Rule("ORDERS")));
        await vm.GoToPrecheckAsync();

        await vm.StartRunAsync();

        Assert.Equal(1, h.Runner.Calls);
        var sessions = Assert.IsType<CopySessions>(h.Runner.Sessions);
        Assert.NotNull(sessions.OpenMonitor);
        Assert.Empty(h.MonitorOpened); // 여는 길만 넘긴다: 실행기가 대기를 볼 때까지 열지 않는다

        // 실행기는 대상 세션을 넘긴다: 그 세션의 접속(TGT)으로 감시 세션을 연다.
        var monitor = await sessions.OpenMonitor(sessions.Target, default);

        Assert.Equal(sessions.Target.Profile, Assert.Single(h.MonitorOpened));
        Assert.Equal("TGT", h.MonitorOpened[0].Name);
        var opened = Assert.IsType<FakeSession>(monitor);
        Assert.Equal(SessionPurpose.Monitor, opened.Purpose);
        Assert.DoesNotContain(opened, h.OpenedSessions); // 실행 세션 쌍과 따로다
    }

    private static void UseLanguage(string language)
    {
        var text = new ShellText(language: language);
        UiText.Current = text;
        LiteralTranslator.Install(text);
    }
}
