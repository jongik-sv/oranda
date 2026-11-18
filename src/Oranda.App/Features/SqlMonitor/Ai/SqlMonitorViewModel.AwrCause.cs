using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.SqlMonitor.Ai;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Features;
using Oranda.Core.Monitoring;
using Oranda.Core.Settings;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>
/// AWR 계획 이력 탭의 「AI로 원인 보기」 배선(설계 §3.1, TSK-15-05): 입구 상태(AI 설정·허용 수준 「계획」 이상·계획 2개 이상, AWR 을 못 쓰면 기존 화면 안내 그대로)와
/// 누르면 채팅창 새 대화(<see cref="AwrCauseConversation"/>). 받는 곳·가림·감사는 Kit(<see cref="AiOneShotRun"/>)이 AI 튜닝과 같은 규칙으로 맡는다.
/// </summary>
public sealed partial class SqlMonitorViewModel
{
    /// <summary>입구 단추 글.</summary>
    public const string AwrCauseLabel = "AI로 원인 보기";

    /// <summary>AI 기능(AI 튜닝)이 꺼졌을 때.</summary>
    public const string AiFeatureOffText = "AI 기능이 꺼져 있습니다. 기능 설정에서 AI 튜닝을 켜면 쓸 수 있습니다.";

    /// <summary>시험용: AI 설정(기본 = 설정 서비스의 <c>ai</c> 섹션).</summary>
    internal Func<AiSettings?>? AiSettingsSource { get; set; }

    /// <summary>시험용: AI 경로(기본 = DI 의 <see cref="IAiDriver"/> 라우터).</summary>
    internal IAiDriver? AiDriver { get; set; }

    /// <summary>시험용: 채팅창 바깥 동작(기본 = <see cref="AiChatHost.For"/>).</summary>
    internal AiChatHost? AiChat { get; set; }

    /// <summary>시험용: SQL 리터럴 가림(기본 = DI).</summary>
    internal ISqlLiteralMasker? AiMasker { get; set; }

    /// <summary>시험용: 클립보드(기본 = 주 창 클립보드).</summary>
    internal Func<string, Task>? AiCopyText { get; set; }

    /// <summary>마지막으로 연 원인 대화(없으면 null).</summary>
    internal AwrCauseConversation? AwrCause { get; private set; }

    /// <summary>입구 단추(AWR 탭이 만든다).</summary>
    internal AiEntryButton CreateAwrCauseEntry() => new(AwrCauseLabel, AwrCauseState, () => StartAwrCauseAsync());

    /// <summary>입구 상태: AI 설정 → 접속 허용 수준(계획 이상) → 화면 조건(AWR 을 읽었고 계획 2개 이상).</summary>
    internal AiEntryState AwrCauseState()
    {
        if (_session is not { } session)
        {
            return new AiEntryState(false, AwrCausePrompt.ScreenReason(Awr));
        }

        if (_services?.GetService(typeof(IFeatureGate)) is IFeatureGate gate && !gate.IsEnabled(FeatureIds.AiTuning))
        {
            return new AiEntryState(false, AiFeatureOffText);
        }

        var configured = CurrentAiSettings() is { } settings && settings.Driver != AiDriverKind.None && CurrentAiDriver() is not null;
        return AiEntryRule.Evaluate(configured, AiAccessPolicy.Effective(session.Profile), AiAccessLevel.Plan, AwrCausePrompt.ScreenReason(Awr), AwrCausePrompt.EnabledTip);
    }

    /// <summary>
    /// 「AI로 원인 보기」: 화면 사실(이력 줄·SQL 전문·바인드 캡처)을 모아 채팅창을 띄우고 새 대화로 보낸다. 실행 범위에는 sql_id·전문·파싱 스키마를 실어
    /// AWR·통계 도구가 그 문장 범위에서만 돌게 한다. 열지 못했으면 false.
    /// </summary>
    internal async Task<bool> StartAwrCauseAsync(CancellationToken ct = default)
    {
        await ReloadAiSettingsAsync(ct);
        if (!AwrCauseState().IsEnabled || _session is not { } session || SelectedSql is not { } item
            || CurrentAiSettings() is not { } settings || CurrentAiDriver() is not { } driver)
        {
            return false;
        }

        var sqlId = item.SqlId;
        var history = Awr.Rows.Select(r => r.Row).ToList();
        SqlDetail? detail = null;
        await RunAsync(async (monitor, s) => detail = await monitor.GetSqlDetailAsync(s, item.Row.Cursor, ct), ct);
        if (!string.Equals(SelectedSqlId, sqlId, StringComparison.Ordinal))
        {
            return false; // 읽는 동안 다른 SQL 을 골랐다
        }

        var facts = new AwrCauseFacts(sqlId, history, detail, item.Row.ParsingSchema, session.Profile.Dbms);
        var route = AiKitRoute.For(session.Profile, settings, AiTier.Main);
        route = route with
        {
            // 원문·sql_id·파싱 스키마: 게이트의 범위 검사와 도구가 쓴다(LLM 으로 가지 않는다).
            Context = route.Context with { SqlId = sqlId, StatementText = detail?.FullText, CurrentSchema = item.Row.ParsingSchema?.ToUpperInvariant() },
        };
        var masker = AiMasker ?? _services?.GetService(typeof(ISqlLiteralMasker)) as ISqlLiteralMasker;
        var bundle = AwrCausePrompt.Bundle(route, facts, masker);
        var run = new AiOneShotRun(new AiOneShotSetup(driver, route, AwrCausePrompt.Feature)
        {
            Audit = _services?.GetService(typeof(IAgentAuditStore)) as IAgentAuditStore,
            TrackSession = _services?.GetService(typeof(IAgentSessionSource)) is Oranda.App.Features.Ai.Agent.AppAgentSessionSource source ? source.Track : null, // 도구 연결이 실행 id 로 찾게
        });
        var chatHost = AiChat ?? AiChatHost.For(_services);
        var host = new AwrCauseHost
        {
            OpenEditor = (title, text) =>
            {
                OpenScript(title, text);
                StatusText = "계획 고정 스크립트를 새 편집기 탭에 넣었습니다(실행하지 않음).";
                return true;
            },
            CopyText = AiCopyText ?? CopyToClipboardAsync,
        };
        var conversation = await AiChatOpener.OpenAsync(chatHost, chat => new AwrCauseConversation(chat, run, facts, bundle, host) { ConfirmText = chatHost.ConfirmText });
        if (conversation is null)
        {
            StatusText = "AI 채팅창을 열지 못했습니다.";
            return false;
        }

        AwrCause = conversation;
        StatusText = "AI 채팅창에서 계획이 바뀐 원인을 봅니다: " + sqlId;
        await conversation.StartAsync(ct);
        return true;
    }

    private AiSettings? CurrentAiSettings() =>
        AiSettingsSource is { } source ? source() : (_services?.GetService(typeof(SettingsService)) as SettingsService)?.Current.Ai;

    /// <summary>보내기 전에 파일의 AI 설정을 다시 읽는다(AI 튜닝·파티션 추천과 같게, 다른 창이 저장한 설정을 라우터와 같게 본다). 못 읽으면 앞 값.</summary>
    private async Task ReloadAiSettingsAsync(CancellationToken ct)
    {
        if (AiSettingsSource is not null || _services?.GetService(typeof(SettingsService)) is not SettingsService settings)
        {
            return;
        }

        try
        {
            await settings.ReloadAiAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("AI 설정을 다시 읽지 못했습니다: {0}", ex.GetType().Name);
        }
    }

    private IAiDriver? CurrentAiDriver() => AiDriver ?? _services?.GetService(typeof(IAiDriver)) as IAiDriver;

    private static async Task CopyToClipboardAsync(string text)
    {
        if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is Window window)
        {
            await ClipboardText.SetAsync(window, text);
        }
    }
}
