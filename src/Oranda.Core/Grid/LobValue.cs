using Oranda.Core.Sessions;

namespace Oranda.Core.Grid;

public enum LobKind
{
    Clob,
    NClob,
    Blob,
    BFile,
}

/// <summary>
/// 결과 셀의 LOB 값. 앞부분만 미리 가져오고(<c>grid.initialLobFetchSize</c>) 나머지는 <see cref="ILobAccessor"/> 로 연다.
/// <see cref="Locator"/> 는 드라이버 구현이 쓰는 불투명 값이다.
/// </summary>
public sealed record LobValue(LobKind Kind, long Length)
{
    public string? TextPreview { get; init; }

    public ReadOnlyMemory<byte> BinaryPreview { get; init; }

    /// <summary>미리보기가 전체를 담지 못했으면 true.</summary>
    public bool IsTruncated { get; init; }

    public object? Locator { get; init; }
}

/// <summary>LOB 전체 읽기(TSK-06-05). 쓰기는 바인드(<c>BindDataType.Clob/Blob</c>)로 실행 엔진을 거친다.</summary>
public interface ILobAccessor
{
    /// <summary>CLOB 은 UTF-8 텍스트 스트림, BLOB 은 원시 바이트 스트림.</summary>
    Task<Stream> OpenReadAsync(ISession s, LobValue lob, CancellationToken ct);
}
