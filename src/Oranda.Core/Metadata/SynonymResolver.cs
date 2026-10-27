namespace Oranda.Core.Metadata;

/// <summary>
/// 이름 풀이 한 단계(<c>소유자.이름</c>)의 답: 그 스키마의 표·뷰(<see cref="IsSynonym"/> 거짓)이거나,
/// 시노님이면 가리키는 소유자·이름·DB 링크. 사전에 그 이름이 없으면 단계 자체가 null 이다.
/// </summary>
public sealed record NameStep(bool IsSynonym, string? TargetOwner = null, string? TargetName = null, string? DbLink = null)
{
    public static NameStep TableOrView { get; } = new(false);

    public static NameStep Synonym(string? owner, string name, string? dbLink = null) => new(true, owner, name, dbLink);
}

/// <summary>시노님을 풀 수 없을 때(DB 링크·순환·너무 긴 사슬). 글은 사용자에게 그대로 보인다.</summary>
public sealed class SynonymResolutionException(string message) : InvalidOperationException(message);

/// <summary>
/// 결과의 표 이름이 시노님이면 실제 소유자.표로 푼다(플래시백·그리드 편집 대상). 오라클 이름 풀이와 같은 순서:
/// 그 스키마의 표·뷰 → 그 스키마의 개인 시노님 → 스키마를 적지 않았을 때만 PUBLIC 시노님. 시노님이 시노님을 가리키면
/// <see cref="MaxDepth"/> 단계까지 따라가고 순환은 막는다. 시노님이 아니거나 사전에 없는 이름은 그대로 돌려준다(없음은 부른 쪽이 알린다).
/// </summary>
public static class SynonymResolver
{
    /// <summary>따라갈 시노님 단계 상한.</summary>
    public const int MaxDepth = 8;

    /// <summary>
    /// <paramref name="table"/> 을 푼다. <paramref name="step"/> 은 (소유자, 이름) 한 단계의 답(사전에 없으면 null).
    /// <paramref name="ownerWritten"/> 은 SQL 에 스키마를 적었는지(적었으면 PUBLIC 시노님을 보지 않는다).
    /// DB 링크 시노님·순환·상한 초과는 <see cref="SynonymResolutionException"/>.
    /// </summary>
    public static async Task<DbObjectRef> ResolveAsync(Func<string, string, Task<NameStep?>> step, DbObjectRef table, bool ownerWritten)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(table);
        var start = $"{table.Owner}.{table.Name}";
        var found = await step(table.Owner, table.Name).ConfigureAwait(false);
        var (owner, name) = (table.Owner, table.Name);
        if (found is null && !ownerWritten)
        {
            found = await step("PUBLIC", table.Name).ConfigureAwait(false);
            owner = "PUBLIC";
        }

        if (found is not { IsSynonym: true })
        {
            return table; // 표·뷰이거나 사전에 없는 이름(없음은 부른 쪽이 메타데이터를 읽다 알린다)
        }

        var seen = new HashSet<(string, string)> { (owner, name) };
        for (var depth = 1; ; depth++)
        {
            if (found.DbLink is { Length: > 0 } link)
            {
                throw new SynonymResolutionException(
                    $"시노님 {start} 은(는) DB 링크({link})를 거쳐 다른 DB 의 표를 가리켜 플래시백·편집 대상으로 쓸 수 없습니다(이 DB 의 표만 지원).");
            }

            // TABLE_OWNER 가 비어 있으면(드묾) 시노님 소유자 스키마의 이름이다.
            owner = string.IsNullOrEmpty(found.TargetOwner) ? owner : found.TargetOwner;
            name = found.TargetName ?? name;
            if (!seen.Add((owner, name)))
            {
                throw new SynonymResolutionException($"시노님 {start} 이(가) 순환합니다({owner}.{name} 로 되돌아옴). 시노님 정의를 확인하세요.");
            }

            if (depth > MaxDepth)
            {
                throw new SynonymResolutionException($"시노님 {start} 을(를) {MaxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)}단계 넘게 따라가도 표가 나오지 않습니다.");
            }

            found = await step(owner, name).ConfigureAwait(false);
            if (found is not { IsSynonym: true })
            {
                return new DbObjectRef(owner, name, "TABLE"); // 표·뷰, 또는 권한이 없어 사전에 안 보이는 대상(부른 쪽이 읽다 알린다)
            }
        }
    }
}
