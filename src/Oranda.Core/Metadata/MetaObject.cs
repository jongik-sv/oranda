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
}
