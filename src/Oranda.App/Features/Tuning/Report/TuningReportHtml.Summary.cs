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
}
