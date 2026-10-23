namespace Oranda.Core.MyBatis;

/// <summary>시나리오가 바뀐 문장(<see cref="IMyBatisScenarioStore.Changed"/>).</summary>
public sealed class MyBatisScenarioChangedEventArgs(string mapperPath, string statementId) : EventArgs
{
    public string MapperPath { get; } = mapperPath;

    public string StatementId { get; } = statementId;
}

/// <summary>
/// 시나리오 저장소(MB-04, TRD 4, design.md 6장). 계약(TSK-01-02) — 구현은 TSK-02-03(로컬 <c>oranda.db</c> 표 <c>mybatis_scenario</c>).
/// 쓰기는 구현이 백그라운드 스레드에서 한다. 매퍼 경로는 정규화한 절대 경로로 비교한다(Windows 는 대소문자 무시).
/// </summary>
public interface IMyBatisScenarioStore
{
    /// <summary>
    /// 문장의 시나리오(순서대로). 경로 + 문장 id 로 찾고, 없으면 <paramref name="ns"/> + 문장 id 로 찾는다(매퍼를 옮긴 경우).
    /// </summary>
    Task<IReadOnlyList<MyBatisScenario>> ListAsync(string mapperPath, string? ns, string statementId, CancellationToken ct);

    /// <summary>
    /// 새로 넣거나(<see cref="MyBatisScenario.Id"/> 가 0) 덮어쓴다. 저장한 모습(새 ID·시각·비밀 값 버림 반영)을 돌려준다.
    /// 새 시나리오는 그 문장 목록의 끝에 놓인다.
    /// </summary>
    Task<MyBatisScenario> SaveAsync(MyBatisScenario scenario, CancellationToken ct);

    Task RenameAsync(long id, string name, CancellationToken ct);

    /// <summary>같은 값으로 새 시나리오를 만들어 원본 바로 뒤에 둔다.</summary>
    Task<MyBatisScenario> DuplicateAsync(long id, string newName, CancellationToken ct);

    Task DeleteAsync(long id, CancellationToken ct);

    /// <summary>목록 순서를 바꾼다. <paramref name="orderedIds"/> = 한 문장의 시나리오 ID 전부를 새 순서로.</summary>
    Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct);

    /// <summary>문장 id 를 바꿨을 때 시나리오가 따라간다(TRD 3.3). 옮긴 시나리오 수를 돌려준다.</summary>
    Task<int> MoveStatementAsync(string mapperPath, string oldId, string newId, CancellationToken ct);

    /// <summary>
    /// 어느 문장의 시나리오가 바뀌었다(저장·이름·복제·지움·순서·옮김). 03-06(로그)·03-04(시나리오로 저장)·03-05(바꾸기)가 만든 시나리오가
    /// 03-02 「파라미터 · 시나리오」 창에 바로 보이게 한다. 백그라운드 스레드에서 날 수 있다(받는 쪽이 UI 스레드로 옮긴다).
    /// </summary>
    event EventHandler<MyBatisScenarioChangedEventArgs>? Changed;
}
