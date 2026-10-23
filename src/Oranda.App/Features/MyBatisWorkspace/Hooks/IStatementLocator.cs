namespace Oranda.App.Features.MyBatisWorkspace.Hooks;

/// <summary>찾은 문장 하나.</summary>
/// <param name="MapperPath">매퍼 파일 절대 경로.</param>
/// <param name="MapperNamespace">매퍼 namespace(없으면 null).</param>
/// <param name="StatementId">문장 id.</param>
/// <param name="Source">찾은 곳(<c>open</c> = 열린 매퍼, <c>folder</c> = 매퍼 폴더).</param>
public sealed record MyBatisStatementLocation(string MapperPath, string? MapperNamespace, string StatementId, string Source);

/// <summary>
/// 문장 찾기(MB-09·MB-10, design.md 9장·D-006). 계약(TSK-01-02) — 구현은 03-06(열린 매퍼, <c>LogPaste/</c>)·03-07(매퍼 폴더, <c>Folder/</c>).
/// 부르는 곳: 03-06 로그 붙여 넣기(logger = namespace.id, <paramref name="exact"/> 참), 03-07 「MyBatis 문장 찾기」(id 일부).
/// 등록된 구현을 모두 불러 결과를 합친다(같은 매퍼·id 는 하나로).
/// </summary>
public interface IStatementLocator
{
    /// <param name="query"><c>namespace.id</c> 또는 id 일부.</param>
    /// <param name="exact">참이면 <c>namespace.id</c>(또는 id) 와 똑같은 것만.</param>
    Task<IReadOnlyList<MyBatisStatementLocation>> FindAsync(string query, bool exact, CancellationToken ct);
}
