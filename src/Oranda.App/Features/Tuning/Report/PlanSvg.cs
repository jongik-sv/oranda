using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Oranda.App.Features.ExecutionPlan;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Features.Tuning.Report;

/// <summary>
/// 계획 트리를 인라인 SVG 로(보고서 4 절). 배치·비중 규칙은 계획 창 그래프 탭의 <see cref="PlanGraphLayout"/> 를 그대로 쓴다
/// (너비 = 자체 비용 비중, 색 진하기 = 자체 시간 비중, 가장 비싼 단계 = 주황, 위험 단계 = 주황 테두리). 보고서는 접지 않고 모두 그린다.
/// 색은 CSS 변수만 쓴다(라이트·다크·인쇄를 보고서 CSS 가 정한다). 모든 글은 이스케이프한다.
/// </summary>
internal static class PlanSvg
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Render(CorePlan plan, PlanMetrics metrics, bool en)
    {
        var layout = PlanGraphLayout.Compute(plan.Steps, metrics, PlanGraphColorBasis.Time, new HashSet<int>());
        var sb = new StringBuilder();
        sb.Append("<svg viewBox=\"0 0 ").Append(F(layout.Width)).Append(' ').Append(F(layout.Height)).Append("\" width=\"").Append(F(layout.Width)).Append("\" height=\"").Append(F(layout.Height))
            .Append("\" role=\"img\" aria-label=\"").Append(TuningReportHtml.Esc(en ? "Plan graph" : "계획 그래프")).Append("\">\n");
        foreach (var e in layout.Edges)
        {
            sb.Append("<path class=\"n-edge\" d=\"");
            for (var i = 0; i < e.Points.Count; i++)
            {
                sb.Append(i == 0 ? "M" : " L").Append(F(e.Points[i].X)).Append(' ').Append(F(e.Points[i].Y));
            }

            sb.Append("\"/>\n");
        }

        foreach (var n in layout.Nodes)
        {
            var step = plan.Steps[n.Index];
            var tip = TuningReportHtml.StepLabel(step) + (n.Detail.Length > 0 ? " — " + n.Detail : string.Empty);
            sb.Append("<g><title>").Append(TuningReportHtml.Esc(tip)).Append("</title><rect x=\"").Append(F(n.X)).Append("\" y=\"").Append(F(n.Y))
                .Append("\" width=\"").Append(F(n.Width)).Append("\" height=\"").Append(F(n.Height)).Append("\" rx=\"6\"");
            if (n.IsHottest)
            {
                sb.Append(" class=\"n-hot\"");
            }
            else if (n.Intensity > 0.005)
            {
                sb.Append(" class=\"n-heat\" fill-opacity=\"").Append((0.10 + 0.32 * n.Intensity).ToString("0.00", Inv)).Append('"');
            }
            else
            {
                sb.Append(" class=\"n-box\"");
            }

            if (n.IsRisky && !n.IsHottest)
            {
                sb.Append(" style=\"stroke:var(--i-orange);stroke-width:2\"");
            }

            var chars = (int)System.Math.Max(6, (n.Width - 8) / 6.2);
            sb.Append("/><text class=\"").Append(n.IsHottest ? "n-ht" : "n-t").Append("\" x=\"").Append(F(n.CenterX)).Append("\" y=\"").Append(F(n.Y + 15))
                .Append("\" text-anchor=\"middle\">").Append(TuningReportHtml.Esc(Clip(n.Title, chars))).Append("</text>");
            if (n.Detail.Length > 0)
            {
                sb.Append("<text class=\"").Append(n.IsHottest ? "n-hs" : "n-s").Append("\" x=\"").Append(F(n.CenterX)).Append("\" y=\"").Append(F(n.Y + 29))
                    .Append("\" text-anchor=\"middle\">").Append(TuningReportHtml.Esc(Clip(n.Detail, (int)(chars * 1.1)))).Append("</text>");
            }

            sb.Append("</g>\n");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..System.Math.Max(max - 1, 1)] + "…";

    private static string F(double v) => System.Math.Round(v, 1).ToString("0.#", Inv);
}
