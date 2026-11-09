using Oranda.Core.Connection;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>디스패처가 함께 쓰는 DBMS 고르기 규칙(D-046).</summary>
internal static class DispatchRules
{
    /// <summary>
    /// 복사 세션 쌍의 DBMS. 원본과 대상의 DBMS 가 같을 때만 그 DBMS 이고, 다르면 <see cref="NotSupportedException"/>
    /// (서로 다른 DBMS 사이 복사는 T5 몫이다).
    /// </summary>
    /// <summary>
    /// 구현 고르기를 열거 시작까지 미룬다. 공급자 구현이 async iterator(인자 검사·연결이 첫 <c>MoveNextAsync</c> 에서 일어남)라
    /// 디스패처가 호출 때 고르면 고르기 예외(null 세션·<see cref="DbmsNotSupportedException"/>)와 첫 어셈블리 로드가 <c>await foreach</c> 밖으로 나온다.
    /// 소비자가 <c>WithCancellation</c> 으로 준 토큰은 안쪽 열거에 그대로 넘긴다.
    /// </summary>
    public static async IAsyncEnumerable<T> Deferred<T>(Func<IAsyncEnumerable<T>> start, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in start().WithCancellation(ct).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public static Dbms DbmsOf(CopySessions sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(sessions.Source);
        ArgumentNullException.ThrowIfNull(sessions.Target);
        var source = sessions.Source.Profile.Dbms;
        var target = sessions.Target.Profile.Dbms;
        return source == target
            ? source
            : throw new NotSupportedException($"서로 다른 DBMS 사이 복사는 아직 지원하지 않는다({source} → {target}).");
    }
}
