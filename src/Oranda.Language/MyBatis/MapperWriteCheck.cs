namespace Oranda.Language.MyBatis;

/// <summary>
/// 쓰기 전 검사 결과(MB-06, design.md 5.4). 계약(TSK-01-02).
/// <see cref="CanWrite"/> 가 거짓이면 쓰지 않는다. <see cref="ChangedOnDisk"/> 는 쓰기를 막지 않고 3방향 병합을 먼저 하라는 신호다.
/// </summary>
/// <param name="XmlError">잘 짜인 매퍼가 아니면 이유(줄·칸 포함). 루트가 mapper 가 아닌 경우 포함. 문제없으면 null.</param>
/// <param name="DuplicateIds">겹치는 최상위 요소 id(정렬).</param>
/// <param name="ChangedOnDisk">연 때 해시와 디스크 해시가 다르다.</param>
public sealed record MapperWriteCheck(string? XmlError, IReadOnlyList<string> DuplicateIds, bool ChangedOnDisk)
{
    public bool IsWellFormed => XmlError is null;

    public bool CanWrite => XmlError is null && DuplicateIds.Count == 0;
}
