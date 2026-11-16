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
            w.Raw("<p class=\"dim\">").Text(L(en, "계획에서 읽는 테이블이 없습니다.", "The plan reads no table.")).Raw("</p>");
            return;
        }

        w.Raw("<h3>").Text(L(en, "테이블", "Tables")).Raw("</h3>\n<div class=\"tw\"><table><thead><tr><th>").Text(L(en, "테이블", "Table")).Raw("</th><th class=\"n\">")
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
}
