namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 시퀀스 보정 계산(설계 8.4). 대상 계정은 <c>ALTER SEQUENCE</c> 권한이 없을 수 있으므로 <c>NEXTVAL</c> 을 모자란 수만큼 소비한다.
/// 시퀀스 값은 내리지 않는다.
/// </summary>
internal static class SequenceAdvance
{
    /// <summary>한 번에 <c>CONNECT BY LEVEL &lt;= :n</c> 으로 소비하는 개수.</summary>
    public const int ChunkSize = 10_000;

    /// <summary>
    /// 소비할 <c>NEXTVAL</c> 개수(0 이상). 다 소비하면 다음 값이 <paramref name="maxValue"/> 보다 커진다.
    /// </summary>
    /// <param name="current">다음 <c>NEXTVAL</c> 이 돌려줄 값(아직 소비하지 않은 값)</param>
    /// <param name="maxValue">열의 최대값</param>
    /// <param name="increment">시퀀스 증가값. 0 이하(내림차순)면 보정하지 않는다</param>
    public static long Plan(long current, long maxValue, long increment)
    {
        if (increment <= 0 || current > maxValue)
        {
            return 0;
        }

        // current, current+inc, … 중 maxValue 이하인 값의 개수.
        return checked(((maxValue - current) / increment) + 1);
    }
}
