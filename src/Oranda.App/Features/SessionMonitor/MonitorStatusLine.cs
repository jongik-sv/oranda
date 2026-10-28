using System;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>모니터 화면 아래 상태 줄 하나를 만드는 글 규칙.</summary>
public static class MonitorStatusLine
{
    /// <summary>
    /// 화면 모델의 상태 글에 고른 행 수를 합쳐 한 줄로 만든다. 상태 글이 「… · 시각 갱신」 꼴이면 마지막 토막 앞에 「N개 선택」 을 끼우고,
    /// 아니면 뒤에 붙인다. 고른 행이 없으면 상태 글 그대로.
    /// </summary>
    public static string Compose(string status, int selected)
    {
        if (selected <= 0)
        {
            return status;
        }

        var text = selected.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + "개 선택";
        var cut = status.LastIndexOf(" · ", StringComparison.Ordinal);
        return cut < 0 ? status + " · " + text : status[..cut] + " · " + text + status[cut..];
    }
}
