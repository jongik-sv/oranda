using Oranda.App.Features.ObjectDetail;
using Oranda.App.Shell;
using Oranda.Core.Features;
using Oranda.Core.Shell;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 「테이블 편집 켜기·끄기」(테이블 편집기 설계 7.1): 활성 객체 상세 탭(테이블의 열·제약·인덱스·설명·파티션 페이지)의 ✎ 편집 토글과 같다.
/// 이 네임스페이스(<c>TableEdit</c>)가 기능 <see cref="FeatureIds.TableEditor"/> 의 꼬리표라, 기능을 끄면 명령과 ✎ 토글이 함께 사라진다.
/// </summary>
[OrandaCommand("oranda.tableEdit.toggle", CommandCategory.Database, Enablement = "!readOnlySession")]
public sealed class TableEditToggleCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public TableEditToggleCommandHandler()
    {
    }

    public TableEditToggleCommandHandler(IServiceProvider services) => _services = services;

    /// <summary>시험용: 대상 객체 상세(기본 = 도킹의 활성 문서).</summary>
    internal Func<ObjectDetailViewModel?>? Target { get; set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var detail = Target?.Invoke()
            ?? ((_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ActiveDocument?.Panel as ObjectDetailViewModel);
        if (detail is not { CanToggleTableEdit: true })
        {
            return;
        }

        await detail.SetTableEditingAsync(!detail.IsTableEditing, cancellationToken).ConfigureAwait(true);
    }
}

/// <summary>테이블 편집기의 바깥 연결 지점.</summary>
public static class TableEditCommands
{
    public const string ToggleId = "oranda.tableEdit.toggle";

    /// <summary>테이블 편집 기능이 켜졌나(게이트가 없으면 켬 = 지금 동작).</summary>
    public static bool IsEnabled(IServiceProvider? services) =>
        services?.GetService(typeof(IFeatureGate)) is not IFeatureGate gate || gate.IsEnabled(FeatureIds.TableEditor);
}
