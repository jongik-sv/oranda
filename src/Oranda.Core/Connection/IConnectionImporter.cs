namespace Oranda.Core.Connection;

/// <summary>
/// 다른 도구의 접속 목록 가져오기(CONN-09, TSK-03-05). 구현마다 하나의 원본 형식을 맡고 자동 수집된다.
/// 비밀번호를 풀 수 있으면 <see cref="ImportedConnection.Secret"/> 에 담아 돌려주고, 호출자가 곧바로 자격 증명 저장소로 옮긴다.
/// </summary>
public interface IConnectionImporter
{
    /// <summary>화면에 보일 원본 이름(예: "SQL Developer").</summary>
    string SourceName { get; }

    /// <summary>파일 선택 대화상자 필터용 확장자(점 포함, 예: ".json").</summary>
    IReadOnlyList<string> FileExtensions { get; }

    Task<IReadOnlyList<ImportedConnection>> ImportAsync(string path, CancellationToken ct);
}

/// <summary>가져온 접속 1개. <see cref="Secret"/> 은 파일에 쓰지 않는다.</summary>
public sealed record ImportedConnection(ConnectionProfile Profile, string? Warning = null)
{
    public ReadOnlyMemory<byte> Secret { get; init; }
}
