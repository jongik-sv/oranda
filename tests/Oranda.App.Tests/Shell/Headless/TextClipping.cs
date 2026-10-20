using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>
/// 글자가 잘리는 곳을 찾는다(ui-shell.md 5장 「글자 영역이 부모 경계를 넘지 않음」):
/// 화면에 보이는 TextBlock 이 ① 원하는 폭보다 좁게 배치됐거나(자름 표시가 없는데 잘림) ② 창 경계 밖으로 나가면 위반이다.
/// </summary>
public static class TextClipping
{
    public static IReadOnlyList<string> Find(TopLevel top)
    {
        var offenders = new List<string>();
        foreach (var tb in top.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!tb.IsEffectivelyVisible || string.IsNullOrEmpty(tb.Text) || tb.Bounds.Width <= 0)
            {
                continue;
            }

            // 세로 스크롤 뷰포트 경계에 걸치거나 밖에 있는 항목(목록의 미리 만든 항목·스크롤 중인 줄)은 뷰포트가 자르므로 보지 않는다. 가로로 넘치는 것은 그대로 본다.
            if (IsCutByScrollViewport(tb))
            {
                continue;
            }

            // 의도한 자르기(말줄임 + 도움말)는 허용한다. 줄바꿈 글은 폭에 맞춰 늘어나므로 높이로 본다.
            if (tb.TextTrimming != TextTrimming.None)
            {
                continue;
            }

            var label = $"'{tb.Text}' ({Path(tb)})";
            var needW = tb.DesiredSize.Width - tb.Margin.Left - tb.Margin.Right;
            var needH = tb.DesiredSize.Height - tb.Margin.Top - tb.Margin.Bottom;
            if (tb.TextWrapping == TextWrapping.NoWrap && tb.Bounds.Width + 0.75 < needW)
            {
                offenders.Add($"{label}: 배치 폭 {tb.Bounds.Width:0.#} < 필요 폭 {needW:0.#}");
            }

            if (tb.Bounds.Height + 0.75 < needH)
            {
                offenders.Add($"{label}: 배치 높이 {tb.Bounds.Height:0.#} < 필요 높이 {needH:0.#}");
            }

            var origin = tb.TranslatePoint(new Point(0, 0), top);
            var far = tb.TranslatePoint(new Point(tb.Bounds.Width, tb.Bounds.Height), top);
            if (origin is { } o && far is { } f && (o.X < -0.75 || o.Y < -0.75 || f.X > top.Bounds.Width + 0.75 || f.Y > top.Bounds.Height + 0.75))
            {
                offenders.Add($"{label}: 창 경계({top.Bounds.Width:0.#}×{top.Bounds.Height:0.#})를 넘는다 ({o.X:0.#},{o.Y:0.#})-({f.X:0.#},{f.Y:0.#})");
            }
        }

        return offenders;
    }

    private static bool IsCutByScrollViewport(Control v)
    {
        foreach (var presenter in v.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>())
        {
            var origin = v.TranslatePoint(new Point(0, 0), presenter);
            var far = v.TranslatePoint(new Point(v.Bounds.Width, v.Bounds.Height), presenter);
            if (origin is { } o && far is { } f && (o.Y < 0 || f.Y > presenter.Bounds.Height))
            {
                return true;
            }
        }

        return false;
    }

    private static string Path(Visual v)
    {
        var names = new List<string>();
        for (Visual? cur = v; cur is not null && names.Count < 4; cur = cur.GetVisualParent())
        {
            names.Add(cur is Control { Name: { Length: > 0 } n } ? $"{cur.GetType().Name}#{n}" : cur.GetType().Name);
        }

        return string.Join("<", names);
    }
}
