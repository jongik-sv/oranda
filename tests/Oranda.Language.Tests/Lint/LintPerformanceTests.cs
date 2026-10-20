using System.Diagnostics;
using System.Globalization;
using System.Text;
using Oranda.Language.Lint;
using Xunit.Abstractions;

namespace Oranda.Language.Tests.Lint;

/// <summary>점검(lint) 단계가 1만 줄 문서에서 쓰는 시간(TSK-04-09 지연 예산 안에 드는지 보는 측정).</summary>
public class LintPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Ten_thousand_statements_are_linted_within_budget()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10_000; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"SELECT a.x, b.y FROM t{i % 50} a, u b WHERE a.id = b.id AND a.c = 'v{i}' ORDER BY a.x;\n");
        }

        var text = sb.ToString();
        var svc = new LintService(lookup: null);

        long t0 = Stopwatch.GetTimestamp();
        var cold = svc.Lint(text, null, CancellationToken.None);
        var coldMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        // 한 문장만 바꾸고 다시 점검: 바뀌지 않은 문장은 구조를 캐시에서 쓴다.
        var edited = text.Replace("v5'", "v5x'", StringComparison.Ordinal);
        t0 = Stopwatch.GetTimestamp();
        var warm = svc.Lint(edited, null, CancellationToken.None);
        var warmMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"lint 1만 문장: 첫 점검 {coldMs:F0} ms, 다시 점검 {warmMs:F0} ms (경고 {cold.Count}/{warm.Count})"));
        Assert.True(coldMs < 3000, $"첫 점검이 {coldMs:F0} ms");
        Assert.True(warmMs < 1000, $"다시 점검이 {warmMs:F0} ms");
    }
}
