using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.TableEdit;
using Oranda.App.Tests.Shell;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 「새 테이블…」·「이 구조로 새 테이블…」 명령(설계 7.5, T8): 등록·자리·활성 조건(소유자 노드·「테이블」 폴더 = <c>ownerNodeSelected</c>, 테이블 노드 = <c>tableObjectSelected</c>),
/// 노드에 따라 고른 소유자·원본으로 「✎ 새 테이블」 탭을 연다.
/// </summary>
public class NewTableCommandTests
{
    private static SchemaNode Schema(string owner) => new(null, SchemaNodeKind.Schema, owner) { Owner = owner };

    private static SchemaNode TablesFolder(string owner) => new(null, SchemaNodeKind.Category, "테이블 (3)") { Owner = owner, CategoryType = "TABLE" };

    private static SchemaNode Item(DbObjectRef r) => new(null, SchemaNodeKind.Item, r.Name) { Ref = r, Owner = r.Owner };

    [Fact]
    public void Commands_are_registered_in_the_schema_context_menu_with_their_enablement()
    {
        var reg = ShellTestHelpers.CreateCommandRegistry();
        var blank = reg.Find(NewTableCommandHandler.NewTableId)!;
        var like = reg.Find(NewTableCommandHandler.NewTableLikeId)!;

        Assert.Contains("context:schema/detail/newTable@43", blank.Placements);
        Assert.Contains("context:schema/detail/newTable@44", like.Placements);
        Assert.Contains("ownerNodeSelected", blank.Enablement, StringComparison.Ordinal);
        Assert.Contains("!readOnlySession", blank.Enablement, StringComparison.Ordinal);
        Assert.Contains("tableObjectSelected", like.Enablement, StringComparison.Ordinal);
        Assert.Contains("!readOnlySession", like.Enablement, StringComparison.Ordinal);
        Assert.Equal("schemaFocus", blank.When);
        Assert.Equal("schemaFocus", like.When);
    }

    [Fact]
    public void Owner_key_is_true_for_owner_nodes_and_the_tables_folder_only()
    {
        Assert.True(SchemaBrowserViewModel.IsOwnerNode(Schema("SAMPLE")));
        Assert.True(SchemaBrowserViewModel.IsOwnerNode(TablesFolder("SAMPLE")));
        Assert.False(SchemaBrowserViewModel.IsOwnerNode(new SchemaNode(null, SchemaNodeKind.Category, "뷰") { Owner = "SAMPLE", CategoryType = "VIEW" }));
        Assert.False(SchemaBrowserViewModel.IsOwnerNode(Item(Orders)));
        Assert.False(SchemaBrowserViewModel.IsOwnerNode(null));

        Assert.True(SchemaBrowserViewModel.IsTableObject(Item(Orders)));
        Assert.False(SchemaBrowserViewModel.IsTableObject(Item(new DbObjectRef("SAMPLE", "V_ORDERS", "VIEW"))));
        Assert.False(SchemaBrowserViewModel.IsTableObject(Schema("SAMPLE")));
        Assert.False(SchemaBrowserViewModel.IsTableObject(null));
    }

    private static (NewTableCommandHandler Handler, List<(ObjectDetailViewModel Vm, string Title)> Opened) CreateHandler(FakeMeta meta)
    {
        var opened = new List<(ObjectDetailViewModel, string)>();
        var handler = new NewTableCommandHandler
        {
            SessionProvider = () => new FakeSession(),
            Owners = () => ["HR", "SAMPLE"],
            CreateViewModel = () =>
            {
                var vm = new ObjectDetailViewModel(meta)
                {
                    TableEditEngine = CoreTableEditEngine.Instance,
                    TableEditDialect = new FakeDialect(),
                    InvalidateTableCache = (_, _, _) => Task.CompletedTask,
                };
                return vm;
            },
            OpenDocument = (vm, title) =>
            {
                opened.Add((vm, title));
                return true;
            },
        };
        return (handler, opened);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("folder")]
    public async Task New_table_from_an_owner_node_or_the_tables_folder_opens_a_blank_tab_for_that_owner(string kind)
    {
        var (handler, opened) = CreateHandler(new FakeMeta());
        object node = kind == "schema" ? Schema("HR") : TablesFolder("HR");

        await handler.ExecuteAsync(new CommandContext(NewTableCommandHandler.NewTableId, FakeContextKeys.AllTrue, node), default);

        var (vm, title) = Assert.Single(opened);
        Assert.Equal("✎ 새 테이블", title);
        Assert.True(vm.IsNewTableMode);
        Assert.Equal("HR", vm.NewTableOwner);
        Assert.Equal(["HR", "SAMPLE"], vm.NewTableOwners);
        Assert.Empty(vm.TableEdit!.Draft.Columns);
    }

    [Fact]
    public async Task New_table_like_a_table_node_copies_its_structure()
    {
        var meta = new FakeMeta();
        var (handler, opened) = CreateHandler(meta);

        await handler.ExecuteAsync(new CommandContext(NewTableCommandHandler.NewTableLikeId, FakeContextKeys.AllTrue, Item(Orders)), default);

        var (vm, _) = Assert.Single(opened);
        Assert.Equal("SAMPLE", vm.NewTableOwner);
        Assert.Equal("ORDERS_COPY", vm.NewTableName);
        Assert.Equal(4, vm.TableEdit!.Draft.Columns.Count);
        Assert.Contains("read ORDERS", meta.Calls);
    }

    [Fact]
    public async Task Commands_do_nothing_without_a_session_or_on_the_wrong_node()
    {
        var (handler, opened) = CreateHandler(new FakeMeta());

        await handler.ExecuteAsync(new CommandContext(NewTableCommandHandler.NewTableLikeId, FakeContextKeys.AllTrue, Item(new DbObjectRef("SAMPLE", "V", "VIEW"))), default);
        await handler.ExecuteAsync(new CommandContext(NewTableCommandHandler.NewTableId, FakeContextKeys.AllTrue, Item(Orders)), default);
        Assert.Empty(opened);

        handler.SessionProvider = () => null;
        await handler.ExecuteAsync(new CommandContext(NewTableCommandHandler.NewTableId, FakeContextKeys.AllTrue, Schema("SAMPLE")), default);
        Assert.Empty(opened);
    }
}
