using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Capture;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Testing;
using Oranda.Testing.Rollback;

namespace Oranda.Core.Tests.Rollback.Capture;

// 소유: rb-m2-oracle(RB-90 뒤 엔진 개선, 소유 예외 허가). 준비 단계의 사전 읽기 캐시와 한 행 키 문장의 COUNT(*) 생략.
public class RollbackTableCacheTests
{
    private static readonly DbObjectRef Products = new("SAMPLE", "PRODUCTS", "TABLE");
    private static readonly DbObjectRef Lines = new("SAMPLE", "ORDER_LINES", "TABLE");
    private static readonly Dictionary<string, BindValue> NoBinds = [];

    private static DmlShape GridUpdate => new(DmlVerb.Update)
    {
        Owner = "SAMPLE", Name = "PRODUCTS", TableText = "SAMPLE.PRODUCTS",
        WhereText = "\"PRODUCT_ID\" = :k1 AND \"UNIT_PRICE\" = :o1", SetColumns = ["UNIT_PRICE"],
    };

    private sealed class Rig
    {
        public Rig(bool withBus = true)
        {
            Inner.Tables[Products] = new TableInfo(Products,
            [
                new ColumnInfo("PRODUCT_ID", 1, "NUMBER"),
                new ColumnInfo("CODE", 2, "VARCHAR2(10)"),
                new ColumnInfo("UNIT_PRICE", 3, "NUMBER(10,2)"),
            ])
            {
                Constraints = [new ConstraintInfo("PRODUCTS_PK", ConstraintKind.PrimaryKey, ["PRODUCT_ID"])],
            };
            Inner.Tables[Lines] = new TableInfo(Lines,
            [
                new ColumnInfo("ORDER_ID", 1, "NUMBER"),
                new ColumnInfo("LINE_NO", 2, "NUMBER"),
                new ColumnInfo("QTY", 3, "NUMBER"),
            ])
            {
                Constraints = [new ConstraintInfo("ORDER_LINES_PK", ConstraintKind.PrimaryKey, ["ORDER_ID", "LINE_NO"])],
            };
            Metadata = new CountingMetadata(Inner);
            Engine = new RollbackCaptureEngine(new RecordingRollbackLedger(), () => new RollbackSettings(), Metadata, new InMemoryRollbackStore(),
                Time, withBus ? Bus : null);
            Channel.When("SELECT COUNT(*)", [12m]);
        }

        public FakeMetadataProvider Inner { get; } = new();

        public CountingMetadata Metadata { get; }

        public FakeEventBus Bus { get; } = new();

        public ManualTimeProvider Time { get; } = new();

        public FakeRollbackChannel Channel { get; } = new();

        public FakeRollbackDialect Dialect { get; } = new();

        public FakeSession Session { get; } = new(TestData.Profile(ConnectionEnvironment.Production));

        public RollbackCaptureEngine Engine { get; }

        public Task<ConfirmExtras> PrepareAsync(DmlShape shape) =>
            Engine.PrepareAsync(new RollbackRequest(Session, new SqlStatement("UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 1", default(TextRange), StatementKind.Dml),
                NoBinds, ExecutionOptions.Default) { Shape = shape }, Channel, Dialect, CancellationToken.None);

        public int SideEffectQueries => Channel.SentSql.Count(sql => sql.Contains("ALL_TRIGGERS", StringComparison.Ordinal));

        public int Counts => Channel.SentSql.Count(sql => sql.Contains("SELECT COUNT(*)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Second_edit_of_the_same_table_reads_no_dictionary_and_ddl_makes_it_read_the_new_columns()
    {
        var rig = new Rig();

        await rig.PrepareAsync(GridUpdate);
        var second = await rig.PrepareAsync(GridUpdate);

        Assert.True(second.Rollback!.Applicable);
        Assert.Equal((1, 1, 1), (rig.Metadata.NameSteps, rig.Metadata.TableReads, rig.SideEffectQueries));

        rig.Inner.Tables[Products] = rig.Inner.Tables[Products] with
        {
            Columns = [.. rig.Inner.Tables[Products].Columns, new ColumnInfo("MEMO", 4, "VARCHAR2(40)")],
        };
        rig.Bus.Publish(new MetadataInvalidated("OTHER/ACCOUNT/KEY", [])); // 다른 계정 접속의 DDL 도 버린다
        var third = await rig.PrepareAsync(GridUpdate);

        Assert.Equal((2, 2, 2), (rig.Metadata.NameSteps, rig.Metadata.TableReads, rig.SideEffectQueries));
        Assert.Contains(third.Rollback!.Table!.Columns, c => c.Name == "MEMO");
    }

    [Fact]
    public async Task Entries_expire_and_closed_sessions_are_forgotten()
    {
        var rig = new Rig();
        await rig.PrepareAsync(GridUpdate);

        rig.Time.Advance(RollbackCaptureEngine.TableTtl + TimeSpan.FromSeconds(1));
        await rig.PrepareAsync(GridUpdate);
        Assert.Equal(2, rig.Metadata.TableReads);
        Assert.Equal(1, rig.SideEffectQueries); // 안내는 더 오래 둔다

        rig.Bus.Publish(new SessionClosed(rig.Session.Id));
        await rig.PrepareAsync(GridUpdate);
        Assert.Equal((3, 2), (rig.Metadata.TableReads, rig.SideEffectQueries));
    }

    [Fact]
    public async Task Failed_notice_lookup_is_not_cached()
    {
        var rig = new Rig();
        rig.Channel.Fail("ALL_TRIGGERS", new DbError("ORA-00942", "table or view does not exist") { Kind = DbErrorKind.ObjectNotFound });

        await rig.PrepareAsync(GridUpdate);
        await rig.PrepareAsync(GridUpdate);

        Assert.Equal((2, 2), (rig.Metadata.TableReads, rig.SideEffectQueries)); // 실패를 캐시하면 경고가 사라진다
    }

    [Fact]
    public async Task Names_without_owner_are_cached_when_they_name_the_table_directly_but_not_through_a_synonym()
    {
        var rig = new Rig();
        var user = rig.Session.Server.SessionUser!;
        rig.Inner.Tables[new DbObjectRef(user, "PRODUCTS", "TABLE")] = rig.Inner.Tables[Products] with { Table = new DbObjectRef(user, "PRODUCTS", "TABLE") };
        var noOwner = GridUpdate with { Owner = null, TableText = "PRODUCTS" };

        await rig.PrepareAsync(noOwner);
        await rig.PrepareAsync(noOwner);
        Assert.Equal(1, rig.Metadata.TableReads); // 세션 사용자의 표로 바로 풀렸다

        var viaSynonym = GridUpdate with { Owner = null, Name = "PROD", TableText = "PROD" };
        rig.Inner.NameSteps[(user, "PROD")] = NameStep.Synonym("SAMPLE", "PRODUCTS");
        await rig.PrepareAsync(viaSynonym);
        await rig.PrepareAsync(viaSynonym);
        Assert.Equal(3, rig.Metadata.TableReads); // 시노님은 다른 곳에서 다시 가리킬 수 있다
    }

    [Fact]
    public async Task Without_a_bus_table_info_is_not_cached_and_only_the_notice_is()
    {
        var rig = new Rig(withBus: false);

        await rig.PrepareAsync(GridUpdate);
        await rig.PrepareAsync(GridUpdate);

        Assert.Equal((2, 2, 1), (rig.Metadata.NameSteps, rig.Metadata.TableReads, rig.SideEffectQueries));
    }

    [Theory]
    [InlineData("\"PRODUCT_ID\" = :k1 AND \"UNIT_PRICE\" = :o1", true)]
    [InlineData("t.product_id = 1040", true)]
    [InlineData("SAMPLE.PRODUCTS.PRODUCT_ID = :k", true)]
    [InlineData("ROWID = :rid", true)]
    [InlineData("ROWID = CHARTOROWID('AAAR3sAAEAAAACXAAA')", true)]
    [InlineData("PRODUCT_ID = :k1 OR UNIT_PRICE = 1", false)]
    [InlineData("PRODUCT_ID = :k1 OR(UNIT_PRICE = 1)", false)]
    [InlineData("PRODUCT_ID > :k1", false)]
    [InlineData("PRODUCT_ID = OTHER_ID", false)]
    [InlineData("CATEGORY_ID = :c", false)]
    [InlineData("NOT T.PRODUCT_ID = 5", false)]
    [InlineData("T.UNIT_PRICE + T.PRODUCT_ID = 5", false)]
    [InlineData("UPPER(PRODUCT_ID) = 1", false)]
    [InlineData("PRODUCT_ID IN (1, 2)", false)]
    [InlineData("PRODUCT_ID = NULL", false)]
    [InlineData("PRODUCT_ID IS NULL", false)]
    [InlineData("\"product_id\" = 1", false)]
    [InlineData("PRODUCT_ID = 'A'OR CODE = 'C'", false)]
    [InlineData("PRODUCT_ID = 1 /* it's */ OR CODE = 'x'", false)]
    [InlineData("PRODUCT_ID = q'[it's]' OR CODE = 'x'", false)]
    [InlineData("CODE = 1", false)]
    public async Task Count_is_skipped_only_when_the_key_surely_picks_one_row(string where, bool oneRow)
    {
        var rig = new Rig();

        var extras = await rig.PrepareAsync(GridUpdate with { WhereText = where });

        Assert.Equal(oneRow ? 0 : 1, rig.Counts);
        Assert.Equal(oneRow ? 1 : 12, extras.Rollback!.EstimatedRows);
    }

    [Theory]
    [InlineData("ORDER_ID = :k1 AND LINE_NO = :k2", true)]
    [InlineData("ORDER_ID = :k1", false)] // 복합 PK 를 일부만 덮음
    [InlineData("ORDER_ID = :k1 AND QTY = :o1", false)]
    public async Task Composite_keys_must_be_fully_covered(string where, bool oneRow)
    {
        var rig = new Rig();
        var shape = new DmlShape(DmlVerb.Update)
        {
            Owner = "SAMPLE", Name = "ORDER_LINES", TableText = "SAMPLE.ORDER_LINES", WhereText = where, SetColumns = ["QTY"],
        };

        await rig.PrepareAsync(shape);

        Assert.Equal(oneRow ? 0 : 1, rig.Counts);
    }

    [Fact]
    public async Task Disabled_primary_keys_do_not_count_as_one_row()
    {
        var rig = new Rig();
        rig.Inner.Tables[Products] = rig.Inner.Tables[Products] with
        {
            Constraints = [new ConstraintInfo("PRODUCTS_PK", ConstraintKind.PrimaryKey, ["PRODUCT_ID"]) { IsEnabled = false }],
        };

        await rig.PrepareAsync(GridUpdate);

        Assert.Equal(1, rig.Counts);
    }

    /// <summary>이름 풀이·표 정보 읽기 횟수를 세는 감싸개.</summary>
    internal sealed class CountingMetadata(IMetadataProvider inner) : IMetadataProvider
    {
        public int TableReads { get; private set; }

        public int NameSteps { get; private set; }

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct)
        {
            TableReads++;
            return inner.GetTableAsync(s, table, ct);
        }

        public Task<NameStep?> GetNameStepAsync(ISession s, string owner, string name, CancellationToken ct)
        {
            NameSteps++;
            return inner.GetNameStepAsync(s, owner, name, ct);
        }

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => inner.ListSchemasAsync(s, ct);

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => inner.ListObjectsAsync(s, q, ct);

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetProceduresAsync(s, obj, ct);

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => inner.GetDdlAsync(s, obj, o, ct);

        public Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetObjectDetailAsync(s, obj, ct);

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetSourceAsync(s, obj, ct);

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetCompileErrorsAsync(s, obj, ct);
    }
}
