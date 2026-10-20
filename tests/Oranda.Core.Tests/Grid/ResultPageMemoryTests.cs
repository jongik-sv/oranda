using System.Globalization;
using Oranda.Core.Grid;
using Xunit.Abstractions;

namespace Oranda.Core.Tests.Grid;

// 소유: TSK-06-12. PRD 7.2 「추가 메모리 행당 200 bytes」(D-023: 데이터 포함, 6열 NUMBER·VARCHAR2·NUMBER·DATE·NUMBER·VARCHAR2).
public sealed class ResultPageMemoryTests(ITestOutputHelper output)
{
    private const int Rows = 200_000, PageSize = 500;

    private static readonly string[] Statuses = ["NEW", "PAID", "SHIPPED", "CLOSED"];

    [Fact]
    [Trait("Category", "Perf")]
    public void Managed_heap_bytes_per_row_for_six_columns_with_and_without_sharing()
    {
        var plain = BytesPerRow(share: false);
        var shared = BytesPerRow(share: true);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"6열 {Rows:N0}행 관리 힙: 공유 없음 {plain:F1} bytes/행, 값 공유 {shared:F1} bytes/행"));
        Assert.True(shared < plain, $"값 공유가 행당 메모리를 줄이지 못했다: {plain:F1} → {shared:F1}");
    }

    [Fact]
    public void Sharing_keeps_values_and_decimal_scale()
    {
        var sharer = new PageValueSharer(3);
        var a = sharer.Share(0, 1.0m);
        var b = sharer.Share(0, 1m);
        var c = sharer.Share(0, 1.0m);
        Assert.Equal("1.0", ((decimal)a!).ToString(CultureInfo.InvariantCulture));
        Assert.Equal("1", ((decimal)b!).ToString(CultureInfo.InvariantCulture));
        Assert.Same(a, c);
        Assert.NotSame(a, b);

        var s1 = sharer.Share(1, new string("ACTIVE".AsSpan()));
        var s2 = sharer.Share(1, new string("ACTIVE".AsSpan()));
        Assert.Same(s1, s2);
        Assert.Null(sharer.Share(2, null));
        var d = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Same(sharer.Share(2, d), sharer.Share(2, d));
        Assert.Equal(DateTimeKind.Utc, ((DateTime)sharer.Share(2, d)!).Kind);
    }

    /// <summary>OracleResultSet 이 만드는 것과 같은 모양(열마다 <c>List&lt;object?&gt;</c>)의 페이지를 쌓고 GC 뒤 힙 증가를 행 수로 나눈다.</summary>
    private static double BytesPerRow(bool share)
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var pages = new List<ResultPage>(Rows / PageSize);
        for (var p = 0; p < Rows / PageSize; p++)
        {
            var buffers = new List<object?>[6];
            for (var c = 0; c < 6; c++)
            {
                buffers[c] = new List<object?>(PageSize);
            }

            var sharer = share ? new PageValueSharer(6) : null;
            for (var r = 0; r < PageSize; r++)
            {
                var i = (p * PageSize) + r;
                object?[] row =
                [
                    (decimal)i,                                           // 고유 번호
                    new string("CUST-" + (i % 997).ToString(CultureInfo.InvariantCulture)), // 거래처 약 1천 종
                    (decimal)((i % 5000) * 100 + 0.50m),                  // 금액(반복 많음)
                    new DateTime(2026, 1, 1).AddDays(i % 365),            // 날짜 365종
                    (decimal)(i % 10),                                    // 수량(작은 정수)
                    new string(Statuses[i % 4].AsSpan()), // 상태 4종
                ];
                for (var c = 0; c < 6; c++)
                {
                    buffers[c].Add(sharer is null ? row[c] : sharer.Share(c, row[c]));
                }
            }

            pages.Add(new ResultPage(p, (long)p * PageSize, PageSize, buffers));
        }

        var after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(pages);
        return (after - before) / (double)Rows;
    }
}
