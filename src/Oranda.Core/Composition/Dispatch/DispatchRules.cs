using Oranda.Core.Connection;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>디스패처가 함께 쓰는 DBMS 고르기 규칙(D-046).</summary>
internal static class DispatchRules
{
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

    /// <summary>복사 세션 쌍의 (원본, 대상) DBMS.</summary>
    public static (Dbms Source, Dbms Target) CopyPair(CopySessions sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(sessions.Source);
        ArgumentNullException.ThrowIfNull(sessions.Target);
        return (sessions.Source.Profile.Dbms, sessions.Target.Profile.Dbms);
    }

    /// <summary>서로 다른 DBMS 사이 복사를 지원하는 쌍인가(xdb-copy D1: Oracle→Tibero·Tibero→Oracle).</summary>
    public static bool IsCrossCopyPair(Dbms source, Dbms target) =>
        (source, target) is (Dbms.Oracle, Dbms.Tibero) or (Dbms.Tibero, Dbms.Oracle);

    /// <summary>
    /// 교차 복사(원본·대상 DBMS 가 다름)의 부품 두 벌(원본 벌, 대상 벌) — 두 DBMS 의 키 등록 <see cref="ICopyDbmsParts"/>.
    /// 지원하는 쌍이 아니거나 어느 쪽 DBMS 에 부품이 등록돼 있지 않으면 <see cref="NotSupportedException"/>
    /// (<see cref="DbmsNotSupportedException"/> 아님 — 그 DBMS 구현이 빠진 것이 아니라 이 쌍의 복사를 아직 하지 않는다).
    /// </summary>
    public static (CopyDbmsParts Source, CopyDbmsParts Target) CrossCopyParts(IDbmsServiceSelector selector, Dbms source, Dbms target)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (!IsCrossCopyPair(source, target))
        {
            throw new NotSupportedException($"서로 다른 DBMS 사이 복사는 이 쌍을 지원하지 않는다({source} → {target}).");
        }

        var sourceParts = selector.Find<ICopyDbmsParts>(source)?.Parts;
        var targetParts = selector.Find<ICopyDbmsParts>(target)?.Parts;
        if (sourceParts is null || targetParts is null)
        {
            var missing = sourceParts is null ? source : target;
            throw new NotSupportedException($"서로 다른 DBMS 사이 복사는 아직 지원하지 않는다({source} → {target}, {missing} 복사 부품 없음).");
        }

        return (sourceParts, targetParts);
    }
}
