using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.Core.Composition;

namespace Oranda.App.Features.MyBatisWorkspace.Folder;

/// <summary>
/// 매퍼 폴더에서 문장 찾기(MB-09·MB-10, <see cref="IStatementLocator"/> 의 <c>folder</c> 구현). 03-06 로그 붙여 넣기와 「MyBatis 문장 찾기」가 함께 쓴다.
/// 열린 폴더의 색인만 본다(폴더가 없거나 훑는 중이면 빈 결과).
/// </summary>
[OrandaService(typeof(IStatementLocator))]
public sealed class FolderStatementLocator(MapperFolderService folder) : IStatementLocator
{
    public Task<IReadOnlyList<MyBatisStatementLocation>> FindAsync(string query, bool exact, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(folder.Index?.FindStatements(query, exact) ?? []);
    }
}
