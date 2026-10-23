using System.Diagnostics;
using System.Globalization;
using System.Text;
using Oranda.Language.MyBatis;
using Xunit.Abstractions;

namespace Oranda.Language.Tests.MyBatis;

// 소유: TSK-02-01. acceptance 「1MB 매퍼 파싱 200 ms 이하」(Perf 범주, 따로 실행).
public sealed class MapperDocumentPerfTests(ITestOutputHelper output)
{
    private const double LimitMs = 200;

    [Fact]
    [Trait("Category", "Perf")]
    public void Parse_1MbMapper_Under200Ms()
    {
        var bytes = BigMapper(out int count);
        var parser = new MapperParser();
        parser.Parse(bytes); // 워밍업(JIT)

        var sw = Stopwatch.StartNew();
        var doc = parser.Parse(bytes);
        sw.Stop();

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"1MB 매퍼({bytes.Length:N0} 바이트, 요소 {count:N0}개) 파싱 {sw.Elapsed.TotalMilliseconds:F1} ms (상한 {LimitMs} ms)"));
        Assert.Equal(count, doc.Elements.Count);
        Assert.True(sw.Elapsed.TotalMilliseconds <= LimitMs, $"1MB 파싱 {sw.Elapsed.TotalMilliseconds:F1} ms > {LimitMs} ms");
    }

    private static byte[] BigMapper(out int count)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<mapper namespace=\"big\">\n");
        count = 0;
        while (sb.Length < 1_048_576)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"  <select id=\"s{count}\" resultType=\"map\">\n    SELECT A, B, '한글' FROM T WHERE X = #{{x}}\n    <if test=\"y != null\">AND Y = #{{y}}</if>\n  </select>\n\n");
            count++;
        }

        sb.Append("</mapper>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
