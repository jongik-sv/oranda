using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Tests.Transfer.Copy;

// 소유: 데이터 복사 Task 9. 시퀀스 보정에 소비할 NEXTVAL 개수(설계 8.4). 값을 내리지 않는다.
public class SequenceAdvanceTests
{
    [Theory]
    [InlineData(101, 500, 1, 400)]
    [InlineData(1000, 500, 1, 0)]
    [InlineData(10, 100, 5, 19)]
    [InlineData(501, 500, 1, 0)]
    [InlineData(500, 500, 1, 1)]
    [InlineData(10, 100, 0, 0)]
    [InlineData(10, 100, -1, 0)]
    public void Plan_consumes_until_next_value_exceeds_max(long current, long max, long increment, long expected)
    {
        Assert.Equal(expected, SequenceAdvance.Plan(current, max, increment));
    }
}
