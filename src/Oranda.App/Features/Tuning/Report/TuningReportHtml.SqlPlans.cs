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

public static partial class TuningReportHtml
{
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
}
