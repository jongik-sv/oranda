using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Shell;

namespace Oranda.App.Features.DataTransfer;

/// <summary>
/// 결과 그리드 「Excel 로 내보내기」(<c>oranda.grid.exportExcel</c>). 결과 메뉴·그리드 오른쪽 클릭 메뉴·명령 팔레트에 놓인다.
/// 하나로 합친 내보내기 대화상자를 Excel 이 골라진 채로 여는 빠른 길이다(D-020): 저장 위치가 채워져 있어 Enter 한 번으로 저장된다.
/// 셸의 처리기 팩터리(DI)가 <see cref="IServiceProvider"/> 를 넣어 준다. 인자 없는 생성자는 아무것도 하지 않는다.
/// <para>
/// 배치 순서: 결과 메뉴는 오른쪽 클릭 배치를 거울처럼 옮긴 묶음(100 단위, 0부터)이라 이 명령만 맨 위 묶음이 되려면 음수 순서(-100)가 필요하다.
/// 오른쪽 클릭 메뉴는 「파일로 내보내기…」와 같은 묶음·같은 순서 값이고, 같은 값이면 제목순(「Excel…」이 「파일로…」 앞)으로 놓여 내보내기 묶음이 맨 아래에 남는다.
/// </para>
/// </summary>
[OrandaCommand("oranda.grid.exportExcel", CommandCategory.Result, Title = "Excel 로 내보내기…", When = "gridFocus", Enablement = "gridHasResult",
    Placements = ["menu:result/exportExcel@-100", "context:grid/export@10"])]
public sealed class ExcelExportCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public ExcelExportCommandHandler()
    {
    }

    public ExcelExportCommandHandler(IServiceProvider services) => _services = services;

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is not null)
        {
            await DataTransferCommands.ShowResultExportAsync(_services, excel: true, cancellationToken).ConfigureAwait(true);
        }
    }
}
