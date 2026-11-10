using Oranda.Core.Events;

namespace Oranda.Core.Metadata;

/// <summary>객체 목록 캐시를 서버 목록에 맞춘 한 번의 건수: 새로 생긴 것·마지막 DDL 시각이나 상태가 바뀐 것·사라진 것.</summary>
public sealed record ObjectListChange(int Added, int Changed, int Removed)
{
    /// <summary>바뀐 것 없음(캐시를 쓰지 않는 구현의 기본 답).</summary>
    public static ObjectListChange None { get; } = new(0, 0, 0);

    /// <summary>추가·바뀜·지움을 모두 더한 수.</summary>
    public int Total => Added + Changed + Removed;
}

/// <summary>
/// 메타데이터 제공자 → 스키마 브라우저. 서버의 전체 객체 목록을 받아 캐시에 맞췄다(<see cref="CachedMetadataProvider"/> 의 전체 갱신이 끝날 때마다).
/// 바뀐 것이 없어도 낸다: 화면의 「목록 기준 시각」(<paramref name="RefreshedAt"/>, UTC)이 따라 바뀐다. 바뀐 객체 알림은 <see cref="MetadataInvalidated"/> 가 따로 맡는다.
/// </summary>
public sealed record ObjectListRefreshed(string DbKey, DateTimeOffset RefreshedAt, ObjectListChange Change) : OrandaEvent;
