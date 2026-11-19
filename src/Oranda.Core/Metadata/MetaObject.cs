namespace Oranda.Core.Metadata;

/// <summary><c>oranda.db</c> <c>meta_source</c> 행(data-model 3.2). 캐시 단위(접속 대상)별 관리 정보.</summary>
public sealed record MetaSource
{
    public required string DbKey { get; init; }

    public string? ServerVersion { get; init; }

    public DateTimeOffset? RefreshedAt { get; init; }

    public DateTimeOffset LastUsedAt { get; init; }
}

/// <summary>
/// <c>oranda.db</c> <c>meta_object</c> 행. <see cref="Type"/> 은 Oracle OBJECT_TYPE 그대로.
/// <see cref="PayloadJson"/> 은 맨 위 <c>"v"</c> 필드로 형식 버전을 가진다(null = 아직 안 받음).
/// </summary>
public sealed record MetaObject
{
    public required string DbKey { get; init; }

    public required string Owner { get; init; }

    public required string Name { get; init; }

    public required string Type { get; init; }

    public string? Status { get; init; }

    public DateTimeOffset? LastDdlTime { get; init; }

    public string? PayloadJson { get; init; }

    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>
    /// 세부 종류 표식(<see cref="ObjectMark.SubKind"/>, 예 <c>GTT_SESSION PART:RANGE</c>). 목록 질의(<c>meta.objects</c>)는 채우지 않고
    /// 목록 갱신 뒤 보조 질의(<c>meta.marks_*</c>)가 <see cref="Storage.IMetadataCache.SetMarksAsync"/> 로 채운다. 캐시는 <see cref="Storage.IMetadataCache.GetMarkedAsync"/> 에서만
    /// 채워 돌려준다(다른 읽기는 null). null = 표식 없음·모름(예전 캐시).
    /// </summary>
    public string? SubKind { get; init; }

    /// <summary>상태 표식(<see cref="ObjectMark.State"/>, 예 <c>DISABLED</c>·<c>BROKEN</c>). 채우는 방법은 <see cref="SubKind"/> 와 같다.</summary>
    public string? StateMark { get; init; }
}
