using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.Tuning.Cursors;
using Oranda.Core.Connection;
using Oranda.Core.Plans;
using Oranda.Core.Tuning;
using Oranda.Language.Lexing;
using Oranda.Language.Tuning;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Features.Tuning.Report;

/// <summary>
/// 튜닝 보고서 HTML 생성기(TSK-06-07). 입력 = 수집 결과(<see cref="TuningReportData"/>), 출력 = 자기 완결 HTML 글 하나.
/// 화면과 떨어진 순수 함수다: 파일·DB·네트워크를 만지지 않는다. 구조·절 순서·스타일은 시안 tuning-report.html 을 따른다
/// (인라인 CSS·SVG, &lt;details&gt; 접기, 목차, 인쇄용, 라이트·다크). 스크립트·외부 자원은 넣지 않는다.
/// 모든 SQL·값은 <see cref="Esc"/> 로 HTML 이스케이프한다. 바인드 값은 <see cref="TuningReportOptions.IncludeBindValues"/> 일 때만 적는다.
/// </summary>
public static class TuningReportHtml
{
    /// <summary>보고서 절 순서(목차 번호 = 위치 + 1).</summary>
    public static readonly IReadOnlyList<TuningReportSection> Order =
    [
        TuningReportSection.Summary, TuningReportSection.SqlText, TuningReportSection.Plans, TuningReportSection.Graph,
        TuningReportSection.ExecStats, TuningReportSection.ObjectStats, TuningReportSection.ChildCursors, TuningReportSection.Waits,
        TuningReportSection.Awr, TuningReportSection.Candidates, TuningReportSection.Environment,
    ];

    /// <summary>요약 카드 상한.</summary>
    public const int MaxCards = 6;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>HTML 글·속성 값 이스케이프(&lt; &gt; &amp; " ' 만 — 한글·가운뎃점 등은 UTF-8 그대로 둔다).</summary>
    public static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        if (s.AsSpan().IndexOfAny("<>&\"'") < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            _ = c switch
            {
                '<' => sb.Append("&lt;"),
                '>' => sb.Append("&gt;"),
                '&' => sb.Append("&amp;"),
                '"' => sb.Append("&quot;"),
                '\'' => sb.Append("&#39;"),
                _ => sb.Append(c),
            };
        }

        return sb.ToString();
    }

    /// <summary>제안 파일 이름: <c>tuning_{sql_id}_{yyyyMMdd_HHmm}.html</c>.</summary>
    public static string SuggestedFileName(string sqlId, DateTimeOffset at)
    {
        var id = new string((sqlId ?? string.Empty).Trim().Where(char.IsLetterOrDigit).ToArray());
        return string.Create(Inv, $"tuning_{(id.Length == 0 ? "sql" : id)}_{at:yyyyMMdd_HHmm}.html");
    }

    public static string Build(TuningReportData d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var w = new Writer(d.Options.English);
        var en = w.En;
        var sqlId = d.Options.SqlId.Trim();
        var title = L(en, "SQL 튜닝 보고서", "SQL tuning report");

        w.Raw("<!doctype html>\n<html lang=\"").Raw(en ? "en" : "ko").Raw("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Raw("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Raw("<meta name=\"generator\" content=\"Oranda ").Text(d.Context.AppVersion).Raw("\">\n")
            .Raw("<title>").Text(title + " " + sqlId).Raw("</title>\n<style>\n").Raw(TuningReportStyles.Css).Raw("</style>\n</head>\n<body>\n<div class=\"wrap\">\n");

        Header(w, d, title, sqlId);
        var states = Order.Select(s => (Section: s, Excluded: ExcludedReason(d, s))).ToList();
        Toc(w, states);
        w.Raw("<main>\n");
        for (var i = 0; i < states.Count; i++)
        {
            var (s, excluded) = states[i];
            Section(w, d, s, i + 1, excluded);
        }

        w.Raw("</main>\n\n<footer>\n")
            .Raw("  <span>").Text(L(en, "모든 자료는 SELECT 로만 모았습니다. DB 에 쓰지 않았습니다.", "All data was read with SELECT only. Nothing was written to the database.")).Raw("</span>\n")
            .Raw("  <span>").Text(L(en, "후보 DDL·재작성 SQL 은 제안이며 실행되지 않았습니다.", "Candidate DDL and rewritten SQL are suggestions and were not run.")).Raw("</span>\n")
            .Raw("  <span>").Text("Oranda " + d.Context.AppVersion + " · " + Stamp(d.Context.CreatedAt)).Raw("</span>\n")
            .Raw("</footer>\n</div>\n</body>\n</html>\n");
        return w.ToString();
    }

    // ---- 머리·목차 ----

    private static void Header(Writer w, TuningReportData d, string title, string sqlId)
    {
        var en = w.En;
        var c = d.Context;
        w.Raw("<header class=\"top\">\n  <div class=\"kicker\">").Text("Oranda · " + title + " · " + L(en, "읽기 전용 수집", "read-only collection")).Raw("</div>\n")
            .Raw("  <h1>").Text(title).Raw(" <code>").Text(sqlId).Raw("</code></h1>\n  <div class=\"meta\">\n");
        w.Raw("    <span>DB <b>").Text(c.DatabaseName ?? "—").Raw("</b> ").Text(c.ServerVersion + (c.IsRac ? " · RAC" : string.Empty)).Raw("</span>\n");
        var (envText, envClass) = c.Environment switch
        {
            ConnectionEnvironment.Production => (L(en, "운영", "Production"), "danger"),
            ConnectionEnvironment.Test => (L(en, "테스트", "Test"), "warn"),
            _ => (L(en, "개발", "Development"), "ok"),
        };
        w.Raw("    <span>").Text(L(en, "접속", "Connection")).Raw(" <b>").Text(c.User + "@" + c.ConnectionName).Raw("</b> <span class=\"chip ").Raw(envClass).Raw("\">").Text(envText).Raw("</span></span>\n");
        w.Raw("    <span>").Text(L(en, "만든 때", "Created")).Raw(" <b>").Text(c.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Raw("</b> (").Text(c.CreatedAt.ToString("zzz", Inv)).Raw(")</span>\n");
        var childText = d.Options.ChildNumber is { } n
            ? "child " + n.ToString(Inv)
            : d.Cursors.Value is { } cur ? string.Format(Inv, L(en, "전체 {0}개", "all {0}"), cur.Children.Count) : L(en, "전체", "all");
        w.Raw("    <span>").Text(L(en, "자식 커서", "Child cursors")).Raw(" <b>").Text(childText).Raw("</b></span>\n");
        w.Raw("    <span>").Text(L(en, "바인드 값", "Bind values")).Raw(" <b>").Text(d.Options.IncludeBindValues ? L(en, "포함", "included") : L(en, "포함 안 함", "not included")).Raw("</b></span>\n");
        w.Raw("    <span>Oranda ").Text(c.AppVersion).Raw("</span>\n  </div>\n</header>\n\n");
    }

    private static void Toc(Writer w, List<(TuningReportSection Section, string? Excluded)> states)
    {
        var en = w.En;
        w.Raw("<nav class=\"toc\" aria-label=\"").Text(L(en, "목차", "Contents")).Raw("\">\n  <div class=\"lbl\">").Text(L(en, "목차", "Contents")).Raw("</div>\n  <ol>\n");
        for (var i = 0; i < states.Count; i++)
        {
            var (s, excluded) = states[i];
            w.Raw(excluded is null ? "    <li>" : "    <li class=\"off\">").Raw("<a href=\"#s").Raw((i + 1).ToString(Inv)).Raw("\">")
                .Text(TuningReportText.SectionTitle(s, en) + (excluded is null ? string.Empty : " (" + L(en, "포함 안 함", "not included") + ")")).Raw("</a></li>\n");
        }

        w.Raw("  </ol>\n</nav>\n\n");
    }

    /// <summary>절이 빠진 이유(고르지 않음·팩 없음). 담기면 null. 수집 실패는 빠짐이 아니다(절 안에 실패를 적는다).</summary>
    internal static string? ExcludedReason(TuningReportData d, TuningReportSection s)
    {
        if (!d.Options.Includes(s))
        {
            return TuningReportText.NotChosen(d.Options.English);
        }

        return s switch
        {
            TuningReportSection.Awr => d.Awr.Excluded,
            TuningReportSection.Waits => d.Waits.Excluded,
            _ => null,
        };
    }

    // ---- 절 ----

    private static void Section(Writer w, TuningReportData d, TuningReportSection s, int number, string? excluded)
    {
        var en = w.En;
        var chips = new StringBuilder();
        var body = new Writer(en);
        if (excluded is not null)
        {
            Chip(chips, L(en, "포함 안 함", "not included"));
            var pack = s == TuningReportSection.Awr && d.Options.Includes(s);
            body.Raw("<div class=\"excluded\">").Raw(pack ? "<span>🔒</span>" : string.Empty).Raw("<div><b>").Text(L(en, "포함 안 함: ", "Not included: ") + excluded).Raw("</b>");
            if (pack)
            {
                body.Raw("<br>").Text(TuningReportText.NoDiagnosticsHint(en));
            }

            body.Raw("</div></div>");
        }
        else
        {
            switch (s)
            {
                case TuningReportSection.Summary: SummarySection(body, chips, d); break;
                case TuningReportSection.SqlText: SqlTextSection(body, chips, d); break;
                case TuningReportSection.Plans: PlansSection(body, chips, d); break;
                case TuningReportSection.Graph: GraphSection(body, chips, d); break;
                case TuningReportSection.ExecStats: ExecStatsSection(body, chips, d); break;
                case TuningReportSection.ObjectStats: ObjectsSection(body, chips, d); break;
                case TuningReportSection.ChildCursors: CursorsSection(body, chips, d); break;
                case TuningReportSection.Waits: WaitsSection(body, chips, d); break;
                case TuningReportSection.Awr: AwrSection(body, chips, d); break;
                case TuningReportSection.Candidates: CandidatesSection(body, chips, d); break;
                case TuningReportSection.Environment: EnvironmentSection(body, chips, d); break;
            }
        }

        var id = "s" + number.ToString(Inv);
        w.Raw("<details class=\"sec\" id=\"").Raw(id).Raw("\" data-section=\"").Raw(s.ToString()).Raw(excluded is null ? "\" open>\n" : "\">\n")
            .Raw("  <summary><span class=\"num\">").Raw(number.ToString(Inv)).Raw("</span><h2>").Text(TuningReportText.SectionTitle(s, en)).Raw("</h2>");
        if (chips.Length > 0)
        {
            w.Raw("<span class=\"right\">").Raw(chips.ToString()).Raw("</span>");
        }

        w.Raw("</summary>\n  <div class=\"body\">\n").Raw(body.ToString()).Raw("\n  </div>\n</details>\n\n");
    }

    private static void Failed(Writer w, string reason) =>
        w.Raw("<div class=\"excluded failed\"><span>⚠</span><div><b>").Text(L(w.En, "수집 실패: ", "Collection failed: ") + reason).Raw("</b></div></div>");

    private static bool FailedPart<T>(Writer w, ReportPart<T> part)
        where T : class
    {
        if (part.Failure is { } f)
        {
            Failed(w, f);
            return true;
        }

        if (part.Excluded is { } e)
        {
            w.Raw("<div class=\"excluded\"><div><b>").Text(L(w.En, "포함 안 함: ", "Not included: ") + e).Raw("</b></div></div>");
            return true;
        }

        return part.Value is null;
    }

    private static void Chip(StringBuilder sb, string text, string? kind = null) =>
        sb.Append("<span class=\"chip").Append(kind is null ? string.Empty : " " + kind).Append("\">").Append(Esc(text)).Append("</span>");

    // 1. 요약
    private enum Severity
    {
        Danger,
        Warn,
        Info,
    }

    private sealed record Card(Severity Severity, string Title, string Html);

    private static void SummarySection(Writer w, StringBuilder chips, TuningReportData d)
    {
        var en = w.En;
        var cards = SummaryCards(d);
        var danger = cards.Count(c => c.Severity == Severity.Danger);
        var warn = cards.Count(c => c.Severity == Severity.Warn);
        var info = cards.Count(c => c.Severity == Severity.Info);
        if (danger > 0)
        {
            Chip(chips, L(en, "위험 ", "danger ") + danger.ToString(Inv), "danger");
        }

        if (warn > 0)
        {
            Chip(chips, L(en, "주의 ", "warning ") + warn.ToString(Inv), "warn");
        }

        if (info > 0)
        {
            Chip(chips, L(en, "정보 ", "info ") + info.ToString(Inv));
        }

        w.Raw("<div class=\"kpis\">");
        if (d.ExecStats.Value is { } st)
        {
            var exec = Math.Max(st.Executions, 1);
            Kpi(w, L(en, "실행당 경과", "Elapsed per exec"), Num(st.ElapsedMicros / 1000d / exec), "ms");
            Kpi(w, L(en, "실행당 논리 읽기", "Buffer gets per exec"), Num((double)st.BufferGets / exec), L(en, "블록", "blocks"));
            Kpi(w, L(en, "실행 수 (누적)", "Executions (total)"), Num(st.Executions), null);
        }

        if (d.Cursors.Value is { } cur)
        {
            Kpi(w, L(en, "자식 커서 · plan_hash", "Children · plan_hash"), cur.Children.Count.ToString(Inv),
                "· " + string.Format(Inv, L(en, "{0}개", "{0}"), cur.Children.Select(c => c.PlanHashValue).Distinct().Count()));
        }

        w.Raw("</div>\n");
        if (d.ExecStats.Failure is { } f && d.Cursors.Value is null)
        {
            Failed(w, f);
        }

        w.Raw("<div class=\"cards\">");
        foreach (var c in cards)
        {
            var (cls, sev) = c.Severity switch
            {
                Severity.Danger => ("danger", L(en, "위험", "Danger")),
                Severity.Warn => ("warn", L(en, "주의", "Warning")),
                _ => ("info", L(en, "정보", "Info")),
            };
            w.Raw("<div class=\"card ").Raw(cls).Raw("\"><div class=\"t\"><span class=\"sev\">").Text(sev).Raw("</span>").Text(c.Title).Raw("</div><p>").Raw(c.Html).Raw("</p></div>");
        }

        w.Raw("</div>");
    }

    private static void Kpi(Writer w, string k, string v, string? unit)
    {
        w.Raw("<div class=\"kpi\"><div class=\"k\">").Text(k).Raw("</div><div class=\"v\">").Text(v);
        if (unit is not null)
        {
            w.Raw(" <small>").Text(unit).Raw("</small>");
        }

        w.Raw("</div></div>");
    }

    private static List<Card> SummaryCards(TuningReportData d)
    {
        var en = d.Options.English;
        var cards = new List<Card>();
        var first = FirstPlan(d);

        // 가장 비싼 단계
        if (first?.Plan is { Steps.Count: > 1 } plan)
        {
            var m = PlanMetrics.Compute(plan.Steps);
            if (m.HottestIndex >= 0)
            {
                var step = plan.Steps[m.HottestIndex];
                var sm = m.Steps[m.HottestIndex];
                var time = m.Actual == PlanActualMetric.Time;
                var share = time ? sm.SelfActualShare : sm.SelfCostShare;
                var sev = share >= 0.5 ? Severity.Danger : share >= 0.2 ? Severity.Warn : Severity.Info;
                var html = new StringBuilder();
                html.Append("Id ").Append(step.Id.ToString(Inv)).Append(" <code>").Append(Esc(StepLabel(step))).Append("</code> ")
                    .Append(Esc(string.Format(Inv, time ? L(en, "가 시간의 {0} %.", "takes {0} % of the time.") : L(en, "가 비용의 {0} %.", "takes {0} % of the cost."), Pct(share))));
                if (step.ActualRows is { } ar)
                {
                    html.Append(' ').Append(Esc(string.Format(Inv, L(en, "실제 {0} 행.", "{0} actual rows."), Num(ar))));
                }

                cards.Add(new Card(sev, L(en, "가장 비싼 단계", "Most expensive step"), html.ToString()));
            }
        }

        // 오래된·없는 통계
        if (d.Objects.Value is { } objects)
        {
            var stale = objects.Where(o => o.Stats is { Stale: true }).ToList();
            if (stale.Count > 0)
            {
                var html = string.Join(" · ", stale.Select(o => "<code>" + Esc(o.Owner + "." + o.Table) + "</code> " + Esc(AnalyzedAgo(o.Stats!.LastAnalyzed, d.Context.CreatedAt, en))));
                cards.Add(new Card(Severity.Danger, L(en, "오래된 통계", "Stale statistics"),
                    html + Esc(L(en, " — stale 표시. 예상 행 수가 실제와 어긋날 수 있습니다.", " — marked stale. Estimated rows may be off."))));
            }

            var none = objects.Where(o => o.Stats is null && o.Failure is null).ToList();
            if (none.Count > 0)
            {
                cards.Add(new Card(Severity.Warn, L(en, "통계 없음", "No statistics"),
                    string.Join(", ", none.Select(o => "<code>" + Esc(o.Owner + "." + o.Table) + "</code>")) + Esc(L(en, " — 통계를 모은 적이 없습니다(동적 샘플링에 기댐).", " — never analyzed (relies on dynamic sampling)."))));
            }
        }

        // 예상 행 수 어긋남
        if (first?.Plan is { } p2 && WorstMisestimate(p2) is { } worst)
        {
            cards.Add(new Card(Severity.Warn, L(en, "예상 행 수 어긋남", "Cardinality misestimate"),
                "Id " + worst.Step.Id.ToString(Inv) + " <code>" + Esc(StepLabel(worst.Step)) + "</code> "
                + Esc(string.Format(Inv, L(en, "예상 {0} 행, 실제 {1} 행 ({2} 배).", "estimated {0} rows, actual {1} ({2}×)."), Num(worst.Estimated), Num(worst.Actual), Num(worst.Ratio)))));
        }

        // 암시적 형 변환
        if (d.Candidates.Value is { } cand && cand.Rewrites.FirstOrDefault(r => r.Rule == RewriteRule.ImplicitConversion) is { } conv)
        {
            cards.Add(new Card(Severity.Warn, L(en, "암시적 형 변환 의심", "Suspected implicit conversion"), Esc(conv.Candidate.Description)));
        }

        // 자식 커서 갈림
        if (d.Cursors.Value is { } cur)
        {
            var hashes = cur.Children.Select(c => c.PlanHashValue).Distinct().Count();
            if (hashes > 1)
            {
                var reasons = cur.Reasons.GroupBy(r => r.Reason).OrderByDescending(g => g.Count()).Take(2)
                    .Select(g => "<code>" + Esc(g.Key) + "</code> " + Esc(string.Format(Inv, L(en, "{0}건", "×{0}"), g.Count())));
                var html = Esc(string.Format(Inv, L(en, "{0} 개 자식, plan_hash {1} 개.", "{0} children, {1} plan hashes."), cur.Children.Count, hashes));
                var reasonText = string.Join(", ", reasons);
                cards.Add(new Card(Severity.Warn, L(en, "자식 커서 갈림", "Diverging child cursors"),
                    html + (reasonText.Length > 0 ? " " + Esc(L(en, "사유 ", "Reasons ")) + reasonText : string.Empty)));
            }
        }

        // 인덱스 후보
        if (d.Candidates.Value is { } c2 && c2.Indexes.Where(i => i.CanCreate).ToList() is { Count: > 0 } idx)
        {
            var a = idx[0];
            cards.Add(new Card(Severity.Info, string.Format(Inv, L(en, "인덱스 후보 {0}", "Index candidates: {0}"), idx.Count),
                "<code>" + Esc(a.Candidate.TableName + " (" + string.Join(", ", a.Candidate.Columns) + ")") + "</code>"
                + (a.Candidate.OverlappingIndex is { } ov ? " " + Esc(string.Format(Inv, L(en, "— 기존 {0} 와 겹침", "— overlaps {0}"), ov)) : string.Empty)));
        }

        if (cards.Count == 0)
        {
            cards.Add(new Card(Severity.Info, L(en, "눈에 띄는 문제 없음", "Nothing stands out"),
                Esc(L(en, "규칙에 걸린 항목이 없습니다. 아래 절의 수치를 직접 확인하세요.", "No rule matched. Check the numbers in the sections below."))));
        }

        return [.. cards.OrderBy(c => c.Severity).Take(MaxCards)];
    }

    private sealed record Misestimate(PlanStep Step, long Estimated, long Actual, double Ratio);

    /// <summary>E-Rows×Starts 와 A-Rows 가 10 배 넘게 다르고 큰 쪽이 100 행 이상인 단계 중 가장 심한 것.</summary>
    private static Misestimate? WorstMisestimate(CorePlan plan)
    {
        Misestimate? worst = null;
        foreach (var s in plan.Steps)
        {
            if (s.EstimatedRows is not { } e || s.ActualRows is not { } a)
            {
                continue;
            }

            var est = e * Math.Max(s.Starts ?? 1, 1);
            var hi = Math.Max(est, a);
            var lo = Math.Max(Math.Min(est, a), 1);
            var ratio = (double)hi / lo;
            if (hi >= 100 && ratio >= 10 && (worst is null || ratio > worst.Ratio))
            {
                worst = new Misestimate(s, est, a, ratio);
            }
        }

        return worst;
    }

    // 2. SQL 전문
    private static void SqlTextSection(Writer w, StringBuilder chips, TuningReportData d)
    {
        var en = w.En;
        if (FailedPart(w, d.SqlText))
        {
            return;
        }

        var t = d.SqlText.Value!;
        var include = d.Options.IncludeBindValues;
        Chip(chips, string.Format(Inv, L(en, "바인드 {0}개", "{0} binds"), t.Binds.Count) + " · " + (include ? L(en, "값 포함", "values included") : L(en, "값 포함 안 함", "values not included")));
        w.Raw("<pre class=\"code\">").Raw(Highlight(t.Text)).Raw("</pre>\n");
        if (t.FromEditor)
        {
            w.Raw("<p class=\"note\">").Text(L(en, "공유 풀에서 글을 찾지 못해 편집기 문장을 담았습니다(바인드 캡처 없음).", "The shared pool had no text, so the editor statement is shown (no bind capture).")).Raw("</p>");
        }

        if (t.Binds.Count == 0)
        {
            return;
        }

        if (!include)
        {
            w.Raw("<p class=\"note\">").Text(L(en, "바인드: ", "Binds: ") + string.Join(" · ", t.Binds.Select(b => b.Name + " " + (b.DataType ?? "?")))
                + L(en, " — 값은 만들 때 「바인드 값 포함」을 끄고 만들어 빠졌습니다.", " — values were left out because \"Include bind values\" was off.")).Raw("</p>");
            return;
        }

        w.Raw("<h3>").Text(L(en, "바인드 캡처", "Captured binds")).Raw("</h3><div class=\"tw\"><table><thead><tr><th class=\"n\">")
            .Text(L(en, "위치", "Pos")).Raw("</th><th>").Text(L(en, "이름", "Name")).Raw("</th><th>").Text(L(en, "형", "Type")).Raw("</th><th>")
            .Text(L(en, "값", "Value")).Raw("</th><th>").Text(L(en, "캡처 시각", "Captured")).Raw("</th></tr></thead><tbody>");
        foreach (var b in t.Binds)
        {
            w.Raw("<tr><td class=\"n\">").Text(b.Position.ToString(Inv)).Raw("</td><td class=\"mono\">").Text(b.Name).Raw("</td><td class=\"mono\">").Text(b.DataType ?? "—")
                .Raw("</td><td class=\"mono\">").Raw(b.Value is null ? "<span class=\"dim\">" + Esc(L(en, "캡처 안 됨", "not captured")) + "</span>" : Esc(b.Value))
                .Raw("</td><td class=\"mono\">").Text(b.CapturedAt?.ToString("yyyy-MM-dd HH:mm:ss", Inv) ?? "—").Raw("</td></tr>");
        }

        w.Raw("</tbody></table></div><p class=\"note\">").Text(L(en, "v$sql_bind_capture 는 서버가 주기적으로 캡처한 값만 보여 줍니다(실행마다의 값이 아닙니다).",
            "v$sql_bind_capture only shows values the server captured periodically (not every execution).")).Raw("</p>");
    }

    /// <summary>편집기와 같은 렉서로 칠한 SQL(모든 조각 이스케이프).</summary>
    internal static string Highlight(string sql)
    {
        var sb = new StringBuilder(sql.Length * 2);
        var pos = 0;
        foreach (var tok in SqlLexer.Tokenize(sql))
        {
            if (tok.Start > pos)
            {
                sb.Append(Esc(sql[pos..tok.Start]));
            }

            var text = Esc(sql.Substring(tok.Start, tok.Length));
            var cls = SqlTokenColors.Of(tok, sql) switch
            {
                SqlColorKind.Keyword => "kw",
                SqlColorKind.StringLiteral => "str",
                SqlColorKind.Comment => "cm",
                SqlColorKind.Bind => "bind",
                SqlColorKind.Number => "num-t",
                SqlColorKind.Hint => "hint",
                _ => null,
            };
            sb.Append(cls is null ? text : "<span class=\"" + cls + "\">" + text + "</span>");
            pos = tok.End;
        }

        if (pos < sql.Length)
        {
            sb.Append(Esc(sql[pos..]));
        }

        return sb.ToString();
    }

    // 3. 실행 계획
    private static ReportChildPlan? FirstPlan(TuningReportData d) => d.Plans.Value?.FirstOrDefault(p => p.Plan is { Steps.Count: > 0 });

    private static void PlansSection(Writer w, StringBuilder chips, TuningReportData d)
    {
        var en = w.En;
        if (FailedPart(w, d.Plans))
        {
            return;
        }

        var plans = d.Plans.Value!;
        if (FirstPlan(d) is { } first)
        {
            Chip(chips, "child " + first.ChildNumber.ToString(Inv) + " · plan_hash " + (first.Plan!.PlanHashValue ?? first.Cursor?.PlanHashValue)?.ToString(Inv));
        }

        Chip(chips, "ALLSTATS LAST");
        var many = plans.Count > 1;
        foreach (var p in plans)
        {
            if (many)
            {
                var head = "child " + p.ChildNumber.ToString(Inv)
                    + (p.InstanceId is { } inst && d.Context.IsRac ? " · inst " + inst.ToString(Inv) : string.Empty)
                    + ((p.Plan?.PlanHashValue ?? p.Cursor?.PlanHashValue) is { } h ? " · plan_hash " + h.ToString(Inv) : string.Empty)
                    + (p.Cursor is { } c ? " · " + string.Format(Inv, L(en, "실행 {0}", "{0} execs"), Num(c.Executions)) : string.Empty);
                w.Raw("<h3>").Text(head).Raw("</h3>");
            }

            if (p.Plan is not { Steps.Count: > 0 } plan)
            {
                Failed(w, p.Failure ?? L(en, "계획을 받지 못했습니다.", "No plan."));
                continue;
            }

            PlanTable(w, plan);
        }

        if (d.Cursors.Value is { } cur && cur.Children.Count > plans.Count && d.Options.ChildNumber is null)
        {
            w.Raw("<p class=\"note\">").Text(string.Format(Inv, L(en, "자식 커서 {0}개 중 실행 수 상위 {1}개만 담았습니다.", "Only the top {1} of {0} child cursors by executions are shown."), cur.Children.Count, plans.Count)).Raw("</p>");
        }
    }

    private static void PlanTable(Writer w, CorePlan plan)
    {
        var en = w.En;
        var m = PlanMetrics.Compute(plan.Steps);
        var hasActual = m.Actual != PlanActualMetric.None;
        w.Raw("<div class=\"tw\"><table>\n<thead><tr><th class=\"n\">Id</th><th>").Text(L(en, "연산", "Operation")).Raw("</th><th>").Text(L(en, "객체", "Object"))
            .Raw("</th><th class=\"n\">").Text(L(en, "비용", "Cost")).Raw("</th><th class=\"n\">E-Rows</th><th class=\"n\">A-Rows</th><th class=\"n\">Starts</th><th class=\"n\">")
            .Text(L(en, "시간(s)", "Time (s)")).Raw("</th><th>").Text(hasActual ? L(en, "실제 비중", "Actual share") : L(en, "비용 비중", "Cost share")).Raw("</th></tr></thead>\n<tbody>\n");
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var s = plan.Steps[i];
            var sm = m.Steps[i];
            var hot = i == m.HottestIndex;
            var share = hasActual ? sm.ActualShare : sm.CostShare;
            var self = hasActual ? sm.SelfActualShare : sm.SelfCostShare;
            var mis = s.EstimatedRows is { } e && s.ActualRows is { } a && Math.Max(e * Math.Max(s.Starts ?? 1, 1), a) >= 100
                && (double)Math.Max(e * Math.Max(s.Starts ?? 1, 1), a) / Math.Max(Math.Min(e * Math.Max(s.Starts ?? 1, 1), a), 1) >= 10;
            w.Raw(hot ? "<tr class=\"hot\">" : "<tr>").Raw("<td class=\"n dim\">").Text(s.Id.ToString(Inv)).Raw("</td><td class=\"op\">");
            for (var k = 0; k < s.Depth; k++)
            {
                w.Raw("<span class=\"ind\"></span>");
            }

            var op = s.Operation + (string.IsNullOrEmpty(s.Options) ? string.Empty : " " + s.Options);
            if (hot)
            {
                w.Raw("<b>").Text(op).Raw("</b>");
            }
            else
            {
                w.Text(op);
            }

            if (s.Risk.HasFlag(PlanRisk.FullScan))
            {
                w.Raw(" <span class=\"warn-t\">⚠ ").Text(L(en, "전체 읽기", "full scan")).Raw("</span>");
            }

            if (s.Risk.HasFlag(PlanRisk.Cartesian))
            {
                w.Raw(" <span class=\"danger-t\">⚠ ").Text(L(en, "카티션 곱", "cartesian")).Raw("</span>");
            }

            w.Raw("</td><td class=\"mono\">").Text(ObjectName(s)).Raw("</td>")
                .Raw("<td class=\"n\">").Text(Opt(s.Cost)).Raw("</td>")
                .Raw(mis ? "<td class=\"n warn-t\">" : "<td class=\"n\">").Text(Opt(s.EstimatedRows)).Raw("</td>")
                .Raw("<td class=\"n\">").Text(Opt(s.ActualRows)).Raw("</td>")
                .Raw("<td class=\"n\">").Text(Opt(s.Starts)).Raw("</td>")
                .Raw("<td class=\"n\">").Text(s.ActualTime is { } t ? t.TotalSeconds.ToString("N2", Inv) : string.Empty).Raw("</td>")
                .Raw("<td><span class=\"bar\"><i class=\"t\" style=\"width:").Raw(Pct(share)).Raw("%\"></i><i style=\"width:").Raw(Pct(self)).Raw("%\"></i></span><span class=\"pct\">")
                .Text(Pct(share) + "%").Raw("</span></td></tr>\n");
        }

        w.Raw("</tbody>\n</table></div>\n");
        var preds = plan.Steps.Where(s => !string.IsNullOrWhiteSpace(s.AccessPredicates) || !string.IsNullOrWhiteSpace(s.FilterPredicates)).ToList();
        if (preds.Count > 0)
        {
            w.Raw("<div class=\"pred\"><b>predicate</b>\n");
            foreach (var s in preds)
            {
                if (!string.IsNullOrWhiteSpace(s.AccessPredicates))
                {
                    w.Raw("  <div>").Text(s.Id.ToString(Inv) + " - access(" + s.AccessPredicates!.Trim() + ")").Raw("</div>\n");
                }

                if (!string.IsNullOrWhiteSpace(s.FilterPredicates))
                {
                    w.Raw("  <div>").Text(s.Id.ToString(Inv) + " - filter(" + s.FilterPredicates!.Trim() + ")").Raw("</div>\n");
                }
            }

            w.Raw("</div>\n");
        }
    }

    // 4. 계획 그래프
    private static void GraphSection(Writer w, StringBuilder chips, TuningReportData d)
    {
        var en = w.En;
        if (FailedPart(w, d.Plans))
        {
            return;
        }

        if (FirstPlan(d) is not { } first)
        {
            Failed(w, L(en, "계획을 받지 못했습니다.", "No plan."));
            return;
        }

        var m = PlanMetrics.Compute(first.Plan!.Steps);
        Chip(chips, m.Actual == PlanActualMetric.Time
            ? L(en, "색 = 시간 비중 · 너비 = 비용 비중", "colour = time share · width = cost share")
            : L(en, "색·너비 = 비용 비중", "colour and width = cost share"));
        if (d.Plans.Value!.Count(p => p.Plan is { Steps.Count: > 0 }) > 1)
        {
            w.Raw("<p class=\"note\" style=\"margin-top:0\">").Text(string.Format(Inv, L(en, "child {0} 의 계획(실행 수가 가장 많은 자식)입니다.", "Plan of child {0} (the most executed child)."), first.ChildNumber)).Raw("</p>");
        }

        w.Raw("<div class=\"svgbox\">\n").Raw(PlanSvg.Render(first.Plan!, m, en)).Raw("\n</div>");
    }

    // 5. 실행 통계
    private static void ExecStatsSection(Writer w, StringBuilder chips, TuningReportData d)
    {
        var en = w.En;
        if (FailedPart(w, d.ExecStats))
        {
            return;
        }

        var s = d.ExecStats.Value!;
        Chip(chips, "gv$sql · " + (s.Instances > 1
            ? string.Format(Inv, L(en, "인스턴스 {0}개 합", "sum of {0} instances"), s.Instances)
            : string.Format(Inv, L(en, "자식 {0}개 합", "sum of {0} children"), s.Children)));
        var exec = Math.Max(s.Executions, 1);
        w.Raw("<div class=\"tw\"><table>\n<thead><tr><th>").Text(L(en, "항목", "Item")).Raw("</th><th class=\"n\">").Text(L(en, "누적", "Total")).Raw("</th><th class=\"n\">")
            .Text(L(en, "실행당", "Per exec")).Raw("</th></tr></thead>\n<tbody>\n");
        void Row(string name, string total, string per) =>
            w.Raw("<tr><td>").Text(name).Raw("</td><td class=\"n\">").Text(total).Raw("</td><td class=\"n\">").Text(per).Raw("</td></tr>\n");
        void TimeRow(string name, long micros) => Row(name, Num(micros / 1_000_000d) + " s", Num(micros / 1000d / exec) + " ms");
        void CountRow(string name, long value) => Row(name, Num(value), Num((double)value / exec));
        Row(L(en, "실행 수", "Executions"), Num(s.Executions), "1");
        TimeRow(L(en, "경과", "Elapsed"), s.ElapsedMicros);
        TimeRow("CPU", s.CpuMicros);
        CountRow(L(en, "논리 읽기 (buffer gets)", "Logical reads (buffer gets)"), s.BufferGets);
        CountRow(L(en, "물리 읽기", "Physical reads"), s.DiskReads);
        CountRow(L(en, "처리 행", "Rows processed"), s.RowsProcessed);
        CountRow(L(en, "파스 호출", "Parse calls"), s.ParseCalls);
        CountRow(L(en, "가져오기 (fetch)", "Fetches"), s.Fetches);
        TimeRow(L(en, "대기: 사용자 I/O", "Wait: user I/O"), s.UserIoWaitMicros);
        TimeRow(L(en, "대기: 애플리케이션 (잠금)", "Wait: application (locks)"), s.ApplicationWaitMicros);
        TimeRow(L(en, "대기: 동시성", "Wait: concurrency"), s.ConcurrencyWaitMicros);
        if (d.Context.IsRac || s.ClusterWaitMicros > 0)
        {
            TimeRow(L(en, "대기: 클러스터", "Wait: cluster"), s.ClusterWaitMicros);
        }

        w.Raw("</tbody></table></div>\n<p class=\"note\">");
        var facts = new List<string>();
        if (s.ParsingSchema is { } schema)
        {
            facts.Add(L(en, "파싱 스키마 ", "Parsing schema ") + schema);
        }

        if (s.Module is { } module)
        {
            facts.Add("module " + module);
        }

        if (s.FirstLoadTime is { } fl)
        {
            facts.Add(L(en, "처음 적재 ", "First load ") + fl);
        }

        if (s.LastActiveTime is { } la)
        {
            facts.Add(L(en, "마지막 실행 ", "Last active ") + la.ToString("yyyy-MM-dd HH:mm:ss", Inv));
        }

        w.Text(string.Join(" · ", facts)).Raw("</p>");
    }

    // 6. 표·인덱스·열 통계
    private static void ObjectsSection(Writer w, StringBuilder chips, TuningReportData d)
    {
        var en = w.En;
        if (FailedPart(w, d.Objects))
        {
            return;
        }

        var objects = d.Objects.Value!;
        var stale = objects.Count(o => o.Stats is { Stale: true });
        var none = objects.Count(o => o.Stats is null && o.Failure is null);
        if (stale > 0)
        {
            Chip(chips, "stale " + stale.ToString(Inv), "danger");
        }

        if (none > 0)
        {
            Chip(chips, L(en, "통계 없음 ", "no stats ") + none.ToString(Inv), "warn");
        }

        if (objects.Count == 0)
        {
            w.Raw("<p class=\"dim\">").Text(L(en, "계획에서 읽는 표가 없습니다.", "The plan reads no table.")).Raw("</p>");
            return;
        }

        w.Raw("<h3>").Text(L(en, "표", "Tables")).Raw("</h3>\n<div class=\"tw\"><table><thead><tr><th>").Text(L(en, "표", "Table")).Raw("</th><th class=\"n\">")
            .Text(L(en, "행 수", "Rows")).Raw("</th><th class=\"n\">").Text(L(en, "블록", "Blocks")).Raw("</th><th class=\"n\">").Text(L(en, "평균 행", "Avg row"))
            .Raw("</th><th>").Text(L(en, "마지막 수집", "Last analyzed")).Raw("</th><th>stale</th></tr></thead><tbody>\n");
        foreach (var o in objects)
        {
            var name = o.Owner + "." + o.Table;
            if (o.Failure is { } f)
            {
                w.Raw("<tr><td class=\"mono\">").Text(name).Raw("</td><td colspan=\"5\" class=\"danger-t\">").Text(L(en, "수집 실패: ", "Collection failed: ") + f).Raw("</td></tr>\n");
                continue;
            }

            if (o.Stats is not { } t)
            {
                w.Raw("<tr><td class=\"mono\">").Text(name).Raw("</td><td colspan=\"5\" class=\"warn-t\">").Text(L(en, "통계 없음 — 모은 적이 없습니다", "No statistics — never analyzed")).Raw("</td></tr>\n");
                continue;
            }

            w.Raw(t.Stale ? "<tr class=\"hot\">" : "<tr>").Raw("<td class=\"mono\">").Text(name).Raw("</td><td class=\"n\">").Text(Opt(t.NumRows)).Raw("</td><td class=\"n\">").Text(Opt(t.Blocks))
                .Raw("</td><td class=\"n\">").Text(t.AvgRowLen is { } ar ? ar.ToString("N0", Inv) + " B" : string.Empty).Raw("</td><td class=\"mono\">")
                .Text(t.LastAnalyzed?.ToString("yyyy-MM-dd HH:mm", Inv) ?? "—").Raw(" <span class=\"dim\">(").Text(AnalyzedAgo(t.LastAnalyzed, d.Context.CreatedAt, en)).Raw(")</span></td>")
                .Raw(t.Stale ? "<td class=\"danger-t\">" : "<td>").Text(t.Stale ? L(en, "예", "yes") : L(en, "아니요", "no")).Raw("</td></tr>\n");
        }

        w.Raw("</tbody></table></div>\n");
        var predicateColumns = TuningReportCollector.PredicateColumns(d.Plans.Value?.Where(p => p.Plan is not null).Select(p => p.Plan!).ToList() ?? []);
        foreach (var o in objects.Where(o => o.Failure is null && (o.Indexes.Count > 0 || o.Histograms.Count > 0 || o.Columns.Count > 0)))
        {
            var name = o.Owner + "." + o.Table;
            w.Raw("<div class=\"grid2\" style=\"margin-top:12px\">\n<div>\n<h3>").Text(L(en, "인덱스 · ", "Indexes · ") + name).Raw("</h3>");
            if (o.Indexes.Count == 0)
            {
                w.Raw("<p class=\"dim\">").Text(L(en, "인덱스가 없습니다.", "No indexes.")).Raw("</p>");
            }
            else
            {
                w.Raw("<div class=\"tw\"><table><thead><tr><th>").Text(L(en, "인덱스", "Index")).Raw("</th><th>").Text(L(en, "열", "Columns")).Raw("</th><th class=\"n\">leaf</th><th class=\"n\">")
                    .Text(L(en, "고유 키", "Distinct keys")).Raw("</th><th class=\"n\">CF</th></tr></thead><tbody>");
                foreach (var ix in o.Indexes)
                {
                    var cols = o.IndexColumns?.FirstOrDefault(c => string.Equals(c.IndexName, ix.IndexName, StringComparison.OrdinalIgnoreCase))?.Columns;
                    var poor = ClusteringPoor(ix.ClusteringFactor, o.Stats);
                    w.Raw("<tr><td class=\"mono\">").Text(ix.IndexName + (ix.Uniqueness == "UNIQUE" ? " (U)" : string.Empty)).Raw("</td><td class=\"mono\">").Text(cols is null ? "—" : string.Join(", ", cols))
                        .Raw("</td><td class=\"n\">").Text(Opt(ix.LeafBlocks)).Raw("</td><td class=\"n\">").Text(Opt(ix.DistinctKeys))
                        .Raw(poor ? "</td><td class=\"n warn-t\">" : "</td><td class=\"n\">").Text(Opt(ix.ClusteringFactor)).Raw("</td></tr>");
                }

                w.Raw("</tbody></table></div><p class=\"note\">").Text(L(en, "CF(clustering factor)가 행 수에 가까우면 인덱스로 읽을 때 블록을 많이 오갑니다.",
                    "A clustering factor close to the row count means index access visits many blocks.")).Raw("</p>");
            }

            w.Raw("\n</div>\n<div>\n");
            if (o.Histograms.Count == 0)
            {
                w.Raw("<h3>").Text(L(en, "히스토그램", "Histograms")).Raw("</h3><p class=\"dim\">").Text(L(en, "조건 열에 히스토그램이 없습니다.", "No histogram on the condition columns.")).Raw("</p>");
            }

            foreach (var (column, kind, buckets) in o.Histograms)
            {
                w.Raw("<h3>").Text(string.Format(Inv, L(en, "히스토그램 · {0}.{1} ({2}, {3} 버킷)", "Histogram · {0}.{1} ({2}, {3} buckets)"), o.Table, column, kind, buckets.Count)).Raw("</h3>");
                Histogram(w, buckets);
            }

            w.Raw("\n</div>\n</div>\n");
            var columns = o.Columns.Where(c => predicateColumns.Contains(c.ColumnName)).ToList();
            if (columns.Count > 0)
            {
                w.Raw("<h3>").Text(L(en, "조건 열 · ", "Condition columns · ") + name).Raw("</h3><div class=\"tw\"><table><thead><tr><th>").Text(L(en, "열", "Column")).Raw("</th><th class=\"n\">")
                    .Text(L(en, "고유 값", "Distinct")).Raw("</th><th class=\"n\">NULL</th><th class=\"n\">").Text(L(en, "밀도", "Density")).Raw("</th><th>")
                    .Text(L(en, "히스토그램", "Histogram")).Raw("</th><th class=\"n\">").Text(L(en, "버킷", "Buckets")).Raw("</th></tr></thead><tbody>");
                foreach (var c in columns)
                {
                    var noHist = string.Equals(c.Histogram, "NONE", StringComparison.OrdinalIgnoreCase);
                    w.Raw("<tr><td class=\"mono\">").Text(c.ColumnName).Raw("</td><td class=\"n\">").Text(Opt(c.NumDistinct)).Raw("</td><td class=\"n\">").Text(Opt(c.NumNulls))
                        .Raw("</td><td class=\"n\">").Text(c.Density is { } den ? den.ToString(den is > 0 and < 0.0001 ? "0.0e+0" : "0.000000", Inv) : string.Empty)
                        .Raw(noHist ? "</td><td class=\"dim\">" : "</td><td>").Text(noHist ? L(en, "없음", "none") : c.Histogram).Raw("</td><td class=\"n\">").Text(Opt(c.NumBuckets)).Raw("</td></tr>");
                }

                w.Raw("</tbody></table></div>\n");
            }
        }
    }

    /// <summary>CF 가 블록 수보다 행 수에 더 가까우면 나쁨(통계 보기 탭과 같은 기준: (CF − 블록) ÷ (행 − 블록) &gt; 0.5).</summary>
    private static bool ClusteringPoor(long? cf, TableStatsRow? table)
    {
        if (cf is not { } c || table?.NumRows is not { } rows || table.Blocks is not { } blocks || rows <= blocks)
        {
            return false;
        }

        return (double)(c - blocks) / (rows - blocks) > 0.5;
    }

    private static void Histogram(Writer w, IReadOnlyList<HistogramBucket> buckets)
    {
        var shown = buckets.OrderByDescending(b => b.Frequency).Take(12).ToList();
        var max = Math.Max(shown.Count == 0 ? 0 : shown.Max(b => b.Frequency), 1);
        w.Raw("<div class=\"hist\">");
        foreach (var b in shown)
        {
            w.Raw("<span class=\"mono\">").Text(Clip(b.EndpointValue ?? b.EndpointNumber.ToString(Inv), 24)).Raw("</span><div class=\"b\" style=\"width:")
                .Raw(Pct((double)b.Frequency / max)).Raw("%\"></div><span class=\"mono\">").Text(Num(b.Frequency)).Raw("</span>");
        }

        w.Raw("</div>");
        if (buckets.Count > shown.Count)
        {
            w.Raw("<p class=\"note\">").Text(string.Format(Inv, L(w.En, "버킷 {0}개 중 큰 것 {1}개.", "Largest {1} of {0} buckets."), buckets.Count, shown.Count)).Raw("</p>");
        }
    }

    // 7. 자식 커서
    private static void CursorsSection(Writer w, StringBuilder chips, TuningReportData d)
    {
        var en = w.En;
        if (FailedPart(w, d.Cursors))
        {
            return;
        }

        var cur = d.Cursors.Value!;
        var hashes = cur.Children.Select(c => c.PlanHashValue).Distinct().Count();
        Chip(chips, "plan_hash " + hashes.ToString(Inv), hashes > 1 ? "warn" : null);
        if (cur.Children.Count == 0)
        {
            w.Raw("<p class=\"dim\">").Text(L(en, "공유 풀에 이 sql_id 의 자식 커서가 없습니다.", "The shared pool has no child cursor for this sql_id.")).Raw("</p>");
            return;
        }

        var rac = d.Context.IsRac || cur.Children.Select(c => c.InstanceId).Distinct().Count() > 1;
        var worstGets = cur.Children.Max(c => c.GetsPerExec ?? 0);
        w.Raw("<div class=\"tw\"><table><thead><tr><th class=\"n\">child</th>").Raw(rac ? "<th class=\"n\">inst</th>" : string.Empty).Raw("<th class=\"n\">plan_hash</th><th>")
            .Text(L(en, "바인드 민감", "Bind sensitive")).Raw("</th><th>").Text(L(en, "인식", "Aware")).Raw("</th><th>").Text(L(en, "공유 가능", "Shareable")).Raw("</th><th class=\"n\">")
            .Text(L(en, "실행", "Execs")).Raw("</th><th class=\"n\">").Text(L(en, "gets/실행", "Gets/exec")).Raw("</th><th class=\"n\">").Text(L(en, "경과/실행", "Elapsed/exec"))
            .Raw("</th><th>").Text(L(en, "불일치 사유", "Mismatch reasons")).Raw("</th></tr></thead><tbody>\n");
        foreach (var c in cur.Children)
        {
            var reasons = cur.Reasons.Where(r => r.ChildNumber == c.ChildNumber).Select(r => r.Reason).Distinct().ToList();
            var bad = cur.Children.Count > 1 && c.GetsPerExec is { } g && g >= worstGets && worstGets > 0;
            w.Raw("<tr><td class=\"n\">").Text(c.ChildNumber.ToString(Inv)).Raw("</td>").Raw(rac ? "<td class=\"n\">" + Esc(c.InstanceId?.ToString(Inv) ?? "") + "</td>" : string.Empty)
                .Raw("<td class=\"n\">").Text(c.PlanHashValue.ToString(Inv)).Raw("</td><td>").Text(Yn(c.IsBindSensitive)).Raw("</td><td>").Text(Yn(c.IsBindAware))
                .Raw(c.IsShareable ? "</td><td>" : "</td><td class=\"dim\">").Text(Yn(c.IsShareable)).Raw("</td><td class=\"n\">").Text(Num(c.Executions))
                .Raw(bad ? "</td><td class=\"n warn-t\">" : "</td><td class=\"n\">").Text(c.GetsPerExec is { } gp ? Num(gp) : "—")
                .Raw("</td><td class=\"n\">").Text(c.ElapsedPerExecMicros is { } ep ? Num(ep / 1000d) + " ms" : "—")
                .Raw("</td><td class=\"mono\" title=\"").Text(string.Join(" / ", reasons.Select(r => CursorMismatchReasons.Meaning(r) ?? r))).Raw("\">")
                .Text(reasons.Count == 0 ? "—" : string.Join(", ", reasons)).Raw("</td></tr>\n");
        }

        w.Raw("</tbody></table></div>\n");
        var meanings = cur.Reasons.Select(r => r.Reason).Distinct().Select(r => (r, CursorMismatchReasons.Meaning(r))).Where(x => x.Item2 is not null).ToList();
        if (meanings.Count > 0)
        {
            w.Raw("<div class=\"pred\">");
            foreach (var (r, meaning) in meanings)
            {
                w.Raw("<div>").Text(r + " — " + meaning).Raw("</div>");
            }

            w.Raw("</div>");
        }

        if (cur.ReasonsFailure is { } rf)
        {
            w.Raw("<p class=\"note danger-t\">").Text(L(en, "불일치 사유 수집 실패: ", "Mismatch reasons failed: ") + rf).Raw("</p>");
        }
    }

    // 8. 대기 분해
    private static void WaitsSection(Writer w, StringBuilder chips, TuningReportData d)
    {
        var en = w.En;
        if (FailedPart(w, d.Waits))
        {
            return;
        }

        var waits = d.Waits.Value!;
        Chip(chips, waits.FromAsh
            ? L(en, "ASH 지난 1 시간 · Diagnostics", "ASH last hour · Diagnostics")
            : string.Format(Inv, L(en, "gv$session 표본 {0} 초 · 팩 없음", "gv$session samples {0} s · no pack"), waits.SampleSeconds));
        var total = waits.Events.Sum(r => r.Samples);
        if (total == 0)
        {
            w.Raw("<p class=\"dim\">").Text(waits.FromAsh
                ? L(en, "지난 1 시간 ASH 에 이 sql_id 의 표본이 없습니다.", "ASH has no sample for this sql_id in the last hour.")
                : L(en, "표본을 모으는 동안 이 sql_id 를 실행하는 활성 세션이 없었습니다. 문장이 도는 중에 다시 만들면 대기가 보입니다.",
                    "No active session ran this sql_id while sampling. Make the report while the statement runs to see its waits.")).Raw("</p>");
            return;
        }

        // 분류 색·「기타」 묶기는 SQL 모니터 대기 분해와 같은 WaitBreakdown 규칙.
        var classes = WaitBreakdown.Classes(waits.Events);
        w.Raw("<div class=\"stack\" role=\"img\" aria-label=\"").Text(L(en, "대기 분류 비율", "Wait class shares")).Raw("\">");
        foreach (var c in classes)
        {
            w.Raw("<span style=\"width:").Raw(Pct(c.Share)).Raw("%;background:").Raw(ToneColor(c.Tone)).Raw("\"></span>");
        }

        w.Raw("</div>\n<div class=\"legend\">");
        foreach (var c in classes)
        {
            var label = c.Tone == WaitTone.Other ? L(en, WaitBreakdown.OtherLabel, "Other") : c.Label;
            w.Raw("<span><i style=\"background:").Raw(ToneColor(c.Tone)).Raw("\"></i>").Text(label + " " + Pct(c.Share) + " %").Raw("</span>");
        }

        var sessions = waits.Events.Any(e => e.Sessions is not null);
        w.Raw("</div>\n<div class=\"tw\" style=\"margin-top:10px\"><table><thead><tr><th>").Text(L(en, "이벤트", "Event")).Raw("</th><th>").Text(L(en, "분류", "Class"))
            .Raw("</th><th class=\"n\">").Text(L(en, "표본", "Samples")).Raw("</th>")
            .Raw(sessions ? "<th class=\"n\">" + Esc(L(en, "세션", "Sessions")) + "</th>" : string.Empty)
            .Raw("<th>").Text(L(en, "비율", "Share")).Raw("</th></tr></thead><tbody>\n");
        foreach (var r in waits.Events)
        {
            w.Raw("<tr><td>").Text(r.Event).Raw("</td><td>").Text(r.WaitClass).Raw("</td><td class=\"n\">").Text(Num(r.Samples)).Raw("</td>")
                .Raw(sessions ? "<td class=\"n\">" + Esc(r.Sessions?.ToString(Inv) ?? "—") + "</td>" : string.Empty)
                .Raw("<td><span class=\"bar\"><i style=\"width:").Raw(Pct(r.Share)).Raw("%;background:").Raw(ToneColor(r.Tone)).Raw("\"></i></span><span class=\"pct\">")
                .Text(Pct(r.Share) + " %").Raw("</span></td></tr>\n");
        }

        w.Raw("</tbody></table></div>");
    }

    /// <summary>대기 색 묶음 → 보고서 CSS 변수(SQL 모니터의 아이콘 브러시와 같은 색 계열).</summary>
    private static string ToneColor(WaitTone tone) => tone switch
    {
        WaitTone.Cpu => "var(--i-green)",
        WaitTone.UserIo => "var(--i-blue)",
        WaitTone.Application => "var(--i-red)",
        WaitTone.Cluster => "var(--i-purple)",
        WaitTone.Concurrency => "var(--i-amber)",
        _ => "var(--i-neutral)",
    };

    // 9. AWR
    private static void AwrSection(Writer w, StringBuilder chips, TuningReportData d)
    {
        var en = w.En;
        if (FailedPart(w, d.Awr))
        {
            return;
        }

        var awr = d.Awr.Value!;
        var rows = awr.Rows;
        var hashes = rows.Select(r => r.PlanHashValue).Distinct().Count();
        Chip(chips, "dba_hist_sqlstat · Diagnostics");
        if (hashes > 1)
        {
            Chip(chips, "plan_hash " + hashes.ToString(Inv), "warn");
        }

        if (rows.Count == 0)
        {
            w.Raw("<p class=\"dim\">").Text(L(en, "AWR 에 이 sql_id 의 기록이 없습니다(스냅숏 사이에 상위 SQL 이 아니었을 수 있습니다).",
                "AWR has no record of this sql_id (it may not have been a top SQL between snapshots).")).Raw("</p>");
            return;
        }

        w.Raw("<div class=\"tw\"><table><thead><tr><th class=\"n\">snap_id</th><th>").Text(L(en, "구간 끝", "Interval end")).Raw("</th><th class=\"n\">plan_hash</th><th class=\"n\">")
            .Text(L(en, "실행", "Execs")).Raw("</th><th class=\"n\">").Text(L(en, "경과/실행", "Elapsed/exec")).Raw("</th><th class=\"n\">").Text(L(en, "gets/실행", "Gets/exec")).Raw("</th></tr></thead><tbody>\n");
        long? previous = null;
        foreach (var r in rows.OrderBy(r => r.SnapId))
        {
            var changed = previous is { } p && p != r.PlanHashValue;
            previous = r.PlanHashValue;
            var exec = Math.Max(r.Executions, 1);
            w.Raw(changed ? "<tr class=\"chg\">" : "<tr>").Raw("<td class=\"n\">").Text(r.SnapId.ToString(Inv)).Raw("</td><td class=\"mono\">").Text(r.SnapTime.ToString("yyyy-MM-dd HH:mm", Inv))
                .Raw("</td><td class=\"n\">").Text(r.PlanHashValue.ToString(Inv)).Raw("</td><td class=\"n\">").Text(Num(r.Executions)).Raw("</td><td class=\"n\">")
                .Text(Num(r.ElapsedMicros / 1000d / exec) + " ms").Raw("</td><td class=\"n\">").Text(Num((double)r.BufferGets / exec)).Raw("</td></tr>\n");
        }

        w.Raw("</tbody></table></div>");
        if (hashes > 1)
        {
            w.Raw("<p class=\"note\">").Text(L(en, "칠한 줄 = 앞 구간과 plan_hash 가 바뀐 곳.", "Highlighted rows = plan_hash changed from the previous interval.")).Raw("</p>");
        }

        // 가장 빠른·느린 plan_hash 의 AWR 계획(dba_hist_sql_plan, 실제 통계 없음 — 비용 비중).
        for (var i = 0; i < awr.Plans.Count; i++)
        {
            var p = awr.Plans[i];
            w.Raw("<h3>").Text((i == 0 ? L(en, "가장 빠른 plan_hash ", "Fastest plan_hash ") : L(en, "가장 느린 plan_hash ", "Slowest plan_hash ")) + p.PlanHashValue.ToString(Inv)).Raw("</h3>");
            if (p.Plan is not { Steps.Count: > 0 } plan)
            {
                Failed(w, p.Failure ?? L(en, "AWR 에 이 plan_hash 의 계획 단계가 없습니다.", "AWR has no plan steps for this plan_hash."));
                continue;
            }

            PlanTable(w, plan);
        }
    }

    // 10. 후보
    private static void CandidatesSection(Writer w, StringBuilder chips, TuningReportData d)
    {
        var en = w.En;
        Chip(chips, L(en, "규칙 기반 · 실행 안 함", "rule based · not run"));
        if (FailedPart(w, d.Candidates))
        {
            return;
        }

        var c = d.Candidates.Value!;
        w.Raw("<h3>").Text(L(en, "인덱스 후보", "Index candidates")).Raw("</h3>\n");
        if (c.Indexes.Count == 0)
        {
            w.Raw("<p class=\"dim\">").Text(FirstPlan(d) is null
                ? L(en, "실제 계획이 없어 인덱스 후보를 고르지 않았습니다.", "No actual plan, so no index candidates.")
                : L(en, "규칙에 걸린 곳이 없습니다 — 전체 읽기 단계가 없거나 조건 열이 없습니다.", "No rule matched — no full scan step or no condition column.")).Raw("</p>\n");
        }

        foreach (var a in c.Indexes)
        {
            var comment = new StringBuilder();
            comment.Append(L(en, "-- 근거: ", "-- Basis: ")).Append(a.BasisText.Length > 0 ? a.BasisText + " · " : string.Empty).Append(a.Reason.ReplaceLineEndings(" "));
            if (a.Candidate.OverlappingIndex is { } ov)
            {
                comment.Append('\n').Append(a.Overlap switch
                {
                    IndexOverlapKind.Covered => string.Format(Inv, L(en, "-- 겹침: 기존 {0} 이(가) 이미 덮습니다 → 새로 만들 필요 없음", "-- Overlap: existing {0} already covers it → not needed"), ov),
                    IndexOverlapKind.Extends => string.Format(Inv, L(en, "-- 겹침: {0} 의 앞 열과 같음 → 이 인덱스를 넓히는 안도 있습니다", "-- Overlap: same leading columns as {0} → widening it is an option"), ov),
                    _ => string.Format(Inv, L(en, "-- 겹침: {0} 와 첫 열이 같음", "-- Overlap: shares the first column with {0}"), ov),
                });
            }
            else if (a.Overlap == IndexOverlapKind.Unknown)
            {
                comment.Append('\n').Append(L(en, "-- 겹침: 기존 인덱스를 확인하지 못했습니다", "-- Overlap: existing indexes could not be checked"));
            }

            w.Raw("<pre class=\"code\"><span class=\"cm\">").Text(comment.ToString()).Raw("</span>\n").Raw(Highlight(a.Candidate.Ddl)).Raw("</pre>\n");
        }

        w.Raw("<h3>").Text(L(en, "재작성 후보", "Rewrite candidates")).Raw("</h3>\n");
        if (c.Rewrites.Count == 0)
        {
            w.Raw("<p class=\"dim\">").Text(L(en, "규칙에 걸린 곳이 없습니다.", "No rule matched.")).Raw("</p>");
            return;
        }

        w.Raw("<div class=\"tw\"><table><thead><tr><th>").Text(L(en, "규칙", "Rule")).Raw("</th><th>").Text(L(en, "설명", "Description")).Raw("</th><th>").Text(L(en, "주의", "Caution"))
            .Raw("</th></tr></thead><tbody>\n");
        foreach (var r in c.Rewrites)
        {
            w.Raw("<tr><td>").Text(r.Title).Raw("</td><td class=\"wrap\">").Text(r.Candidate.Description).Raw(r.Caution is null ? "</td><td class=\"dim\">" : "</td><td class=\"wrap warn-t\">")
                .Text(r.Caution ?? "—").Raw("</td></tr>\n");
        }

        w.Raw("</tbody></table></div>\n");
        foreach (var r in c.Rewrites.Where(r => r.HasRewrite))
        {
            w.Raw("<h3>").Text(r.Title).Raw("</h3><pre class=\"code\">").Raw(Highlight(r.Candidate.Sql)).Raw("</pre>\n");
        }
    }

    // 11. 환경
    private static void EnvironmentSection(Writer w, StringBuilder chips, TuningReportData d)
    {
        var en = w.En;
        if (FailedPart(w, d.Environment))
        {
            return;
        }

        var rows = d.Environment.Value!;
        foreach (var v in rows.Where(r => r.Kind == TuningEnvKind.Version))
        {
            w.Raw("<p class=\"mono\" style=\"margin:0 0 10px\">").Text(v.Value).Raw("</p>");
        }

        w.Raw("<div class=\"grid2\">\n");
        EnvTable(w, L(en, "optimizer 매개변수", "Optimizer parameters"), rows.Where(r => r.Kind == TuningEnvKind.Parameter));
        EnvTable(w, "NLS", rows.Where(r => r.Kind == TuningEnvKind.Nls));
        w.Raw("</div>");
    }

    private static void EnvTable(Writer w, string head, IEnumerable<TuningEnvRow> rows)
    {
        w.Raw("<div class=\"tw\"><table><thead><tr><th>").Text(head).Raw("</th><th>").Text(L(w.En, "값", "Value")).Raw("</th></tr></thead><tbody>");
        foreach (var r in rows)
        {
            w.Raw("<tr><td class=\"mono\">").Text(r.Name).Raw("</td><td class=\"mono\">").Text(r.Value ?? string.Empty).Raw("</td></tr>");
        }

        w.Raw("</tbody></table></div>\n");
    }

    // ---- 값 서식 ----

    internal static string StepLabel(PlanStep s) =>
        s.Operation + (string.IsNullOrEmpty(s.Options) ? string.Empty : " " + s.Options) + (ObjectName(s) is { Length: > 0 } o ? " " + o : string.Empty);

    internal static string ObjectName(PlanStep s) =>
        string.IsNullOrEmpty(s.ObjectName) ? string.Empty : string.IsNullOrEmpty(s.ObjectOwner) ? s.ObjectName : s.ObjectOwner + "." + s.ObjectName;

    private static string AnalyzedAgo(DateTime? at, DateTimeOffset now, bool en)
    {
        if (at is not { } a)
        {
            return L(en, "수집 안 됨", "never");
        }

        var days = (now.DateTime.Date - a.Date).Days;
        return days <= 0 ? L(en, "오늘", "today") : string.Format(Inv, L(en, "{0} 일 전", "{0} days ago"), days);
    }

    internal static string Num(double v) =>
        Math.Abs(v) >= 100 || v == Math.Floor(v) ? Math.Round(v).ToString("N0", Inv) : v.ToString(Math.Abs(v) >= 10 ? "N1" : "N2", Inv);

    internal static string Num(long v) => v.ToString("N0", Inv);

    private static string Opt(long? v) => v is { } x ? x.ToString("N0", Inv) : string.Empty;

    internal static string Pct(double share) => Math.Round(Math.Clamp(share, 0, 1) * 100).ToString("0", Inv);

    private static string Yn(bool v) => v ? "Y" : "N";

    private static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss", Inv);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string L(bool en, string ko, string english) => en ? english : ko;

    /// <summary>글 조각을 모으는 도우미: <see cref="Text"/> 는 늘 이스케이프, <see cref="Raw"/> 는 생성기가 만든 태그만.</summary>
    private sealed class Writer(bool en)
    {
        private readonly StringBuilder _sb = new(64 * 1024);

        public bool En { get; } = en;

        public Writer Raw(string s)
        {
            _sb.Append(s);
            return this;
        }

        public Writer Text(string? s)
        {
            _sb.Append(Esc(s));
            return this;
        }

        public override string ToString() => _sb.ToString();
    }
}
