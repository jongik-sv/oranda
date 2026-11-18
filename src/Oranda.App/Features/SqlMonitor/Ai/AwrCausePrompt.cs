using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.App.Features.Ai.Kit;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.SqlMonitor.Ai;

/// <summary>「AI로 원인 보기」 에 실을 화면 사실: AWR 이력 줄(계획 이력 탭이 읽은 그대로)·SQL 전문·바인드 캡처·파싱 스키마.</summary>
/// <param name="SqlId">대상 sql_id.</param>
/// <param name="History">AWR 스냅숏 × plan_hash 줄.</param>
/// <param name="Detail">SQL 전문과 바인드 캡처(못 읽었으면 null).</param>
/// <param name="ParsingSchema">파싱 스키마(모르면 null).</param>
/// <param name="Dbms">접속 DBMS(계획 고정 스크립트를 낼 수 있는지 정한다).</param>
public sealed record AwrCauseFacts(string SqlId, IReadOnlyList<AwrPlanRow> History, SqlDetail? Detail, string? ParsingSchema, Dbms Dbms = Dbms.Oracle)
{
    /// <summary>계획이 처음 나온 차례대로의 plan_hash.</summary>
    public IReadOnlyList<long> PlanHashes => [.. History.OrderBy(r => r.SnapId).Select(r => r.PlanHashValue).Distinct()];

    /// <summary>SQL Plan Baseline 스크립트를 낼 수 있는 DBMS(Tibero 는 계획 관리 원본이 없다).</summary>
    public bool CanPinPlan => DbmsSupport.Supports(Dbms, DbmsFeature.PlanControls);
}

/// <summary>
/// AWR 「계획이 바뀐 원인」 의 보낼 글(설계 §3.1): 지시·문맥 꾸러미·답 틀(① 언제 무엇이 바뀌었나 ② 원인 후보 ③ 권하는 조치 ④ 고정할 계획)과,
/// 답의 「고정할 계획」 칸에서 SQL Plan Baseline 스크립트를 앱이 만드는 일. 스크립트 글은 AI 가 쓰지 않는다(계획 번호만 받는다). 아무것도 실행하지 않는다.
/// </summary>
public static partial class AwrCausePrompt
{
    /// <summary>채팅창 제목·카드 제목.</summary>
    public const string Title = "계획이 바뀐 원인";

    /// <summary>감사 기록의 기능 이름(<c>ai_kit:awr_cause</c>).</summary>
    public const string Feature = "awr_cause";

    public const string WhenSection = "언제 무엇이 바뀌었나";

    public const string CauseSection = "원인 후보";

    public const string ActionSection = "권하는 조치";

    public const string PinSection = "고정할 계획";

    /// <summary>꾸러미에 싣는 스냅숏 줄 수(가장 최근 것부터). 더 필요하면 AI 가 도구로 읽는다.</summary>
    public const int MaxSnapshotLines = 48;

    /// <summary>답 틀.</summary>
    public static AiAnswerTemplate Template { get; } = new([
        new AiAnswerSection(WhenSection, "when the plan changed, from which plan_hash_value to which, and how elapsed time and buffer gets per execution changed"),
        new AiAnswerSection(CauseSection, "likely causes, most likely first, each with its evidence such as the statistics gathering time, histograms, bind sensitivity, index changes or data volume", ["원인", "근거"]),
        new AiAnswerSection(ActionSection, "recommended actions as a short list; nothing is executed by the app"),
        new AiAnswerSection(PinSection, "only the plan_hash_value number to pin with a SQL Plan Baseline when pinning that plan is the right action, otherwise 없음"),
    ]);

    /// <summary>문맥 꾸러미: 계획별 요약·바뀐 시점·스냅숏 줄(계획 수준) · 파싱 스키마(메타데이터) · SQL 전문(SQL 글, 가림 대상이면 가림) · 바인드 캡처 값(조회+행 값).</summary>
    public static AiContextBundle Bundle(AiKitRoute route, AwrCauseFacts facts, ISqlLiteralMasker? masker)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(facts);
        var b = route.Bundle(Title, masker)
            .Add("awr_plans", "AWR 계획별 요약", PlansText(facts.History), AiAccessLevel.Plan)
            .Add("plan_changes", "계획이 바뀐 시점", ChangesText(facts.History), AiAccessLevel.Plan)
            .Add("awr_snapshots", "AWR 스냅숏 줄", SnapshotsText(facts.History), AiAccessLevel.Plan)
            .Add("parsing_schema", "파싱 스키마", facts.ParsingSchema, AiAccessLevel.Metadata);
        if (facts.Detail is { } detail)
        {
            b.Add("statement", "SQL 전문", detail.FullText, AiAccessLevel.Plan, AiContextKind.Sql)
             .Add("bind_capture", "바인드 캡처 값", BindsText(detail.Binds), AiAccessLevel.QueryRows);
        }

        return b.Build();
    }

    /// <summary>첫 요청 글: 지시 → 꾸러미 → 답 틀.</summary>
    public static string FirstPrompt(AwrCauseFacts facts, IAiContextBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(bundle);
        var text = new StringBuilder();
        text.AppendLine("# Task");
        text.AppendLine(CultureInfo.InvariantCulture, $"Find why the execution plan of the statement with sql_id {facts.SqlId} changed over time. The app collected its AWR history below.");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Compare the plans with get_awr_plan_history and get_awr_plan (sql_id {facts.SqlId}), and look at the tables and indexes in the plans with get_table_stats, get_index_info and get_histogram.");
        text.AppendLine("- Match last_analyzed of the tables and indexes with the time the plan changed. Consider statistics refresh, bind peeking (histograms, bind-sensitive cursors), index changes and data volume changes.");
        text.AppendLine("- Never run DDL, DML or PL/SQL and never ask the user to let you run them. Only explain and suggest.");
        text.AppendLine(facts.CanPinPlan
            ? "- If pinning one plan with a SQL Plan Baseline is the right action, write only its plan_hash_value in the last section; the app writes the script."
            : "- This DBMS has no SQL Plan Baseline; write 없음 in the last section.");
        text.AppendLine();
        text.AppendLine(bundle.ToPrompt());
        text.Append(Template.Rules);
        return text.ToString();
    }

    /// <summary>이어 묻기 글. 이어받기 id 가 없으면 첫 요청과 앞 답을 데이터로 다시 싣는다(<see cref="AiCarriedConversation"/> 와 같은 꼴).</summary>
    public static string FollowUpPrompt(string question, string firstPrompt, IReadOnlyList<(string Question, string Answer)> turns, string? resumeId)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(firstPrompt);
        ArgumentNullException.ThrowIfNull(turns);
        var follow = "# Follow-up\n" + question + "\nAnswer in Korean, briefly, in Markdown. Never write tool or function names. Never run anything; only suggest.";
        if (resumeId is not null)
        {
            return follow;
        }

        var history = new StringBuilder(firstPrompt);
        foreach (var (q, a) in turns)
        {
            if (q.Length > 0)
            {
                history.Append("\n<previous_question>\n").Append(q).Append("\n</previous_question>");
            }

            history.Append("\n<previous_answer>\n").Append(a).Append("\n</previous_answer>");
        }

        return history.Append("\n\n").Append(follow).ToString();
    }

    /// <summary>
    /// 답의 「고정할 계획」 칸에서 고정할 plan_hash 를 찾는다: 칸 글의 숫자 중 이력에 있는 계획만 받는다(지어낸 번호는 버린다). 없거나 「없음」 이면 null.
    /// </summary>
    public static long? PinnedPlan(AiTemplatedAnswer? answer, AwrCauseFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.CanPinPlan || answer?[PinSection] is not { } part)
        {
            return null;
        }

        var known = facts.PlanHashes.ToHashSet();
        foreach (Match m in Number().Matches(part.Body))
        {
            if (long.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hash) && known.Contains(hash))
            {
                return hash;
            }
        }

        return null;
    }

    /// <summary>
    /// SQL Plan Baseline 고정 스크립트(앱이 만든 글, 실행하지 않음): 공유 풀에서 올리는 문안(<see cref="TuningScripts.Baseline"/>)과, 공유 풀에 없을 때 AWR 에서 올리는 문안(12.2 이상, 주석).
    /// </summary>
    public static string BaselineScript(AwrCauseFacts facts, long planHash)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var mine = facts.History.Where(r => r.PlanHashValue == planHash).ToList();
        var execs = mine.Sum(r => r.Executions);
        var avg = execs > 0 ? mine.Sum(r => r.ElapsedMicros) / 1000d / execs : (double?)null;
        var text = new StringBuilder();
        text.AppendLine("-- AI 「계획이 바뀐 원인」 이 권한 계획 고정(Oranda 가 만든 문안, 실행하지 않았습니다). 실행 전에 내용을 확인하세요.");
        text.Append(CultureInfo.InvariantCulture, $"-- 대상: sql_id {facts.SqlId} · plan_hash {planHash}");
        if (avg is { } ms)
        {
            text.Append(CultureInfo.InvariantCulture, $" (AWR 평균 {AwrRowItem.Ms(ms)}/실행)");
        }

        text.AppendLine();
        text.AppendLine(TuningScripts.Baseline(facts.SqlId, planHash).Body);
        if (mine.Count > 0)
        {
            var begin = Math.Max(1, mine.Min(r => r.SnapId) - 1);
            var end = mine.Max(r => r.SnapId);
            var filter = TuningScripts.Literal($"sql_id = '{facts.SqlId}' AND plan_hash_value = {planHash.ToString(CultureInfo.InvariantCulture)}");
            text.AppendLine();
            text.AppendLine("-- 이 계획이 공유 풀에 없으면(위 결과 0) AWR 에서 올립니다(12.2 이상, DB 에 씁니다). 쓰려면 주석을 푸세요.");
            text.AppendLine("-- DECLARE");
            text.AppendLine("--   l_loaded PLS_INTEGER;");
            text.AppendLine("-- BEGIN");
            text.AppendLine(CultureInfo.InvariantCulture, $"--   l_loaded := DBMS_SPM.LOAD_PLANS_FROM_AWR(begin_snap => {begin}, end_snap => {end},");
            text.AppendLine(CultureInfo.InvariantCulture, $"--     basic_filter => {filter}, fixed => 'YES');");
            text.AppendLine("--   DBMS_OUTPUT.PUT_LINE('loaded plans: ' || l_loaded);");
            text.AppendLine("-- END;");
            text.Append("-- /");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>입구가 꺼지는 까닭(켜지면 null): 기존 AWR 화면의 안내를 그대로 쓰고(AE-4), 계획이 2개 이상일 때만 켠다.</summary>
    public static string? ScreenReason(AwrHistoryViewModel awr)
    {
        ArgumentNullException.ThrowIfNull(awr);
        return awr.State switch
        {
            AwrPaneState.Loaded when awr.Plans.Count >= 2 => null,
            AwrPaneState.Loaded => string.Create(CultureInfo.InvariantCulture,
                $"같은 sql_id 에 계획(plan_hash_value)이 2개 이상일 때 씁니다. 지금은 {awr.Plans.Count}개입니다."),
            AwrPaneState.Loading => "AWR 계획 이력을 읽는 중입니다.",
            AwrPaneState.LicenseOff or AwrPaneState.NoSession or AwrPaneState.NoSql or AwrPaneState.Empty or AwrPaneState.Error =>
                (awr.Message + ". " + awr.HintText).Trim(),
            _ => awr.Message,
        };
    }

    /// <summary>켜졌을 때의 툴팁.</summary>
    public const string EnabledTip = "AWR 이력과 통계로 계획이 바뀐 원인을 AI 채팅창에서 봅니다(주 모델 · 도구 씀, 실행하지 않음).";

    private static string PlansText(IReadOnlyList<AwrPlanRow> rows)
    {
        var text = new StringBuilder();
        foreach (var g in rows.GroupBy(r => r.PlanHashValue).OrderBy(g => g.Min(r => r.SnapId)))
        {
            var execs = g.Sum(r => r.Executions);
            text.AppendLine(CultureInfo.InvariantCulture,
                $"plan_hash_value={g.Key} first={Time(g.Min(r => r.SnapTime))} last={Time(g.Max(r => r.SnapTime))} snapshots={g.Select(r => r.SnapId).Distinct().Count()} executions={execs} elapsed_ms_per_exec={PerExec(g.Sum(r => r.ElapsedMicros) / 1000d, execs)} buffer_gets_per_exec={PerExec(g.Sum(r => r.BufferGets), execs)}");
        }

        return text.ToString().TrimEnd();
    }

    private static string ChangesText(IReadOnlyList<AwrPlanRow> rows)
    {
        var text = new StringBuilder();
        long? previous = null;
        foreach (var snap in rows.Where(r => r.Executions > 0).GroupBy(r => r.SnapId).OrderBy(g => g.Key))
        {
            var main = snap.OrderByDescending(r => r.Executions).First();
            if (previous is { } p && p != main.PlanHashValue)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{Time(main.SnapTime)} snap {snap.Key}: {p} -> {main.PlanHashValue}");
            }

            previous = main.PlanHashValue;
        }

        return text.Length == 0 ? "(no switch between snapshots; the plans ran in the same snapshots)" : text.ToString().TrimEnd();
    }

    private static string SnapshotsText(IReadOnlyList<AwrPlanRow> rows)
    {
        var ordered = rows.OrderBy(r => r.SnapId).ThenBy(r => r.PlanHashValue).ToList();
        var skip = Math.Max(0, ordered.Count - MaxSnapshotLines);
        var text = new StringBuilder();
        if (skip > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"({skip} older lines omitted)");
        }

        text.AppendLine("snap_id | end_time | plan_hash_value | executions | elapsed_ms_per_exec | buffer_gets_per_exec");
        foreach (var r in ordered.Skip(skip))
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{r.SnapId} | {Time(r.SnapTime)} | {r.PlanHashValue} | {r.Executions} | {PerExec(r.ElapsedMicros / 1000d, r.Executions)} | {PerExec(r.BufferGets, r.Executions)}");
        }

        return text.ToString().TrimEnd();
    }

    private static string? BindsText(IReadOnlyList<CapturedBind> binds) =>
        binds.Count == 0 ? null : string.Join('\n', binds.OrderBy(b => b.Position).Select(b => $"{b.Name} ({b.DataType ?? "?"}) = {b.Value ?? "(not captured)"}"));

    private static string Time(DateTime t) => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string PerExec(double total, long executions) =>
        executions > 0 ? (total / executions).ToString("0.###", CultureInfo.InvariantCulture) : "-";

    [GeneratedRegex(@"\d{1,10}", RegexOptions.CultureInvariant)]
    private static partial Regex Number();
}
