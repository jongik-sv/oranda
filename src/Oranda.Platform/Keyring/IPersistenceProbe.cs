namespace Oranda.Platform.Keyring;

/// <summary>
/// 비밀 저장소가 재시작 뒤에도 값을 지키는지 실행 중에 알려 준다. 키링을 못 쓰면 메모리로 대신하는 저장소가 단다.
/// 원복 금고(<c>RollbackVault.IsDurable</c>)가 이 값으로 원복 스크립트를 만들지 정한다.
/// </summary>
internal interface IPersistenceProbe
{
    /// <summary>재시작 뒤에도 값이 남으면 true.</summary>
    bool IsPersistent { get; }
}
