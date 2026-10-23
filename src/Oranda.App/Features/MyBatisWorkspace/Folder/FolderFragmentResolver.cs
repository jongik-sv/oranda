using Oranda.Core.Composition;
using Oranda.Core.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Folder;

/// <summary>
/// 매퍼 폴더 안 다른 파일의 sql 조각(MB-10, TRD 4 <see cref="IMyBatisFragmentResolver"/> 의 폴더 구현). 세션은 같은 매퍼를 먼저 보고 없을 때 이것을 묻는다.
/// 계산기가 UI 스레드에서 동기로 부르므로 디스크를 읽지 않고 열린 폴더의 색인만 본다. 폴더가 없으면 늘 null.
/// </summary>
[OrandaService(typeof(IMyBatisFragmentResolver))]
public sealed class FolderFragmentResolver(MapperFolderService folder) : IMyBatisFragmentResolver
{
    public MyBatisFragment? Resolve(string refid, string? currentNamespace)
    {
        ArgumentNullException.ThrowIfNull(refid);
        return folder.Index?.ResolveFragment(refid, currentNamespace);
    }
}
