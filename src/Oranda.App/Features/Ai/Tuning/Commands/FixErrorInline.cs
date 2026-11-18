using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.Agent;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Extensibility;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Core.Text;

namespace Oranda.App.Features.Ai.Tuning.Commands;

/// <summary>받아들이기에 되돌려 받는 값: 고칠 대상과 가린 SQL 의 사전.</summary>
internal sealed record FixErrorInlineState(AiCommandTarget Target, LiteralVault? Vault);

/// <summary>
/// 「오류 고치기」 바로 처리(설계 §2.1 「(기존) SQL 오류 고치기 = 바로」, TSK-15-04): 채팅창을 열지 않고 SQL 편집기 아래 제안 막대(받아들이기가 붙은 AI 띠)에 답한다.
/// 가벼운 일(<see cref="AiTier.Light"/>) 한 번짜리 요청이라 도구를 쓰지 않는다 — 오류(가리는 대화면 번호·가린 글 기준 위치만)와 문장을 꾸러미로 한 번에 보낸다.
/// 대상·대조 규칙은 <see cref="FixErrorCommand.PrepareAsync"/> 그대로다. 「받아들이기」 는 고친 문장을 그 탭 그 자리에 기존 차이 미리보기(<see cref="IEditorContext.ProposeEditAsync"/>)로 넣고,
/// 까닭 한 줄을 미리보기 제목과 막대에 적는다. 실행·컴파일은 사람이 한다.
/// </summary>
/// <param name="command">대상 고르기·오류 줄·조인 규칙.</param>
/// <param name="tabs">받아들이기 직전 대조(같은 탭이 활성 · 그 자리 문장이 그대로).</param>
/// <param name="masker">가린 SQL 되돌리기. 가림 대상인데 없으면 문장 칸이 빠져 보내지 않는다.</param>
public sealed partial class FixErrorInline(FixErrorCommand command, IEditorTabs tabs, ISqlLiteralMasker? masker)
{
    /// <summary>막대 이름.</summary>
    public const string Title = "오류 고치기";

    /// <summary>감사 기록의 기능 이름.</summary>
    public const string Feature = "fix_error";

    /// <summary>답에 고친 SQL 이 없을 때.</summary>
    public const string NoSqlText = "AI 답에 고친 문장이 없어 넣지 않았습니다. 「다시」 로 다시 물어보세요.";

    /// <summary>답의 자리표시를 되돌릴 수 없을 때.</summary>
    public const string UnknownPlaceholderText = "AI 답에 모르는 자리표시가 있어 넣지 않았습니다(가린 값을 되돌릴 수 없습니다).";

    /// <summary>받아들이는 사이 다른 탭으로 옮겼을 때.</summary>
    public const string OtherTabText = "오류가 난 탭이 지금 탭이 아니라 넣지 않았습니다. 그 탭을 연 뒤 받아들이세요.";

    /// <summary>답을 받는 사이 문장이 바뀌었을 때.</summary>
    public const string ChangedText = "답을 받는 사이 문장이 바뀌어 넣지 않았습니다. 문장을 다시 실행해 오류를 확인한 뒤 시도하세요.";

    /// <summary>고친 문장이 원래와 같을 때.</summary>
    public const string SameText = "AI 가 바꾼 곳이 없어 넣지 않았습니다.";

    /// <summary>차이 미리보기에서 거절했을 때.</summary>
    public const string RejectedText = "적용하지 않았습니다.";

    /// <summary>AI 를 쓸 수 없는 상태(서비스·보이는 접속 없음).</summary>
    public const string UnavailableText = "AI 를 쓸 수 없는 상태라 보내지 않았습니다(접속 또는 AI 설정을 확인하세요).";

    /// <summary>답 틀: 까닭 한 줄과 고친 문장.</summary>
    public static AiAnswerTemplate Template { get; } = new([
        new AiAnswerSection("원인", "one line: the error code in bold and the cause in a few words, for example **ORA-00942 · 테이블 이름 오타**"),
        new AiAnswerSection("고친 SQL", "the whole corrected statement in one ```sql code block and nothing else"),
    ]);

    /// <summary>
    /// 막대 요청을 만든다: 대상 고르기(<see cref="FixErrorCommand.PrepareAsync"/>) → 꾸러미(오류 줄 · 문장, 가림 그대로) → 지시 글·답 틀.
    /// </summary>
    /// <param name="env">시작 환경(접속·편집기·활성 탭).</param>
    /// <param name="runFor">추정 입력 토큰으로 요청 경로를 만든다(<see cref="AiKitRoute.For"/>: 크기 올림은 대상을 고를 때 정한다).</param>
    /// <param name="joinSyntax">설정의 조인 문법.</param>
    public async Task<AiStripPreparation> PrepareAsync(AiCommandStartEnv env, Func<int, AiOneShotRun> runFor, AiJoinSyntax joinSyntax, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(runFor);
        if (AiAccessPolicy.Effective(env.Profile) == AiAccessLevel.Off)
        {
            return AiStripPreparation.Refuse(AiTuningFlow.CommandAccessOffText);
        }

        var preparation = await command.PrepareAsync(env, ct).ConfigureAwait(true);
        if (preparation.Target is not { } target)
        {
            return AiStripPreparation.Refuse(preparation.RefusalText ?? AiTuningLauncher.CommandStartFailedText);
        }

        // 받는 곳(가림·확인·기대값)은 보낼 글의 크기로 고른다: 가벼운 일 모델 한도의 70% 를 넘으면 주 모델(설계 §1.2 「크기 올림」).
        var run = runFor(AiKitRoute.EstimateFor(target.Statement.Text, (target.State as FixErrorState)?.Message));
        var route = run.Route;

        // 가린 글 기준 오류 위치를 쓰려면 가린 문장이 먼저 있어야 한다: 문장만 한 번 가려 보고(같은 가림이라 결과가 같다) 오류 줄을 만든 뒤 꾸러미를 짓는다.
        var probe = route.Bundle(Title, masker).Add("statement", "오류가 난 문장", target.Statement.Text, AiAccessLevel.Metadata, AiContextKind.Sql).Build();
        if (probe.Items.FirstOrDefault(i => i.Key == "statement") is not { } masked)
        {
            return AiStripPreparation.Refuse(probe.Omitted.Any(o => o.Reason == AiContextOmitReason.MaskUnavailable)
                ? AiTuningFlow.CommandMaskerMissingText
                : command.MaskFailedText);
        }

        var input = new AiCommandPromptInput(target, masked.Text, probe.Vault, masker, route.Context);
        var state = target.State as FixErrorState ?? new FixErrorState(0, null, null, null, null);
        var errorText = string.Join("\n", FixErrorCommand.ErrorLines(state, input));
        var bundle = route.Bundle(Title, masker)
            .Add("error", "오류", errorText, AiAccessLevel.Metadata)
            .Add("statement", "오류가 난 문장", target.Statement.Text, AiAccessLevel.Metadata, AiContextKind.Sql)
            .Build();
        return AiStripPreparation.Ready(new AiStripRequest(run, bundle, Instruction(joinSyntax), Template, new FixErrorInlineState(target, bundle.Vault)));
    }

    /// <summary>지시 글: 도구 없이 오류와 문장만으로 가장 작게 고친다.</summary>
    internal string Instruction(AiJoinSyntax joinSyntax) =>
        "Fix this Oracle SQL statement, which failed when it was run. The error and the statement are in the context blocks below. "
        + "You have no tools and cannot read the database or run anything, so do not guess table or column names you cannot see; "
        + "if the information you need is missing, say so in the cause line and repeat the statement unchanged. "
        + "Make the smallest change that removes the error. "
        + command.JoinRule(joinSyntax) + " "
        + AiTuningFlow.KeepCommentsRule;

    /// <summary>
    /// 「받아들이기」: 고친 문장을 되돌리고(가린 값) 대조한 뒤 그 자리에 차이 미리보기로 넣는다. 넣었으면 null, 아니면 까닭.
    /// </summary>
    /// <param name="accept">띠가 넘긴 요청·결과·답.</param>
    /// <param name="propose">편집기 제안(<see cref="IEditorContext.ProposeEditAsync"/>, 받아들이면 true).</param>
    public async Task<string?> AcceptAsync(AiStripAccept accept, Func<TextEdit, CancellationToken, Task<bool>> propose, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentNullException.ThrowIfNull(propose);
        if (accept.Request.State is not FixErrorInlineState { Target: var target } state || ExtractSql(accept.Answer?["고친 SQL"]?.Body) is not { } sql)
        {
            return NoSqlText;
        }

        var restored = sql;
        if (state.Vault is { } vault)
        {
            if (masker is null)
            {
                return UnknownPlaceholderText;
            }

            var back = masker.Restore(sql, vault);
            if (!back.Succeeded)
            {
                return UnknownPlaceholderText;
            }

            restored = back.Text;
        }

        var original = target.Statement;
        restored = MatchTerminator(original.Text, restored);
        if (target.TabId is not { } tab || tabs.ActiveTabId != tab)
        {
            return OtherTabText;
        }

        if (tabs.GetStatementAt(tab, original.Range.Start) is not { } current || current.Text != original.Text || current.Range != original.Range)
        {
            return ChangedText;
        }

        if (string.Equals(restored.Trim(), original.Text.Trim(), StringComparison.Ordinal))
        {
            return SameText;
        }

        var reason = ReasonLine(accept.Answer);
        var edit = new TextEdit(original.Range, restored) { Description = reason.Length == 0 ? "AI 오류 고치기" : "AI 오류 고치기 — " + reason };
        return await propose(edit, ct).ConfigureAwait(true) ? null : RejectedText;
    }

    /// <summary>답 칸에서 SQL 을 꺼낸다: 코드 블록이 있으면 첫 블록, 없으면 글 그대로. 비면 null.</summary>
    internal static string? ExtractSql(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var block = CodeBlock().Match(body);
        var sql = (block.Success ? block.Groups["code"].Value : body).Trim();
        return sql.Length == 0 ? null : sql;
    }

    /// <summary>까닭 한 줄(굵은 글 표시를 뺀 첫 줄).</summary>
    internal static string ReasonLine(AiTemplatedAnswer? answer) =>
        (answer?["원인"]?.Body ?? string.Empty).Split('\n').Select(l => l.Replace("**", string.Empty, StringComparison.Ordinal).Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;

    /// <summary>원래 문장에 끝 「;」 가 없으면 고친 글의 끝 「;」 를 뺀다(문장 범위에는 구분자가 들어 있지 않다).</summary>
    private static string MatchTerminator(string original, string replacement)
    {
        var trimmed = replacement.TrimEnd();
        return !original.TrimEnd().EndsWith(';') && trimmed.EndsWith(';') ? trimmed[..^1].TrimEnd() : trimmed;
    }

    [GeneratedRegex(@"```[a-zA-Z]*[ \t]*\r?\n(?<code>[\s\S]*?)\r?\n?```")]
    private static partial Regex CodeBlock();
}

/// <summary>
/// 앱 연결: 「오류 고치기」 명령이 부른다. 활성 SQL 탭 아래 제안 막대(<see cref="AiSuggestionBar"/>, <see cref="SqlEditorViewModel.AiBar"/>)를 만들고 바로 요청한다.
/// 누를 때마다 접속·설정·오류를 새로 읽는다(「다시」·「주 모델로 다시 보내기」 도 같다).
/// </summary>
public static class FixErrorInlineLauncher
{
    /// <summary>활성 SQL 탭에 막대를 띄우고 요청한다. 탭이 없으면 아무것도 하지 않는다.</summary>
    public static Task RunAsync(IServiceProvider services, CancellationToken ct) => RunAsync(services, SqlEditorHost.Current.Active, ct);

    internal static async Task RunAsync(IServiceProvider services, SqlEditorViewModel? tab, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (tab is null)
        {
            return;
        }

        if (tab.AiBar is { IsRunning: true })
        {
            return; // 진행 중인 막대가 있다: 겹쳐 보내지 않는다
        }

        var editor = services.GetService(typeof(IEditorContext)) as IEditorContext;
        var masker = services.GetService(typeof(ISqlLiteralMasker)) as ISqlLiteralMasker;
        var withMasker = new FixErrorInline(FixErrorCommand.Instance, new HostEditorTabs(), masker);
        Func<AiTier?, CancellationToken, Task<AiStripPreparation>> prepare = (tier, token) => PrepareAsync(services, withMasker, tier, token);
        var strip = editor is null
            ? new AiInlineStripViewModel(FixErrorInline.Title, prepare, AiChatHost.For(services))
            : AiSuggestionBar.Create(FixErrorInline.Title, prepare, (accept, token) => withMasker.AcceptAsync(accept, editor.ProposeEditAsync, token), AiChatHost.For(services));
        tab.AiBar = strip;
        await strip.RunAsync(ct).ConfigureAwait(true);
    }

    /// <summary>앱 서비스로 시작 환경을 읽어 막대 요청을 만든다(튜닝 시작과 같은 규칙: 보이는 접속의 저장된 최신 값, 다시 읽은 AI 설정).</summary>
    private static async Task<AiStripPreparation> PrepareAsync(IServiceProvider services, FixErrorInline inline, AiTier? tier, CancellationToken ct)
    {
        if (services.GetService(typeof(IEditorContext)) is not IEditorContext editor
            || services.GetService(typeof(IAiDriver)) is not IAiDriver driver
            || services.GetService(typeof(IEventBus)) is not IEventBus bus
            || ExecutionState.For(bus).ShownProfile is not { } shown)
        {
            return AiStripPreparation.Refuse(FixErrorInline.UnavailableText);
        }

        var (source, statement) = AiStatementScope.Resolve(editor, SessionRouter.Find(services)?.SqlDocumentActive, shown.Id);
        if (source == AiStatementSource.OtherDocument)
        {
            return AiStripPreparation.Refuse(AiTuningLauncher.CommandOtherDocumentText);
        }

        var profile = await LatestProfileAsync(services, shown, ct).ConfigureAwait(true) ?? shown;
        var settingsService = services.GetService(typeof(SettingsService)) as SettingsService;
        if (settingsService is not null)
        {
            try
            {
                await settingsService.ReloadAiAsync(ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("AI 설정을 다시 읽지 못했습니다: {0}", AiErrorText.Redact(ex.GetType().Name));
            }
        }

        var settings = settingsService?.Current.Ai ?? new AiSettings();
        var requested = tier ?? FixErrorCommand.Instance.Tier;
        var main = AiKitRoute.For(profile, settings, requested);
        var document = editor.GetActiveDocument();
        AiOneShotRun RunFor(int tokens) => new(new AiOneShotSetup(driver, AiKitRoute.For(profile, settings, requested, tokens), FixErrorInline.Feature)
        {
            Audit = services.GetService(typeof(IAgentAuditStore)) as IAgentAuditStore,
            TrackSession = services.GetService(typeof(IAgentSessionSource)) is AppAgentSessionSource sessions ? sessions.Track : null,
        });

        // 시작 환경의 가림 여부는 참고용이다(명령 계약): 실제 가림은 크기로 고른 대상의 경로가 정한다.
        var env = new AiCommandStartEnv(services, editor, profile, main.Context, source, statement, document?.TabId, main.MaskRequired);
        return await inline.PrepareAsync(env, RunFor, settings.JoinSyntax, ct).ConfigureAwait(true);
    }

    private static async Task<ConnectionProfile?> LatestProfileAsync(IServiceProvider services, ConnectionProfile known, CancellationToken ct)
    {
        if (services.GetService(typeof(IConnectionStore)) is not IConnectionStore store)
        {
            return known;
        }

        try
        {
            var document = await store.LoadAsync(ct).ConfigureAwait(true);
            return document.Value.Profiles.FirstOrDefault(p => p.Id == known.Id);
        }
#pragma warning disable CA1031 // 저장소를 못 읽어도 알고 있는 사본으로 계속한다(튜닝 시작과 같다).
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return known;
        }
    }
}
