using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Composition;

namespace Oranda.App.Features.MyBatisWorkspace.LogPaste;

/// <summary>
/// 열린 매퍼에서 문장 찾기(TSK-03-06, <see cref="IStatementLocator"/> 의 <c>open</c> 구현). 열려 있는 MyBatis 작업 공간의 세션 문서만 훑는다.
/// <paramref name="exact"/> 참이면 <c>namespace.id</c> 가 같은 문장, 거짓이면 그 글자가 <c>namespace.id</c> 안에 든 문장(대소문자 무시).
/// </summary>
[OrandaService(typeof(IStatementLocator))]
public sealed class OpenMapperStatementLocator : IStatementLocator
{
    private readonly Func<IEnumerable<IMyBatisWorkspaceSession>> _sessions;

    public OpenMapperStatementLocator()
        : this(() => SqlEditorHost.Current.Workspaces.Select(w => w.MyBatis).OfType<IMyBatisWorkspaceSession>().ToList())
    {
    }

    /// <summary>시험용: 훑을 세션을 직접 넘긴다.</summary>
    public OpenMapperStatementLocator(Func<IEnumerable<IMyBatisWorkspaceSession>> sessions)
    {
        _sessions = sessions;
    }

    public Task<IReadOnlyList<MyBatisStatementLocation>> FindAsync(string query, bool exact, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var found = new List<MyBatisStatementLocation>();
        if (query.Length > 0)
        {
            foreach (var session in _sessions())
            {
                var ns = session.MapperNamespace;
                foreach (var element in session.Document.Elements)
                {
                    if (!element.IsStatement || element.Id is not { } id)
                    {
                        continue;
                    }

                    var full = ns is null ? id : ns + "." + id;
                    var hit = exact
                        ? string.Equals(full, query, StringComparison.Ordinal) || string.Equals(id, query, StringComparison.Ordinal)
                        : full.Contains(query, StringComparison.OrdinalIgnoreCase);
                    if (hit)
                    {
                        found.Add(new MyBatisStatementLocation(session.MapperPath, ns, id, "open"));
                    }
                }
            }
        }

        return Task.FromResult<IReadOnlyList<MyBatisStatementLocation>>(found);
    }
}
