using System.Threading;
using System.Threading.Tasks;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// 문서 탭 머리에서 바로 부를 수 있는 동작을 가진 문서 ViewModel(SQL 작업 공간).
/// 이런 문서는 탭 머리를 두 번 누르면 떼어 내지 않고 이름을 바꾸며, 오른쪽 클릭 메뉴도 이 동작으로 채운다.
/// </summary>
public interface IDockTabActions
{
    /// <summary>탭 머리의 「이름 바꾸기」(두 번 누르기·오른쪽 클릭 메뉴).</summary>
    Task RenameFromTabAsync(CancellationToken ct);

    /// <summary>탭 머리의 「닫기」(저장 확인 포함).</summary>
    Task CloseFromTabAsync(CancellationToken ct);
}
