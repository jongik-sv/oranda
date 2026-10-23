using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Workspace;

// MyBatisWorkspaceSession 의 일부: sql 조각 해석기(MyBatisWorkspaceSession.cs 에서 나눔, 동작 불변).
public sealed partial class MyBatisWorkspaceSession
{
    /// <summary>같은 매퍼 → 등록된 해석기(03-07 폴더 등) 순으로 sql 조각을 찾는다.</summary>
    private sealed class SessionFragments(MyBatisWorkspaceSession session) : IMyBatisFragmentResolver
    {
        public MyBatisFragment? Resolve(string refid, string? currentNamespace)
        {
            ArgumentNullException.ThrowIfNull(refid);
            var ns = session.MapperNamespace;
            var local = refid;
            if (ns is not null && refid.Length > ns.Length + 1 && refid.StartsWith(ns, StringComparison.Ordinal) && refid[ns.Length] == '.')
            {
                local = refid[(ns.Length + 1)..];
            }

            bool sameMapper = !local.Contains('.', StringComparison.Ordinal) && (currentNamespace is null || string.Equals(currentNamespace, ns, StringComparison.Ordinal) || !ReferenceEquals(local, refid));
            if (sameMapper && session._entries.FirstOrDefault(e => e.Kind == MyBatisStatementKind.Sql && !e.IsDeleted && string.Equals(e.Id, local, StringComparison.Ordinal)) is { } frag)
            {
                return new MyBatisFragment(local, ns, frag.Text, session.MapperPath);
            }

            foreach (var other in session._workspace.Host.MyBatisHooks.FragmentResolvers)
            {
                if (other.Resolve(refid, currentNamespace) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
