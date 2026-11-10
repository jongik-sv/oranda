using System.Runtime.CompilerServices;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 사전 점검의 같은 시점 읽기 + 동시 복사(PF2b): 시작 SCN 으로 읽는다는 항목(<see cref="CopyCheckCodes.ReadPoint"/>, UNDO 경고), 다른 계정 원본 테이블의
/// FLASHBACK 권한(<see cref="CopyCheckCodes.FlashbackNoPrivilege"/> — 객체 권한·FLASHBACK ANY TABLE·소유자면 통과), 원본 SELECT 미리 보기의 AS OF 절.
/// 같은 시점 읽기를 끄거나 한 테이블씩이면 하나도 없다.
/// </summary>
public sealed class CopyPrecheckReadPointTests
{
    [Fact]
    public async Task Concurrent_consistent_read_adds_the_read_point_item_and_as_of_previews()
    {
        var f = new Fixture(consistent: true, concurrent: 2);

        var report = await f.RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ReadPoint);
        Assert.Equal(CheckLevel.Pass, item.Level);
        Assert.Contains("원본 SCN 하나를 잡아 모든 테이블", item.Message, StringComparison.Ordinal);
        Assert.Contains("AS OF SCN :ORANDA_SCN", item.Message, StringComparison.Ordinal);
        Assert.Contains("UNDO_RETENTION", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("표", item.Message, StringComparison.Ordinal);
        var concurrent = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ConcurrentCopy);
        Assert.Equal(CheckLevel.Confirm, concurrent.Level);
        Assert.Contains(CopyConcurrency.ReadPointNote, concurrent.Message, StringComparison.Ordinal);

        var selects = report.Preview.Where(p => p.Side == PreviewSide.Source).Select(p => p.Sql).ToList();
        Assert.Equal(2, selects.Count);
        Assert.All(selects, s => Assert.Contains(" AS OF SCN :ORANDA_SCN", s, StringComparison.Ordinal));
        Assert.Contains("SELECT \"ID\", \"NAME\" FROM \"APP\".\"CODES\" AS OF SCN :ORANDA_SCN", selects);

        // 실행기가 그대로 동시에(시점 읽기로) 돈다.
        var run = CopyConcurrency.ForRun(f.Job, report, Dbms.Oracle);
        Assert.Equal(2, run.Tables);
        Assert.True(run.ReadsAtPoint);
    }

    [Fact]
    public async Task Another_accounts_table_without_flashback_is_an_error_and_own_tables_are_fine()
    {
        var f = new Fixture(consistent: true, concurrent: 2);

        var report = await f.RunAsync();

        var error = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.FlashbackNoPrivilege);
        Assert.Equal(CheckLevel.Error, error.Level);
        Assert.Equal("APP.CODES", error.Table);
        Assert.Equal(
            "APP.CODES: 같은 시점 읽기로 동시에 복사하면 원본 APP.CODES 을(를) 시작 SCN 시점으로 읽는데, 이 계정에 그 테이블의 FLASHBACK 권한(FLASHBACK ANY TABLE 포함)이 없습니다."
            + " 권한을 받거나, 동시 테이블 수를 1로 하거나, 같은 시점 읽기를 끄세요.",
            error.Message);
        Assert.True(report.HasErrors);
    }

    [Fact]
    public async Task Unverified_privilege_names_make_a_missing_flashback_grant_a_confirmation_not_an_error()
    {
        // PF2b2 Tibero: 다른 계정 테이블에 FLASHBACK 권한이 필요한지 실측하지 못했다 — 막지 않고 확인 필요로 둔다(읽지 못하면 그 테이블만 실패).
        var f = new Fixture(consistent: true, concurrent: 2, Dbms.Tibero, PointDialect.Tsn);

        var report = await f.RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.FlashbackNoPrivilege);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.StartsWith("APP.CODES: 같은 시점 읽기로 동시에 복사하면 원본 APP.CODES 을(를) 시작 TSN 시점으로 읽는데", item.Message, StringComparison.Ordinal);
        Assert.Contains("원본 Tibero 에서 이 권한이 필요한지는 확인되지 않았습니다.", item.Message, StringComparison.Ordinal);
        Assert.Contains("지금 데이터로 바꿔 읽지 않습니다", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("표", item.Message, StringComparison.Ordinal);
        Assert.False(report.HasErrors);
        Assert.Contains("원본 TSN 하나를 잡아", Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ReadPoint).Message, StringComparison.Ordinal);
        Assert.True(CopyConcurrency.ForRun(f.Job, report, Dbms.Tibero).ReadsAtPoint);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Flashback_object_grant_or_flashback_any_table_passes(bool objectGrant, bool anyTable)
    {
        var f = new Fixture(consistent: true, concurrent: 2) { FlashbackGrant = objectGrant, FlashbackAnyTable = anyTable };

        var report = await f.RunAsync();

        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.FlashbackNoPrivilege);
        Assert.Contains(report.Items, i => i.Code == CopyCheckCodes.ReadPoint);
    }

    [Theory]
    [InlineData(false, 2)] // 같은 시점 읽기 끔: 예전 PF2(테이블마다 따로 읽기)
    [InlineData(true, null)] // 한 테이블씩: 읽기 전용 트랜잭션 하나가 시점을 맞춘다
    public async Task No_read_point_without_consistent_read_and_concurrency(bool consistent, int? concurrent)
    {
        var f = new Fixture(consistent, concurrent);

        var report = await f.RunAsync();

        Assert.DoesNotContain(report.Items, i => i.Code is CopyCheckCodes.ReadPoint or CopyCheckCodes.FlashbackNoPrivilege);
        Assert.All(report.Preview, p => Assert.DoesNotContain("AS OF", p.Sql, StringComparison.Ordinal));
    }

    // ---- 가짜 협력자 ----

    private sealed class Fixture
    {
        private readonly FakePrecheckBulk _bulk;

        public Fixture(bool consistent, int? concurrent, Dbms dbms = Dbms.Oracle, CopyReadPointSupport? point = null)
        {
            Job = new CopyJob
            {
                Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
                SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==", ConsistentRead = consistent,
                Tables =
                [
                    new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert),
                    new TableRule("CODES", RuleKind.All, TargetAction.DeleteAllInsert) { SourceOwner = "APP", TargetOwner = "DST" },
                ],
                Options = new CopyOptions { ConcurrentTables = concurrent },
            };
            Source = new FakeSession(TestData.Profile(readOnly: true), TestData.Server()) { DbKey = "SRCDB/PDB1/SRC" };
            Target = new FakeSession(TestData.Profile() with { Name = "dev" }, TestData.Server()) { DbKey = "DSTDB/PDB1/DST" };
            var metadata = new FakeMetadataProvider();
            metadata.Tables[new DbObjectRef("SRC", "ORDERS", "TABLE")] = Table("SRC", "ORDERS");
            metadata.Tables[new DbObjectRef("DST", "ORDERS", "TABLE")] = Table("DST", "ORDERS");
            metadata.Tables[new DbObjectRef("APP", "CODES", "TABLE")] = Table("APP", "CODES");
            metadata.Tables[new DbObjectRef("DST", "CODES", "TABLE")] = Table("DST", "CODES");
            var parts = new CopyDbmsParts(dbms, new IdCatalog(), new FakeClassifier(), new PointDialect { ReadPoint = point ?? PointDialect.Scn }, new FakePrecheckDialect(), new FakeCodec(),
                new FakeRecreateStep());
            _bulk = new FakePrecheckBulk(Source, Target);
            Service = new CopyPrecheckService(_bulk, metadata, new AllowPolicy(), parts, parts);
        }

        public CopyJob Job { get; }

        public FakeSession Source { get; }

        public FakeSession Target { get; }

        /// <summary>원본 계정이 APP.CODES 에 FLASHBACK 객체 권한을 받았다.</summary>
        public bool FlashbackGrant
        {
            get => _bulk.FlashbackGrant;
            init => _bulk.FlashbackGrant = value;
        }

        /// <summary>원본 계정이 FLASHBACK ANY TABLE 시스템 권한을 가졌다.</summary>
        public bool FlashbackAnyTable
        {
            get => _bulk.FlashbackAnyTable;
            init => _bulk.FlashbackAnyTable = value;
        }

        private CopyPrecheckService Service { get; }

        public Task<PrecheckReport> RunAsync() =>
            Service.RunAsync(new CopySessions(Source, Target), Job, new ResolvedPeriod(null, null), null, CancellationToken.None);

        private static TableInfo Table(string owner, string name) =>
            new(new DbObjectRef(owner, name, "TABLE"),
            [
                new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false },
                new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 },
            ])
            {
                Constraints = [new ConstraintInfo("PK_" + name, ConstraintKind.PrimaryKey, ["ID"])],
            };
    }

    /// <summary>카탈로그 글 대신 id 를 그대로 글로 돌려준다(가짜 대량 읽기가 id 로 답한다).</summary>
    private sealed class IdCatalog : Oranda.Core.Catalog.ISqlCatalog
    {
        public Oranda.Core.Catalog.CatalogQuery Get(string id, ServerInfo server) =>
            new(new Oranda.Core.Catalog.CatalogHeader(id, 11, [], Oranda.Core.Catalog.CatalogLicense.None, Oranda.Core.Catalog.CatalogRac.V, id + ".sql"), id);
    }

    /// <summary>점검 조회(id)에 답한다. 원본 계정 = SRC(APP.CODES 는 SELECT 만, 고르면 FLASHBACK 도), 대상 계정 = DST. 원본 <c>SELECT COUNT(*)</c> 는 2.</summary>
    private sealed class FakePrecheckBulk(ISession source, ISession target) : IBulkExecutionService
    {
        public bool FlashbackGrant { get; set; }

        public bool FlashbackAnyTable { get; set; }

        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var isTarget = ReferenceEquals(s, target);
            object?[][] rows = query.Text switch
            {
                "copy.session_user" => [[isTarget ? "DST" : "SRC"]],
                "copy.session_privs" when !isTarget && FlashbackAnyTable => [["FLASHBACK ANY TABLE"]],
                "copy.target_is_table" => [[1L]],
                "copy.table_privs" when !isTarget && Equals(binds["OWN"].Value, "APP") =>
                    FlashbackGrant ? [["SRC", "SELECT"], ["SRC", "FLASHBACK"]] : [["SRC", "SELECT"]],
                _ when query.Text.StartsWith("SELECT COUNT(*)", StringComparison.Ordinal) && ReferenceEquals(s, source) => [[2L]],
                _ => [],
            };
            foreach (var row in rows)
            {
                yield return row;
            }
        }

        public Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
                                                  ExecutionOptions options, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class AllowPolicy : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) => ValueTask.FromResult(PolicyDecision.Allowed);
    }

    private sealed class FakeClassifier : IDbErrorClassifier
    {
        public Dbms Dbms => Dbms.Oracle;

        public DbErrorKind Classify(int number) => DbErrorKind.Other;

        public DbError? FromException(Exception exception) => null;
    }

    private sealed class FakeCodec : IBulkValueCodec
    {
        public bool IsSupported(string serverType) => true;

        public BulkKind KindOf(string serverType) => serverType.StartsWith("VARCHAR", StringComparison.Ordinal) ? BulkKind.Text : BulkKind.Other;

        public BindDataType BindTypeOf(string serverType) => BindDataType.Varchar2;

        public string? ToKeyText(object? value, string serverType) => value?.ToString();

        public object? FromKeyText(string? text, string serverType) => text;

        public long? ToInt64(object? value) => value is long l ? l : null;

        public string? ToText(object? value) => value?.ToString();

        public string? NumberText(object? value) => value is long l ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        public bool IsZero(object? value) => value is 0L;

        public long ToInt64Exact(object? value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FakeRecreateStep : ICopyRecreateStep
    {
        public Task<PreservedDdl> CaptureAsync(ISession target, string owner, string table, CancellationToken ct) => Task.FromResult(PreservedDdl.Empty);

        public Task<IReadOnlyList<(string Parent, string Ddl, string? Name)>> SourceForeignKeysAsync(ISession source, string sourceOwner, string table,
            IReadOnlySet<string> parents, string targetOwner, CancellationToken ct) => Task.FromResult<IReadOnlyList<(string Parent, string Ddl, string? Name)>>([]);

        public Task<HashSet<string>> RecycleBinAsync(ISession target, string table, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakePrecheckDialect : ICopyPrecheckDialect
    {
        public bool SupportsReadPrivilege => false;

        public string? RecreateBlockedReason(ISession target) => null;

        public Task<IReadOnlyList<string>> ExtendedStringBlockersAsync(TableInfo source, Func<string, CancellationToken, Task<string?>> targetParameter,
                                                                       CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

        public IReadOnlyList<(string CatalogId, string Label)> NotRestoredProbes => [];

        public string RecycleBinNote(string? tablespace) => string.Empty;

        public string? ReadConsistencyParameter => null;

        public (CheckLevel Level, string Message) ReadConsistencyHint(long estimatedSeconds, long? retention) => (CheckLevel.Pass, string.Empty);

        public string ChildRecordFoundError => "ORA-02292";
    }

    /// <summary>문장 글만 만드는 방언(값 자리 = <c>:이름</c>) + Oracle 과 같은 꼴의 시점 지정 읽기.</summary>
    private sealed class PointDialect : ICopyDialect
    {
        public static readonly CopyReadPointSupport Scn = new("SCN", "AS OF SCN :{0}", [new CopyReadPointQuery("SELECT X FROM DUAL", "x")], "FLASHBACK", "FLASHBACK ANY TABLE")
        {
            SnapshotTooOldErrors = "ORA-01555·ORA-08180",
        };

        /// <summary>Tibero 모양(PF2b2): 이름 TSN, 권한 이름 미실측.</summary>
        public static readonly CopyReadPointSupport Tsn = new("TSN", "AS OF SCN :{0}", [new CopyReadPointQuery("SELECT X FROM DUAL", "x")], "FLASHBACK", "FLASHBACK ANY TABLE")
        {
            PrivilegeUnverified = true,
            UnmappedErrorsFailTable = true,
        };

        public CopyReadPointSupport ReadPoint { get; init; } = Scn;

        private static SqlStatement Ddl(string text) => new(text, default, StatementKind.Ddl);

        public string BindPlaceholder(string bindName, string serverType) => ":" + bindName;

        public SqlStatement DisableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER \"{owner}\".\"{trigger}\" DISABLE");

        public SqlStatement DisableConstraint(DisabledObject fk) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" DISABLE CONSTRAINT \"{fk.Name}\"");

        public SqlStatement DropTable(string owner, string table) => Ddl($"DROP TABLE \"{owner}\".\"{table}\"");

        public SqlStatement NextValues(string owner, string sequence) => throw new NotSupportedException();

        public SqlStatement SetTransactionReadOnly() => new("SET TRANSACTION READ ONLY", default, StatementKind.TransactionControl);

        public SqlStatement Savepoint(string name) => new("SAVEPOINT " + name, default, StatementKind.TransactionControl);

        public SqlStatement RollbackToSavepoint(string name) => new("ROLLBACK TO SAVEPOINT " + name, default, StatementKind.TransactionControl);

        public SqlStatement EnableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER \"{owner}\".\"{trigger}\" ENABLE");

        public SqlStatement DropConstraint(DisabledObject fk) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" DROP CONSTRAINT \"{fk.Name}\"");

        public SqlStatement EnableConstraint(DisabledObject fk, bool validate) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" ENABLE CONSTRAINT \"{fk.Name}\"");

        public SqlStatement RecycleBinOn() => throw new NotSupportedException();

        public string RestartIdentity(string owner, string table, string column) => throw new NotSupportedException();

        public string AddForeignKeyWithoutValidation(string addForeignKeyDdl) => addForeignKeyDdl;

        public bool IsParentKeysNotFound(DbError? dbError) => false;

        public bool IsReferencedByForeignKeys(DbError? dbError) => false;
    }
}
