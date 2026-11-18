using System.Runtime.CompilerServices;
using System.Text.Json;
using Oranda.App.Features.TableEdit;
using Oranda.App.Features.TableEdit.Advisor;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using static Oranda.App.Tests.Features.TableEdit.TableEditPartitionSessionTests;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// AI 파티션 추천(설계 2026-11-18): 받는 곳·가림 판단, 요청 모양(구조 결과·기대값·가림 필요), 결과 읽기·앱 확인·채울 모델,
/// 세션의 채우기 → 띠 → 직접 고침 → 되돌리기, 객체 상세 배선(설정 없음 안내 · 확인 창 → AI 대화 창 → 카드로 채우기 · 분포 보기). 가짜 <see cref="IAiDriver"/>·모으기로.
/// </summary>
public class PartitionAdvisorTests
{
    internal static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Unspecified);

    internal static PartitionEvidence Evidence(params ColumnDistribution[] distributions) =>
        PartitionEvidence.Empty("SAMPLE", "ORDERS", Dbms.Oracle, Now) with
        {
            NumRows = 4_812_330,
            Blocks = 90_000,
            LastAnalyzed = Now.AddDays(-77),
            Columns =
            [
                new EvidenceColumn("ORDER_ID", "NUMBER", false, 4_812_330, 0, null, null),
                new EvidenceColumn("STATUS", "VARCHAR2(50)", true, 6, 0, "FREQUENCY", null),
                new EvidenceColumn("ORDER_DATE", "DATE", true, 1_100_000, 154_000, null, null),
            ],
            Indexes = [new EvidenceIndex("PK_ORDERS", true, ["ORDER_ID"], true)],
            Keys = [new EvidenceKey("PK_ORDERS", EvidenceKeyKind.Primary, ["ORDER_ID"])],
            Predicates = [new PredicateUse("ORDER_DATE", 1200, 14, 0.63)],
            Distributions = distributions,
            Skipped = [new EvidenceSkip(EvidenceItem.Predicates, EvidenceSkipReason.NoPrivilege)],
        };

    internal static PartitionCandidate Candidate(int rank, string method = AdviceMethod.Range, string title = "RANGE(ORDER_DATE) 월") =>
        new(rank, title, method, ["ORDER_DATE"], AdviceInterval.Month, new PartitionLayout(AdviceLayoutKind.Monthly, "2025-08"), null, [], ["조회의 63% 가 ORDER_DATE 범위"], []);

    internal static string AdviceJson(string verdict, params PartitionCandidate[] candidates) => JsonSerializer.Serialize(new
    {
        verdict,
        summary = verdict == AdviceVerdict.NoPartition ? "행이 적어 얻을 것이 없습니다" : "월 범위가 좋습니다",
        candidates = candidates.Select(c => new
        {
            rank = c.Rank,
            title = c.Title,
            method = c.Method,
            key_columns = c.KeyColumns,
            interval = c.Interval,
            layout = new { kind = c.Layout.Kind, start = c.Layout.Start },
            local_indexes = c.LocalIndexes,
            reasons = c.Reasons,
            cautions = c.Cautions,
        }),
        not_chosen = new[] { new { column = "STATUS", reason = "값이 6개뿐" } },
    });

    /// <summary>시험 엔진: 프롬프트 = 표 이름 + 값 보내기 여부, 확인 = 순위별 지정, 채우기 = RANGE 줄 둘.</summary>
    internal static AdvisorEngine Engine(Func<PartitionCandidate, IReadOnlyList<AdviceCheck>>? checks = null) => AdvisorEngine.Default with
    {
        BuildPrompt = (e, o) =>
        {
            var vault = new EvidenceValueVault();
            var token = vault.Mask("STATUS", "A");
            return ($"PROMPT {e.Table} goal={o.Goal} values={o.SendValues} require={o.RequireCandidates} {(o.SendValues ? "A" : token)}", vault);
        },
        Check = (c, _, _, _, _) => checks?.Invoke(c) ?? [new AdviceCheck(AdviceCheckLevel.Ok, AdviceCheckCodes.LocalOk, "PK_ORDERS")],
        Apply = (c, _, _, _) => new AdviceDraft(
            new PartitionTableDraft(new PartitionScheme(PartitionMethod.Range, [.. c.KeyColumns]) { Interval = "NUMTOYMINTERVAL(1, 'MONTH')" },
            [
                new PartitionDraft(DraftKeys.New(), "P_202508") { Bound = "DATE '2025-09-01'" },
                new PartitionDraft(DraftKeys.New(), "P_202509") { Bound = "DATE '2025-10-01'" },
            ]),
            null,
            [],
            []),
    };

    internal sealed class FakeDriver : IAiDriver
    {
        public List<AiRequest> Requests { get; } = [];

        public Func<AiRequest, IReadOnlyList<AiEvent>> Answer { get; set; } =
            _ => [new AiFinal("{}", JsonDocument.Parse(AdviceJson(AdviceVerdict.Partition, Candidate(2, title: "B"), Candidate(1))).RootElement.Clone(), null)];

        public AiDriverCapabilities Capabilities { get; } = new(false, false, false, false, true);

        public TaskCompletionSource? Gate { get; set; }

        /// <summary>요청을 받은 때 실행 범위가 이미 폐기돼 있었는가(요청마다 한 칸).</summary>
        public List<bool> RevokedAtStart { get; } = [];

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            RevokedAtStart.Add(request.Session?.IsRevoked ?? false);

            // 실제 드라이버(SystemCliRunner·OpenAiApiDriver·OpencodeDriver)와 같은 전제 조건: 실행 범위가 없거나 폐기됐으면 막는다.
            if (request.Session is null)
            {
                yield return new AiError("AI 요청에 실행 범위(Session)가 없습니다.", "session_required");
                yield break;
            }

            if (request.Session.IsRevoked)
            {
                yield return new AiError("이 AI 요청의 실행 범위가 이미 폐기되었습니다. 새 요청으로 다시 시작하세요.", "session_revoked");
                yield break;
            }

            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }

            foreach (var e in Answer(request))
            {
                yield return e;
            }
        }
    }

    internal sealed class FakeCollector : IPartitionEvidenceCollector
    {
        public PartitionEvidence Evidence { get; set; } = PartitionAdvisorTests.Evidence();

        public List<EvidenceOptions> Calls { get; } = [];

        public Task<PartitionEvidence> CollectAsync(ISession s, DbObjectRef table, EvidenceOptions options, IProgress<EvidenceProgress>? progress, CancellationToken ct)
        {
            Calls.Add(options);
            progress?.Report(new EvidenceProgress(EvidenceStep.ColumnsAndKeys));
            progress?.Report(new EvidenceProgress(EvidenceStep.Distributions, "ORDER_DATE", 1, 1));
            return Task.FromResult(Evidence);
        }
    }

    internal sealed class FakeAudit : IAgentAuditStore
    {
        public List<AgentAuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(AgentAuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<AgentAuditEntry>> ListRecentAsync(int limit, Guid? profileId, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentAuditEntry>>(Entries);
    }

    internal static ConnectionProfile Profile(ConnectionEnvironment env = ConnectionEnvironment.Development, AiAccessLevel? access = null) =>
        new("dev-example", "SAMPLE", "h:1521/s", Id: Guid.NewGuid(), Environment: env, AiAccess: access);

    internal static PartitionAdvisorRun Run(FakeDriver driver, AdvisorRoute? route = null, FakeAudit? audit = null, AdvisorEngine? engine = null, FakeCollector? collector = null) =>
        new(new AdvisorSetup(driver, collector ?? new FakeCollector(), new FakeSession(), Orders, new OracleRules(), Oracle19,
            route ?? AdvisorRoute.For(Profile(access: AiAccessLevel.QueryRows), new AiSettings()))
        {
            Engine = engine ?? Engine(),
            Now = () => Now,
            Audit = audit,
        });

    // ---- 받는 곳·가림 ------------------------------------------------------------

    [Fact]
    public void Route_locks_real_values_on_production_unless_local_ai_and_follows_the_access_level()
    {
        var prod = AdvisorRoute.For(Profile(ConnectionEnvironment.Production), new AiSettings());
        Assert.True(prod.MaskRequired);
        Assert.False(prod.ValuesAllowed);
        Assert.Equal("Claude Code", prod.RecipientText);
        Assert.Equal(new AiProfileExpectation(null), prod.Target.ToExpectation());

        var devPlan = AdvisorRoute.For(Profile(), new AiSettings());
        Assert.False(devPlan.MaskRequired);
        Assert.False(devPlan.ValuesAllowed); // 기본 수준(Plan)은 행 값을 보내지 않는다

        var devRows = AdvisorRoute.For(Profile(access: AiAccessLevel.QueryRows), new AiSettings());
        Assert.True(devRows.ValuesAllowed);

        var masked = AdvisorRoute.For(Profile(access: AiAccessLevel.QueryRows), new AiSettings { MaskLiterals = true });
        Assert.True(masked.MaskRequired);
        Assert.False(masked.ValuesAllowed);
    }

    // ---- 요청·결과 ---------------------------------------------------------------

    [Fact]
    public async Task Ask_sends_the_schema_and_expectation_and_reads_sorted_candidates_with_checks()
    {
        var driver = new FakeDriver();
        var audit = new FakeAudit();
        var route = AdvisorRoute.For(Profile(ConnectionEnvironment.Production), new AiSettings());
        var run = Run(driver, route, audit);

        var outcome = await run.AskAsync(Evidence(), new AdvicePromptOptions(AdviceGoal.Query, SendValues: true, "ko"), CancellationToken.None);

        var request = Assert.Single(driver.Requests);
        Assert.Equal(PartitionAdvice.Schema().GetRawText(), request.OutputSchema!.Value.GetRawText());
        Assert.Equal(route.Target.ToExpectation(), request.Expected);
        Assert.True(request.MaskingRequired);
        var session = Assert.IsType<AgentSession>(request.Session); // 실제 드라이버는 실행 범위가 없으면 session_required 로 막는다
        Assert.Equal(route.Context, session.Context);
        Assert.Equal(route.ConnectionId, session.Profile.Id);
        Assert.True(session.Run.MaskLiteralsRequired); // 라우트의 가림 판정과 같게(운영 접속)
        Assert.Null(session.Run.Literals); // SQL 리터럴 사전은 쓰지 않는다
        Assert.False(Assert.Single(driver.RevokedAtStart));
        Assert.True(session.IsRevoked); // 끝나면 폐기
        Assert.Null(outcome.Error);
        Assert.Contains("values=False", request.Prompt, StringComparison.Ordinal); // 잠긴 값 보내기는 꺼서 보낸다
        Assert.DoesNotContain(" A", request.Prompt, StringComparison.Ordinal);
        Assert.Equal([1, 2], outcome.Candidates.Select(c => c.Candidate.Rank));
        Assert.True(outcome.Candidates[0].CanFill);
        Assert.Equal("값이 6개뿐", Assert.Single(outcome.Advice!.NotChosen).Reason);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(PartitionAdvisorRun.AuditTool, entry.Tool);
        Assert.Contains("table=SAMPLE.ORDERS", entry.Arguments, StringComparison.Ordinal);
        Assert.Contains("values=masked", entry.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("PROMPT", entry.Arguments, StringComparison.Ordinal); // 본문은 남기지 않는다
    }

    [Fact]
    public async Task Ask_sends_a_tracked_unmasked_scope_on_a_dev_route_and_revokes_it_after_an_error()
    {
        var tracked = new List<AgentSession>();
        var driver = new FakeDriver { Answer = _ => [new AiError("profile_changed")] };
        var run = new PartitionAdvisorRun(Run(driver).Setup with { TrackSession = s => { tracked.Add(s); return s; } });

        var outcome = await run.AskAsync(Evidence(), new AdvicePromptOptions(AdviceGoal.Query, false, "ko"), CancellationToken.None);

        var session = Assert.Single(tracked); // 도구 연결(MCP)이 실행 id 로 찾을 수 있게 등록한다(AI 튜닝과 같다)
        Assert.Same(session, Assert.Single(driver.Requests).Session);
        Assert.False(session.Run.MaskLiteralsRequired);
        Assert.Equal("profile_changed", outcome.Error);
        Assert.True(session.IsRevoked);
    }

    [Fact]
    public async Task Ask_revokes_the_scope_when_cancelled_or_when_the_driver_throws()
    {
        var driver = new FakeDriver { Gate = new TaskCompletionSource() };
        using var cts = new CancellationTokenSource();
        var pending = Run(driver).AskAsync(Evidence(), new AdvicePromptOptions(AdviceGoal.Query, false, "ko"), cts.Token);
        await Task.Yield();
        var session = Assert.Single(driver.Requests).Session!;
        Assert.False(session.IsRevoked); // 도는 동안은 살아 있다
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(session.IsRevoked);

        var throwing = new FakeDriver { Answer = _ => throw new InvalidOperationException("boom") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(throwing).AskAsync(Evidence(), new AdvicePromptOptions(AdviceGoal.Query, false, "ko"), CancellationToken.None));
        Assert.True(Assert.Single(throwing.Requests).Session!.IsRevoked);
    }

    [Fact]
    public async Task Ask_keeps_the_raw_text_when_the_answer_cannot_be_read_and_reports_errors()
    {
        var driver = new FakeDriver { Answer = _ => [new AiTextDelta("그냥 글"), new AiFinal(null, null, null)] };
        var outcome = await Run(driver).AskAsync(Evidence(), new AdvicePromptOptions(AdviceGoal.QueryAndPurge, false, "ko"), CancellationToken.None);
        Assert.Null(outcome.Advice);
        Assert.Empty(outcome.Candidates);
        Assert.Equal("그냥 글", outcome.RawText);

        driver.Answer = _ => [new AiError("profile_changed")];
        var failed = await Run(driver).AskAsync(Evidence(), new AdvicePromptOptions(AdviceGoal.QueryAndPurge, false, "ko"), CancellationToken.None);
        Assert.Equal("profile_changed", failed.Error);
        Assert.Null(failed.Advice);
    }

    [Fact]
    public async Task A_stop_check_or_fill_problem_turns_fill_off()
    {
        var engine = Engine(c => c.Rank == 1 ? [new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.IntervalNull, "ORDER_DATE")] : []);
        var outcome = await Run(new FakeDriver(), engine: engine).AskAsync(Evidence(), new AdvicePromptOptions(AdviceGoal.Query, false, "ko"), CancellationToken.None);
        Assert.False(outcome.Candidates[0].CanFill);
        Assert.True(outcome.Candidates[1].CanFill);

        // Core 채우기의 ✖(모르는 방식)도 채우기를 끈다.
        var core = await Run(new FakeDriver { Answer = _ => [new AiFinal(null, JsonDocument.Parse(AdviceJson(AdviceVerdict.Partition, Candidate(1, "weird"))).RootElement.Clone(), null)] },
            engine: Engine() with { Apply = PartitionAdviceApplier.Apply }).AskAsync(Evidence(), new AdvicePromptOptions(AdviceGoal.Query, false, "ko"), CancellationToken.None);
        Assert.False(Assert.Single(core.Candidates).CanFill);
        Assert.Contains(core.Candidates[0].AllChecks, c => c.Code == AdviceCheckCodes.MethodUnknown);
    }

    [Fact]
    public async Task Report_is_one_escaped_html_file_with_evidence_cards_and_the_masking_note()
    {
        var driver = new FakeDriver
        {
            Answer = _ => [new AiFinal("<b>raw</b>", JsonDocument.Parse(AdviceJson(AdviceVerdict.Partition,
                Candidate(1) with { Reasons = ["<script>alert(1)</script> 63%"] })).RootElement.Clone(), null)],
        };
        var outcome = await Run(driver).AskAsync(
            Evidence(new ColumnDistribution("STATUS", DistributionKind.TopValues, [new DistributionBucket("A", 30), new DistributionBucket("B", 10)], 40, null)),
            new AdvicePromptOptions(AdviceGoal.Query, false, "ko"), CancellationToken.None);
        var path = Path.Combine(Path.GetTempPath(), "ort_pa_" + Guid.NewGuid().ToString("N") + ".html");
        var cards = outcome.Candidates.Select(c => new AdviceCandidateCard(c)).ToList();
        try
        {
            var (saved, error) = await PartitionAdviceReport.SaveAsync((_, _) => Task.FromResult<string?>(path), "ORDERS",
                () => PartitionAdviceReport.Render("AI 파티션 추천 — ORDERS", new PartitionEvidenceViewModel(outcome.Evidence, outcome.Vault, false), outcome, cards, Now),
                CancellationToken.None);
            Assert.Null(error);
            Assert.Equal(path, saved);
            var html = await File.ReadAllTextAsync(path);
            Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
            Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; 63%", html, StringComparison.Ordinal);
            Assert.Contains("&lt;b&gt;raw&lt;/b&gt;", html, StringComparison.Ordinal);
            Assert.Contains("실제 값은 AI 에 보내지 않았습니다", html, StringComparison.Ordinal);
            Assert.Contains("A (V1)", html, StringComparison.Ordinal); // 프롬프트와 같은 자리표시
            Assert.Contains("ORDER_DATE", html, StringComparison.Ordinal);
            Assert.Contains("1안 RANGE(ORDER_DATE) 월", html, StringComparison.Ordinal);
            Assert.Contains("STATUS — 값이 6개뿐", html, StringComparison.Ordinal); // not_chosen
        }
        finally
        {
            File.Delete(path);
        }

        // 고르지 않으면 쓰지 않는다
        Assert.Equal((null, null), await PartitionAdviceReport.SaveAsync((_, _) => Task.FromResult<string?>(null), "ORDERS", () => "x", CancellationToken.None));
    }

    // ---- 문구 -------------------------------------------------------------------

    [Fact]
    public void Every_check_code_skip_reason_and_step_has_korean_and_english_text()
    {
        var root = FindRepoRoot();
        var ko = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "src", "Oranda.App", "Resources", "strings-ko.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "src", "Oranda.App", "Resources", "strings-en.json")))!;
        var codes = typeof(AdviceCheckCodes).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(18, codes.Count);
        var keys = codes.Select(c => AdviceCheck.TextKeyPrefix + c)
            .Concat(Enum.GetNames<EvidenceSkipReason>().Select(n => PartitionAdvisorText.SkipKeyPrefix + n))
            .Concat(Enum.GetNames<EvidenceItem>().Select(n => PartitionAdvisorText.ItemKeyPrefix + n))
            .Concat(Enum.GetNames<EvidenceStep>().Select(n => PartitionAdvisorText.StepKeyPrefix + n))
            .Append(PartitionAdvisorText.StepKeyPrefix + "Waiting");
        foreach (var key in keys)
        {
            Assert.True(ko.ContainsKey(key), "ko 없음: " + key);
            Assert.True(en.ContainsKey(key), "en 없음: " + key);
        }
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Oranda.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root");
    }

    // ---- 세션: 채우기 → 띠 → 직접 고침 → 되돌리기 ------------------------------

    private static TableEditSession Plain()
    {
        var s = Create(WithOrderDate(OrdersTable()));
        s.OpenAiAdvisor = () => Task.CompletedTask;
        return s;
    }

    private static AdvisorCandidate FillCandidate(params AdviceCheck[] checks) =>
        new(Candidate(1), checks, Engine().Apply(Candidate(1), Evidence(), new EvidenceValueVault(), DateOnly.FromDateTime(Now)));

    [Fact]
    public void Advisor_buttons_show_only_for_a_plain_table_with_the_action_wired()
    {
        Assert.True(Plain().ShowsAiAdvisor);
        Assert.False(Create(WithOrderDate(OrdersTable())).ShowsAiAdvisor); // 동작이 없으면 숨김
        var partitioned = Create(PartitionedOrders());
        partitioned.OpenAiAdvisor = () => Task.CompletedTask;
        Assert.False(partitioned.ShowsAiAdvisor); // 이미 파티션된 테이블은 범위 밖
    }

    [Fact]
    public void Advisor_row_hides_after_the_table_becomes_partitioned()
    {
        var s = Plain();
        var raised = new List<string?>();
        s.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.True(s.ShowsAiAdvisor);

        s.SetPartitions(PartitionedOrders()); // 바꾸기를 실행하고 다시 읽음

        Assert.False(s.ShowsAiAdvisor);
        Assert.Contains(nameof(TableEditSession.ShowsAiAdvisor), raised);
    }

    [Fact]
    public void Fill_replaces_the_partition_model_and_shows_the_banner_until_reverted()
    {
        var s = Plain();
        var before = s.PartitionDraft;
        var c = FillCandidate(new AdviceCheck(AdviceCheckLevel.Warn, AdviceCheckCodes.PkGlobal, "PK_ORDERS"), new AdviceCheck(AdviceCheckLevel.Ok, AdviceCheckCodes.LocalOk));

        Assert.True(s.ApplyAdvice(c.Candidate, c.Draft, c.Checks));

        Assert.True(s.IsPartitionConversion);
        Assert.Equal(PartitionMethod.Range, s.PartitionDraft!.Scheme!.Method);
        Assert.Equal(["ORDER_DATE"], s.PartitionDraft.Scheme.KeyColumns);
        Assert.Equal(2, s.PartitionRows.Count);
        Assert.Single(s.Script.Statements); // 한 문장 바꾸기(19c)
        Assert.True(s.ShowsAdviceBanner);
        Assert.False(s.IsAdviceEdited);
        Assert.Contains("1안을 채웠습니다", s.AdviceBannerText, StringComparison.Ordinal);
        Assert.Contains("파티션 2줄", s.AdviceBannerText, StringComparison.Ordinal);
        Assert.Single(s.AdviceBannerWarnings); // ⚠ 만 띠에 남는다(✔ 는 아님)

        s.PartitionRows[0].Name = "P_FIRST"; // 사용자가 고침
        Assert.True(s.IsAdviceEdited);
        Assert.Contains("직접 고침", s.AdviceBannerText, StringComparison.Ordinal);

        Assert.True(s.RevertAdvice());
        Assert.Same(before, s.PartitionDraft);
        Assert.False(s.ShowsAdviceBanner);
        Assert.False(s.IsPartitionConversion);
        Assert.Empty(s.Script.Statements);
        Assert.False(s.RevertAdvice()); // 두 번은 없다
    }

    [Fact]
    public void Fill_is_refused_with_a_stop_check_and_on_a_partitioned_table()
    {
        var s = Plain();
        var stop = FillCandidate(new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.KeyMissing));
        Assert.False(s.ApplyAdvice(stop.Candidate, stop.Draft, stop.Checks));
        Assert.False(s.IsPartitionConversion);
        Assert.False(s.ShowsAdviceBanner);

        var partitioned = Create(PartitionedOrders());
        var ok = FillCandidate();
        Assert.False(partitioned.ApplyAdvice(ok.Candidate, ok.Draft, ok.Checks));
    }

    [Fact]
    public void A_second_fill_puts_the_subpartition_template_and_local_indexes_in_the_model_and_reverts_to_the_first_fill()
    {
        var s = Plain();
        var first = FillCandidate();
        Assert.True(s.ApplyAdvice(first.Candidate, first.Draft, first.Checks));
        var afterFirst = s.PartitionDraft;

        var sub = first with
        {
            Draft = first.Draft with
            {
                Subpartition = new SubpartitionAdvice(AdviceMethod.List, ["STATUS"], [new ListGroup("SP_A", ["A"], false), new ListGroup("SP_ETC", [], true)]),
                LocalIndexes = ["IX_ORDERS_01"],
            },
        };
        Assert.True(s.ApplyAdvice(sub.Candidate with { Rank = 2 }, sub.Draft, sub.Checks));

        var template = s.PartitionDraft!.Scheme!.SubpartitionTemplate!;
        Assert.Equal(PartitionMethod.List, template.Method);
        Assert.Equal(["'A'", "DEFAULT"], template.Rows.Select(r => r.Values));
        Assert.Equal(["IX_ORDERS_01"], s.PartitionDraft.LocalIndexes);
        Assert.Contains("SUBPARTITION BY LIST (STATUS)", s.Script.Statements[0].Statement.Text, StringComparison.Ordinal);
        Assert.Contains("IX_ORDERS_01 LOCAL", s.Script.Statements[0].Statement.Text, StringComparison.Ordinal);
        Assert.Contains("2안", s.AdviceBannerText, StringComparison.Ordinal);
        Assert.Contains("하위 LIST(STATUS)", s.AdviceBannerText, StringComparison.Ordinal);
        Assert.Contains("인덱스 1개 LOCAL", s.AdviceBannerText, StringComparison.Ordinal);

        Assert.True(s.RevertAdvice());
        Assert.Same(afterFirst, s.PartitionDraft); // 채우기 직전 = 1안을 채운 모델
    }

    // ---- 객체 상세 배선 --------------------------------------------------------

    internal sealed record Detail(Oranda.App.Features.ObjectDetail.ObjectDetailViewModel Vm, FakeDriver Driver, Oranda.App.Features.Ai.Chat.AiChatViewModel Chat, List<PartitionEvidenceViewModel> Evidence)
    {
        public int ChatShown { get; set; }
    }

    private static async Task<Detail> DetailAsync(AiSettings? settings = null)
    {
        var (vm, meta, _) = CreateDetail();
        meta.Table = WithOrderDate(OrdersTable());
        var driver = new FakeDriver();
        var chat = new Oranda.App.Features.Ai.Chat.AiChatViewModel();
        var detail = new Detail(vm, driver, chat, []);
        vm.AdvisorDriver = driver;
        vm.EvidenceCollector = new FakeCollector();
        vm.AdvisorSettings = () => settings ?? new AiSettings();
        vm.AdvisorEngineOverride = Engine();
        vm.ShowAdvisorStart = async start => await start.SendAsync();
        vm.ShowAdvisorEvidence = detail.Evidence.Add;
        vm.AdvisorChat = () =>
        {
            detail.ChatShown++;
            return Task.FromResult<Oranda.App.Features.Ai.Chat.AiChatViewModel?>(chat);
        };
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        Assert.True(await vm.SetTableEditingAsync(true));
        return detail;
    }

    [Fact]
    public async Task Without_ai_settings_the_advisor_explains_and_offers_settings_instead_of_the_dialog()
    {
        var d = await DetailAsync(new AiSettings { Driver = AiDriverKind.None });
        var vm = d.Vm;
        var started = false;
        vm.ShowAdvisorStart = _ =>
        {
            started = true;
            return Task.CompletedTask;
        };
        string? told = null;
        vm.AdvisorUnavailableNotice = reason =>
        {
            told = reason;
            return Task.FromResult(false);
        };
        Assert.True(vm.TableEdit!.ShowsAiAdvisor);

        await vm.TableEdit.OpenAiAdvisorAsync();

        Assert.False(started);
        Assert.Contains("선택 안 함", told, StringComparison.Ordinal);
        Assert.Empty(d.Driver.Requests);
        Assert.Equal(0, d.ChatShown);
    }

    [Fact]
    public async Task Advisor_flow_streams_into_the_chat_and_fills_the_editor_from_a_card_then_reopens_the_chat_from_the_banner()
    {
        var d = await DetailAsync();
        var edit = d.Vm.TableEdit!;

        await edit.OpenAiAdvisorAsync();

        Assert.Single(d.Driver.Requests);
        Assert.Equal(1, d.ChatShown);
        var conversation = d.Vm.AdviceConversation!;
        Assert.Same(conversation, d.Chat.Owner);
        Assert.Contains("AI 파티션 추천", d.Chat.Title, StringComparison.Ordinal);
        var cards = d.Chat.Entries.OfType<Oranda.App.Features.Ai.Chat.ChatCardEntry>().Select(e => e.Card).OfType<AdviceCandidateCard>().ToList();
        Assert.Equal(2, cards.Count);
        Assert.Single(d.Chat.Entries.OfType<Oranda.App.Features.Ai.Chat.ChatCardEntry>(), e => e.Card is AdviceResultCard);
        Assert.False(d.Chat.IsRunning);

        Assert.True(cards[0].Fill());
        Assert.True(cards[0].IsFilled);
        Assert.True(edit.IsPartitionConversion);
        Assert.True(edit.ShowsAdviceBanner);
        Assert.Contains(d.Chat.Entries.OfType<Oranda.App.Features.Ai.Chat.ChatAssistantEntry>(), e => e.Text.Contains("1안을 파티션 편집기에 채웠습니다", StringComparison.Ordinal));

        await edit.ReopenAiAdviceAsync();
        Assert.Equal(2, d.ChatShown); // 「추천 다시 보기」 = 대화 창을 보인다

        Assert.False(await d.Vm.SetTableEditingAsync(false)); // 편집을 끄면(버리기 확인 = 예) 대화는 잊는다
        Assert.Null(d.Vm.AdviceConversation);
        Assert.False(cards[1].Fill()); // 끈 편집에는 채우지 않는다
    }

    [Fact]
    public async Task Advisor_does_not_start_while_another_ai_task_runs_in_the_chat()
    {
        var d = await DetailAsync();
        d.Chat.BeginTurn("다른 일"); // 튜닝 등이 도는 중

        await d.Vm.TableEdit!.OpenAiAdvisorAsync();

        Assert.Empty(d.Driver.Requests);
        Assert.Null(d.Vm.AdviceConversation);
        Assert.Contains(d.Chat.Entries.OfType<Oranda.App.Features.Ai.Chat.ChatAssistantEntry>(), e => e.Text.Contains("다른 작업이 진행 중", StringComparison.Ordinal));
    }

    private sealed class FakeDateLikeProvider : IPartitionEvidenceProvider
    {
        public List<string> Asked { get; } = [];

        public Func<string, DateLike?> Answer { get; set; } = c => c switch
        {
            "WORK_YMD" => new DateLike(DateLikeFormat.Yyyymmdd, 1.0, 500),
            "WORK_YM" => new DateLike(DateLikeFormat.Yyyymm, 0.9, 500), // 95% 미만 = 넣지 않음
            _ => null,
        };

        public Task<IReadOnlyList<PredicateUse>?> GetPredicateUseAsync(ISession s, string owner, string table, CancellationToken ct) => Task.FromResult<IReadOnlyList<PredicateUse>?>(null);

        public Task<DateLike?> DetectDateLikeAsync(ISession s, string owner, string table, string column, CancellationToken ct)
        {
            Asked.Add(column);
            return Task.FromResult(Answer(column));
        }

        public Task<ColumnDistribution?> SampleMonthlyAsync(ISession s, string owner, string table, string column, DateLikeFormat? format, double? samplePercent, CancellationToken ct) =>
            Task.FromResult<ColumnDistribution?>(null);

        public Task<ColumnDistribution?> SampleTopValuesAsync(ISession s, string owner, string table, string column, int top, double? samplePercent, CancellationToken ct) =>
            Task.FromResult<ColumnDistribution?>(null);
    }

    private static TableInfo WithDateLikeColumns() => OrdersTable() with
    {
        Columns =
        [
            .. OrdersTable().Columns,
            new ColumnInfo("WORK_YMD", 5, "VARCHAR2") { Length = 8, CharSemantics = CharSemantics.Bytes },
            new ColumnInfo("WORK_YM", 6, "NUMBER") { Precision = 6 },
        ],
    };

    [Fact]
    public async Task Turning_editing_on_detects_date_like_columns_for_the_easy_setup()
    {
        var (vm, meta, _) = CreateDetail();
        meta.Table = WithDateLikeColumns();
        var provider = new FakeDateLikeProvider();
        vm.DateLikeProvider = provider;
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);

        Assert.True(await vm.SetTableEditingAsync(true));
        await vm.DateLikeDetection;

        Assert.Equal(["CUSTOMER_ID", "WORK_YMD", "WORK_YM"], provider.Asked); // 8자리 정수·8자 글·6자리 정수만(ORDER_ID 10자리·STATUS 50자·OLD_FLAG 1자는 아님)
        Assert.Contains("WORK_YMD", vm.TableEdit!.EasyDateColumns);
        Assert.DoesNotContain("WORK_YM", vm.TableEdit.EasyDateColumns);
    }

    [Fact]
    public async Task Date_like_detection_stops_quietly_on_a_provider_error()
    {
        var (vm, meta, _) = CreateDetail();
        meta.Table = WithDateLikeColumns();
        var provider = new FakeDateLikeProvider { Answer = _ => throw new InvalidOperationException("ORA-01031") };
        vm.DateLikeProvider = provider;
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);

        Assert.True(await vm.SetTableEditingAsync(true));
        await vm.DateLikeDetection;

        Assert.Single(provider.Asked); // 첫 오류에서 그만둔다
        Assert.DoesNotContain("WORK_YMD", vm.TableEdit!.EasyDateColumns);
    }

    [Fact]
    public async Task Evidence_view_collects_without_ai_and_opens_an_evidence_only_window()
    {
        var d = await DetailAsync();

        await d.Vm.TableEdit!.OpenPartitionEvidenceAsync();

        Assert.Empty(d.Driver.Requests);
        Assert.Equal(0, d.ChatShown); // 근거만: AI 대화 창을 열지 않는다
        var evidence = Assert.Single(d.Evidence);
        Assert.Equal(3, evidence.Columns.Count);
        Assert.Contains("PK", evidence.Columns[0].Usage, StringComparison.Ordinal);
        Assert.Contains("63%", evidence.Columns[2].Usage, StringComparison.Ordinal);
        Assert.True(evidence.CanSaveReport);
        Assert.Null(d.Vm.AdviceConversation);
    }
}

/// <summary>앱 확인·건너뜀 문구(글자 입구는 전역이라 앱 정적 상태 묶음에서 한국어로 고정).</summary>
[Collection("GridEditStatics")]
public sealed class PartitionAdvisorTextTests : IDisposable
{
    private readonly Oranda.App.Shell.IShellText? _previous = Oranda.App.Features.Localization.UiText.Current;

    public PartitionAdvisorTextTests() => Oranda.App.Features.Localization.UiText.Current = new Oranda.App.Shell.ShellText(language: "ko");

    public void Dispose() => Oranda.App.Features.Localization.UiText.Current = _previous;

    [Fact]
    public void Check_text_puts_the_subject_first_and_args_in_brackets()
    {
        Assert.Equal("ORDER_DATE: 키 열이 테이블에 없거나 지운 열·가상 열입니다",
            PartitionAdvisorText.Check(new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.KeyMissing, "ORDER_DATE")));
        Assert.Equal("행 수가 100만 미만이거나 블록이 1만 미만이라 파티션 이득이 작습니다 (행 1,240 · 블록 8)",
            PartitionAdvisorText.Check(new AdviceCheck(AdviceCheckLevel.Warn, AdviceCheckCodes.SmallTable, Args: ["1240", "8"])));
        Assert.Equal("ORDER_DATE: INTERVAL 인데 키 열에 NULL 행이 있습니다(ORA-14300). NULL 을 먼저 채우거나 NOT NULL 로 바꾸세요 (NULL 154,000건)",
            PartitionAdvisorText.Check(new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.IntervalNull, "ORDER_DATE", ["154000"])));
        Assert.Equal("키 열 형식이 이 방식에 맞지 않습니다(HASH 밖의 LOB·LONG, INTERVAL 인데 DATE·TIMESTAMP·NUMBER 아님)",
            PartitionAdvisorText.Check(new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.KeyType))); // 채우기 Problems 는 Args 가 없다
        Assert.Equal("통계 마지막 수집이 30일을 넘었습니다(근거 숫자가 오래됨)",
            PartitionAdvisorText.Check(new AdviceCheck(AdviceCheckLevel.Warn, AdviceCheckCodes.StatsOld, Args: []))); // 수집한 적 없음
        Assert.StartsWith("이 서버는 한 문장으로 바꾸지 못해",
            PartitionAdvisorText.Check(new AdviceCheck(AdviceCheckLevel.Warn, AdviceCheckCodes.Dialect, Args: ["rebuild"])), StringComparison.Ordinal);
        Assert.Equal("new-code", PartitionAdvisorText.Check(new AdviceCheck(AdviceCheckLevel.Ok, "new-code"))); // 모르는 code 는 그대로
        Assert.Equal("✖", PartitionAdvisorText.Mark(AdviceCheckLevel.Stop));
        Assert.Equal("조회 조건 · STATUS — 건너뜀(권한 없음) (ORA-01031)",
            PartitionAdvisorText.Skip(new EvidenceSkip(EvidenceItem.Predicates, EvidenceSkipReason.NoPrivilege, "STATUS", "ORA-01031")));
    }

    [Fact]
    public void Check_text_reads_one_language_even_when_the_literal_translator_is_english()
    {
        // 다른 시험(셸)이 화면 글자 번역기를 영어로 남겨도 괄호 속 인수 문구가 키 글자와 같은 언어를 따른다(CI 흔들림 원인).
        Oranda.App.Features.Localization.LiteralTranslator.Install(new Oranda.App.Shell.ShellText(language: "en"));
        try
        {
            Assert.Equal("행 수가 100만 미만이거나 블록이 1만 미만이라 파티션 이득이 작습니다 (행 1,240 · 블록 8)",
                PartitionAdvisorText.Check(new AdviceCheck(AdviceCheckLevel.Warn, AdviceCheckCodes.SmallTable, Args: ["1240", "8"])));
        }
        finally
        {
            Oranda.App.Features.Localization.LiteralTranslator.Install(new Oranda.App.Shell.ShellText(language: "ko"));
        }
    }
}
