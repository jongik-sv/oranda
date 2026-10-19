using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Shell;

namespace Oranda.App.Features.SchemaBrowser;

/// <summary>
/// TSK-07-02 스키마 브라우저 명령 처리기 (commands.md 7장 「데이터베이스」). 동작은 <see cref="SchemaBrowserViewModel"/> 에 있다.
/// 셸의 처리기 팩터리(DI)가 ViewModel 을 넣어 준다. 인자 없는 생성자(기본 <c>Activator</c>)는 아무것도 하지 않는다.
/// </summary>
[OrandaCommand("oranda.schema.refresh", CommandCategory.Database, When = "schemaFocus", Enablement = "hasSession",
    Placements = ["context:schema/refresh@10"])]
[OrandaCommand("oranda.schema.filter", CommandCategory.Database, When = "schemaFocus", Enablement = "hasSession")]
[OrandaCommand("oranda.schema.toggleFavorite", CommandCategory.Database, When = "schemaFocus", Enablement = "schemaNodeSelected",
    Placements = ["context:schema/favorite@20"])]
[OrandaCommand("oranda.schema.insertName", CommandCategory.Database, When = "schemaFocus", Enablement = "schemaNodeSelected && sqlDocOpen",
    Placements = ["context:schema/insert@30"])]
[OrandaCommand("oranda.schema.insertSelect", CommandCategory.Database, When = "schemaFocus", Enablement = "schemaNodeSelected && sqlDocOpen",
    Placements = ["context:schema/insert@31"])]
[OrandaCommand("oranda.schema.insertColumns", CommandCategory.Database, When = "schemaFocus", Enablement = "schemaNodeSelected && sqlDocOpen",
    Placements = ["context:schema/insert@32"])]
public sealed class SchemaBrowserCommandHandler : ICommandHandler
{
    private readonly SchemaBrowserViewModel? _vm;

    public SchemaBrowserCommandHandler()
    {
    }

    public SchemaBrowserCommandHandler(SchemaBrowserViewModel vm) => _vm = vm;

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (_vm is null)
        {
            return;
        }

        // 메뉴·단축키는 선택 노드, 시험·확장은 Parameter 로 노드를 넘길 수 있다.
        var node = context.Parameter as SchemaNode;
        switch (context.CommandId)
        {
            case "oranda.schema.refresh":
                await _vm.LoadAsync(cancellationToken);
                break;
            case "oranda.schema.filter":
                _vm.RequestFilterFocus();
                break;
            case "oranda.schema.toggleFavorite":
                await _vm.ToggleFavoriteAsync(node, cancellationToken);
                break;
            case "oranda.schema.insertName":
                await _vm.InsertAsync(node, SchemaInsertMode.Name, cancellationToken);
                break;
            case "oranda.schema.insertSelect":
                await _vm.InsertAsync(node, SchemaInsertMode.Select, cancellationToken);
                break;
            case "oranda.schema.insertColumns":
                await _vm.InsertAsync(node, SchemaInsertMode.Columns, cancellationToken);
                break;
        }
    }
}

/// <summary>자리 표시 클래스(ui-contract 소유 파일 규약). 실제 처리기는 <see cref="SchemaBrowserCommandHandler"/>.</summary>
public static class SchemaBrowserCommands
{
}
