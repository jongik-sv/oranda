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
}
