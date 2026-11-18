using System.Globalization;
using System.Text;
using System.Text.Json;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.Localization;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;

namespace Oranda.App.Features.TableEdit.Advisor;

/// <summary>
/// 받는 곳과 가림 판단(확인 창을 열 때 한 번 정한다). AI 튜닝과 같은 규칙: 받는 곳 = 설정의 활성 대상(<see cref="AiConversationTarget"/>),
/// 라우터 기대값 = <see cref="AiConversationTarget.ToExpectation"/>, 가림 = <see cref="AiAccessPolicy.MaskLiterals(ConnectionProfile, bool, bool)"/>.
/// </summary>
/// <param name="MaskRequired">이 접속의 글을 가려 보내야 한다(<see cref="AiRequest.MaskingRequired"/>, 운영 표시 또는 가림 설정이고 로컬 AI 가 아님)</param>
/// <param name="ValuesLockedReason">실제 값 보내기(PA-3)를 켤 수 없는 까닭. null = 켤 수 있음</param>
/// <param name="Profile">이 접속(요청의 실행 범위 <see cref="AgentRunScope.Profile"/> 에 싣는다)</param>
public sealed record AdvisorRoute(AiConversationTarget Target, AiContext Context, Guid? ConnectionId, bool MaskRequired, string? ValuesLockedReason, ConnectionProfile Profile)
{
    /// <summary>실제 값 보내기를 켤 수 있다.</summary>
    public bool ValuesAllowed => ValuesLockedReason is null;

    /// <summary>받는 곳 한 줄(이름 · 주소).</summary>
    public string RecipientText => Target.RecipientUrl is { } url ? Target.Label + " · " + url : Target.Label;

    /// <summary>
    /// 접속·설정으로 만든다. 값 보내기 잠금 = 가림 대상 접속(PA-3) 또는 접속의 AI 허용 수준이 행 값까지가 아님
    /// (AI 튜닝이 행 값을 보내는 규칙과 같게 — 로컬 AI 면 <see cref="AiAccessPolicy.DataLevel"/>).
    /// </summary>
    public static AdvisorRoute For(ConnectionProfile profile, AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);
        var target = AiConversationTarget.From(settings);
        var level = AiAccessPolicy.Effective(profile);
        var context = new AiContext(profile.Name, profile.Environment, level, LocalAi: target.LocalAi);
        var mask = AiAccessPolicy.MaskLiterals(profile, settings.MaskLiterals, target.LocalAi);
        string? locked = null;
        if (mask)
        {
            locked = profile.Environment == ConnectionEnvironment.Production
                ? TableEditText.T("운영 접속이라 로컬 AI 가 아니면 실제 값을 보낼 수 없습니다", "This is a production connection: real values go only to a local AI")
                : TableEditText.T("설정에서 값 가림이 켜져 있어 로컬 AI 가 아니면 실제 값을 보낼 수 없습니다", "Value masking is on in the settings: real values go only to a local AI");
        }
        else if (AiAccessPolicy.DataLevel(level, target.LocalAi) < AiAccessLevel.QueryRows)
        {
            locked = TableEditText.T("이 접속의 AI 허용 수준이 「행 값까지」가 아니라 실제 값을 보낼 수 없습니다", "This connection's AI access level does not allow row values");
        }

        return new AdvisorRoute(target, context, profile.Id, mask, locked, profile);
    }
}

/// <summary>후보 하나와 앱 확인·채울 모델.</summary>
public sealed record AdvisorCandidate(PartitionCandidate Candidate, IReadOnlyList<AdviceCheck> Checks, AdviceDraft Draft)
{
    /// <summary>확인 + 채우기 문제(표 순서 그대로, 채우기 문제는 뒤). 같은 (수준, code, 대상)은 한 번만.</summary>
    public IReadOnlyList<AdviceCheck> AllChecks => [.. Checks.Concat(Draft.Problems).DistinctBy(c => (c.Level, c.Code, c.Subject))];

    /// <summary>✖ 가 없고 채울 방식이 있다(「편집기에 채우기」를 켬).</summary>
    public bool CanFill => Draft.Draft.Scheme is not null && PartitionAdviceChecker.CanFill(AllChecks);
}

/// <summary>한 번 받은 결과.</summary>
/// <param name="Advice">읽은 추천. null = 원문만(못 읽음) 또는 실패</param>
/// <param name="RawText">AI 응답 글 그대로</param>
/// <param name="Error">AI 실패 글(없으면 null)</param>
public sealed record AdvisorOutcome(
    PartitionEvidence Evidence,
    AdvicePromptOptions Options,
    string Prompt,
    EvidenceValueVault Vault,
    PartitionAdvice? Advice,
    IReadOnlyList<AdvisorCandidate> Candidates,
    string RawText,
    string? Error,
    DateTime ReceivedAt);

/// <summary>Core 함수 묶음(시험이 바꾼다). 기본 = Core 의 프롬프트·파서·확인·채우기.</summary>
public sealed record AdvisorEngine(
    Func<PartitionEvidence, AdvicePromptOptions, (string Prompt, EvidenceValueVault Vault)> BuildPrompt,
    Func<JsonElement?, string?, PartitionAdvice?> Parse,
    Func<PartitionCandidate, PartitionEvidence, ITableEditDialect, ServerInfo?, EvidenceValueVault?, IReadOnlyList<AdviceCheck>> Check,
    Func<PartitionCandidate, PartitionEvidence, EvidenceValueVault, DateOnly, AdviceDraft> Apply)
{
    public static AdvisorEngine Default { get; } = new(
        PartitionAdvicePrompt.Build,
        PartitionAdviceParser.Parse,
        (c, e, d, s, v) => PartitionAdviceChecker.Check(c, e, d, s, v),
        PartitionAdviceApplier.Apply);
}

/// <summary>추천 한 표의 재료(객체 상세가 채운다).</summary>
public sealed record AdvisorSetup(
    IAiDriver Driver,
    IPartitionEvidenceCollector Collector,
    ISession Session,
    DbObjectRef Table,
    ITableEditDialect Dialect,
    ServerInfo? Server,
    AdvisorRoute Route)
{
    public AdvisorEngine Engine { get; init; } = AdvisorEngine.Default;

    /// <summary>「오늘」(채우기 기준).</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>감사 기록 저장소(없으면 남기지 않음).</summary>
    public IAgentAuditStore? Audit { get; init; }

    /// <summary>
    /// 새 실행 범위를 등록한다(앱 = <see cref="Oranda.App.Features.Ai.Agent.AppAgentSessionSource.Track"/>, 도구 연결이 실행 id 로 찾는다). null = 등록하지 않음.
    /// </summary>
    public Func<AgentSession, AgentSession>? TrackSession { get; init; }
}

/// <summary>
/// 근거 모으기 → 프롬프트 → <see cref="IAiDriver"/> → 파서 → 앱 확인·채울 모델(설계 3장 ②~③). 실행은 SELECT(모으기)와 AI 호출뿐이다 — DB 에 아무것도 바꾸지 않는다.
/// 드라이버가 실행 범위를 꼭 요구하므로 AI 튜닝과 같게 <see cref="AiRequest.Session"/> 을 만들어 싣고, 요청이 끝나면(성공·실패·취소) 폐기한다.
/// 근거는 이미 글에 있으므로 프롬프트가 도구를 부르지 말라고 적는다(PA-7). 게이트·허용 수준은 튜닝과 같다.
/// </summary>
public sealed class PartitionAdvisorRun
{
    /// <summary>감사 기록의 합성 도구 이름.</summary>
    public const string AuditTool = "partition_advice_send";

    public PartitionAdvisorRun(AdvisorSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        Setup = setup;
    }

    public AdvisorSetup Setup { get; }

    public AdvisorRoute Route => Setup.Route;

    /// <summary>근거를 모은다(취소 = <see cref="OperationCanceledException"/>).</summary>
    public Task<PartitionEvidence> CollectAsync(EvidenceOptions options, IProgress<EvidenceProgress>? progress, CancellationToken ct) =>
        Setup.Collector.CollectAsync(Setup.Session, Setup.Table, options, progress, ct);

    /// <summary>값 보내기는 잠겨 있으면 끈다(창이 켠 값을 그대로 믿지 않는다).</summary>
    public AdvicePromptOptions Normalize(AdvicePromptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.SendValues && !Route.ValuesAllowed ? options with { SendValues = false } : options;
    }

    /// <summary>보낼 프롬프트(「보낼 내용 보기」와 실제 전송이 같은 함수를 쓴다).</summary>
    public (string Prompt, EvidenceValueVault Vault) BuildPrompt(PartitionEvidence evidence, AdvicePromptOptions options) =>
        Setup.Engine.BuildPrompt(evidence, Normalize(options));

    /// <summary>화면 언어(프롬프트 언어).</summary>
    public static string Language => LiteralTranslator.IsEnglish ? "en" : "ko";

    /// <summary>
    /// AI 에 보내고 결과를 읽는다. 실패는 <see cref="AdvisorOutcome.Error"/> 로, 취소는 <see cref="OperationCanceledException"/> 으로 끝난다.
    /// 보내기 전에 감사 기록을 남긴다(실패해도 보내기를 막지 않는다 — 게이트의 감사와 같은 규칙).
    /// </summary>
    public async Task<AdvisorOutcome> AskAsync(PartitionEvidence evidence, AdvicePromptOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        options = Normalize(options);
        var (prompt, vault) = Setup.Engine.BuildPrompt(evidence, options);
        await AuditAsync(prompt, vault, options, ct).ConfigureAwait(true);

        var session = NewSession();
        var request = new AiRequest(
            prompt,
            Route.Context,
            OutputSchema: PartitionAdvice.Schema(),
            Session: session,
            Expected: Route.Target.ToExpectation(), // 확인 창에 적은 곳과 실제 전송처가 같게(profile_changed)
            MaskingRequired: Route.MaskRequired); // 값을 가려 보낼 수 없는 기타 CLI 의 라우터만 읽는다
        var text = new StringBuilder();
        string? finalText = null;
        JsonElement? result = null;
        string? error = null;
        try
        {
            await foreach (var e in Setup.Driver.RunAsync(request, ct).WithCancellation(ct).ConfigureAwait(true))
            {
                switch (e)
                {
                    case AiTextDelta delta:
                        text.Append(delta.Text);
                        break;
                    case AiFinal final:
                        finalText = final.Text;
                        result = final.Result;
                        break;
                    case AiError fail:
                        error = AiErrorText.Redact(fail.Message);
                        break;
                }
            }
        }
        finally
        {
            session.Revoke(); // 성공·실패·취소 모두: 실행 범위를 남기지 않는다(튜닝과 같은 수명)
        }

        ct.ThrowIfCancellationRequested();
        var raw = string.IsNullOrEmpty(finalText) ? text.ToString() : finalText;
        var advice = error is null || result is not null || raw.Length > 0 ? Setup.Engine.Parse(result, raw) : null;
        var today = DateOnly.FromDateTime(Setup.Now());
        IReadOnlyList<AdvisorCandidate> candidates = advice is null
            ? []
            : [.. advice.Candidates.OrderBy(c => c.Rank).Select(c => new AdvisorCandidate(
                c,
                SafeCheck(c, evidence, vault),
                SafeApply(c, evidence, vault, today)))];
        return new AdvisorOutcome(evidence, options, prompt, vault, advice, candidates, raw, error, Setup.Now());
    }

    /// <summary>
    /// 이 요청 한 번의 실행 범위(AI 튜닝의 <c>NewSession</c> 과 같은 모양). SQL 리터럴 사전은 없고, 가림 필요는 라우트의 판정과 같다 —
    /// 가려야 하는데 사전이 없으므로 게이트가 SQL 을 다루는 도구를 막는다.
    /// </summary>
    private AgentSession NewSession()
    {
        var session = new AgentSession(new AgentRunScope(Guid.NewGuid().ToString("N"), Route.Profile, Route.Context)
        {
            MaskLiteralsRequired = Route.MaskRequired,
        });
        return Setup.TrackSession?.Invoke(session) ?? session;
    }

    private IReadOnlyList<AdviceCheck> SafeCheck(PartitionCandidate c, PartitionEvidence evidence, EvidenceValueVault vault)
    {
        try
        {
            return Setup.Engine.Check(c, evidence, Setup.Dialect, Setup.Server, vault); // 금고 = LIST 자리표시자를 되돌려 본다
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or FormatException)
        {
            return [new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.MethodUnknown, Args: [ex.Message])];
        }
    }

    private AdviceDraft SafeApply(PartitionCandidate c, PartitionEvidence evidence, EvidenceValueVault vault, DateOnly today)
    {
        try
        {
            return Setup.Engine.Apply(c, evidence, vault, today);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or FormatException)
        {
            return new AdviceDraft(Oranda.Core.Schema.TableEdit.Partitions.PartitionTableDraft.None, null, [],
                [new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.MethodUnknown, Args: [ex.Message])]);
        }
    }

    /// <summary>
    /// 감사 한 줄: 표 이름·글자 수·값 가림 여부·받는 곳만(프롬프트 본문·값은 담지 않는다). <c>Reason</c> = 실제 값을 보냈으면 <c>values_sent</c>.
    /// </summary>
    public static AgentAuditEntry BuildAuditEntry(AdvisorRoute route, DbObjectRef table, string prompt, EvidenceValueVault vault, AdvicePromptOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(options);
        return new AgentAuditEntry
        {
            OccurredAt = now,
            ProfileId = route.ConnectionId,
            ProfileName = route.Context.ConnectionName,
            Client = "oranda-partition-advisor",
            RunId = Guid.NewGuid().ToString("N"),
            Tool = AuditTool,
            Arguments = string.Create(CultureInfo.InvariantCulture,
                $"table={table.Owner}.{table.Name}; chars={prompt.Length}; values={(options.SendValues ? "sent" : "masked")}; placeholders={vault.Count}; to={route.Target.RecipientUrl ?? route.Target.Label}"),
            AccessLevel = route.Context.AccessLevel,
            Verdict = AiToolJudgement.Allowed,
            Reason = options.SendValues ? "values_sent" : null,
            ResultChars = prompt.Length,
        };
    }

    private async Task AuditAsync(string prompt, EvidenceValueVault vault, AdvicePromptOptions options, CancellationToken ct)
    {
        if (Setup.Audit is not { } store)
        {
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await store.AppendAsync(BuildAuditEntry(Route, Setup.Table, prompt, vault, options, DateTimeOffset.UtcNow), timeout.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceWarning("파티션 추천 감사 기록 실패: {0}", AiErrorText.Redact(ex.GetType().Name));
        }
    }
}
