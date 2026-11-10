using System.Runtime.CompilerServices;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;
using Oranda.Tibero.Transfer.Copy;

namespace Oranda.Tibero.Tests.Transfer.Copy;

/// <summary>
/// Tibero 대상 「테이블 다시 만들기」의 부모·자식 순서(TR1 위험 3)를 실행기 경로(<c>CopyRunner.RecreateTablesAsync</c> → <c>RestoreAsync</c>)로 본다(서버 없음, 가짜 실행).
/// 지운 자식 표가 휴지통에서 FK 를 쥔 채 남아 부모 DROP 을 막지 않게, 함께 다시 만드는 자식의 FK 를 첫 DROP 전에 떼고 적재 뒤 원본 정의로 부모 다음에 붙인다.
/// 서버로 도는 확인은 <c>TiberoRecreateStepIt</c>(같은 계정)·<c>CrossDbmsRecreateIntoTiberoIt</c>(Oracle→Tibero 실행기 전체).
/// </summary>
public sealed class TiberoCopyRunnerRecreateTests
{
    private const string Owner = "SCOTT";
    private const string SourceOwner = "SRC";
    private const string Parent = "P";
    private const string Child = "C";
    private const string ChildFk = "C_P_FK";

    private const string DetachChildFk = "ALTER TABLE \"SCOTT\".\"C\" DROP CONSTRAINT \"C_P_FK\"";
    private const string DropChild = "DROP TABLE \"SCOTT\".\"C\"";
    private const string DropParent = "DROP TABLE \"SCOTT\".\"P\"";
    private const string ReferencingFk = "ALTER TABLE \"SCOTT\".\"C\" ADD CONSTRAINT \"C_P_FK\" FOREIGN KEY (\"PID\") REFERENCES \"SCOTT\".\"P\" (\"ID\")";
    private const string JobFk = "ALTER TABLE \"SCOTT\".\"C\" ADD CONSTRAINT \"C_P_FK\" FOREIGN KEY (\"PID\") REFERENCES \"SCOTT\".\"P\" (\"ID\") ON DELETE CASCADE";

    [Fact]
    public void Tibero_dialect_detaches_child_foreign_keys_before_the_first_drop_and_oracle_default_does_not()
    {
        ICopyDialect tibero = TiberoCopyDialect.Instance;

        Assert.True(tibero.DetachChildForeignKeysBeforeDrop);
        Assert.False(((ICopyDialect)new DefaultDialectProbe()).DetachChildForeignKeysBeforeDrop);
    }

    [Fact]
    public async Task Child_foreign_key_is_detached_before_any_drop_and_added_back_once_from_the_source_after_both_tables_exist()
    {
        var f = new Fixture();

        var (tables, notices) = await f.RunAsync();

        var texts = f.Texts();
        Assert.True(texts.IndexOf(DetachChildFk) >= 0, "the child foreign key was not detached");
        Assert.True(texts.IndexOf(DetachChildFk) < texts.IndexOf(DropChild), "the child foreign key must go before the child table is dropped (not into the recycle bin)");
        Assert.True(texts.IndexOf(DropChild) < texts.IndexOf(DropParent), "children are dropped before parents");
        var createParent = texts.FindIndex(t => t.StartsWith("CREATE TABLE \"SCOTT\".\"P\"", StringComparison.Ordinal));
        var createChild = texts.FindIndex(t => t.StartsWith("CREATE TABLE \"SCOTT\".\"C\"", StringComparison.Ordinal));
        Assert.True(texts.IndexOf(DropParent) < createParent && createParent < createChild, "parents are created before children, after every drop");
        Assert.DoesNotContain("FOREIGN KEY", texts[createChild], StringComparison.Ordinal); // FK 는 CREATE 에 넣지 않는다

        // 자식 FK 는 원본 정의(작업 안 FK)로 부모·자식이 모두 생긴 뒤 한 번만 붙는다. 휴지통·받아 둔 참조 FK 로 다시 붙이지 않는다.
        Assert.Equal(1, texts.Count(t => t == JobFk));
        Assert.True(texts.IndexOf(JobFk) > createChild);
        Assert.DoesNotContain(ReferencingFk, texts);
        Assert.Equal(1, texts.Count(t => t == DetachChildFk));

        Assert.All(tables, t => Assert.True(t.Dropped && t.Created && !t.Flashbacked, t.Name));
        Assert.DoesNotContain(notices, n => n.Contains("떼어 내지 못해", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_child_foreign_key_that_cannot_be_detached_stops_before_any_table_is_dropped()
    {
        var f = new Fixture();
        f.Execution.Responder = call => call.Statement.Text == DetachChildFk
            ? new ExecutionResult(call.Statement, ExecutionStatus.Failed) { Error = new DbError("TBR-1", "denied") }
            : new ExecutionResult(call.Statement, ExecutionStatus.Succeeded);

        var (tables, notices) = await f.RunAsync();

        Assert.DoesNotContain(f.Texts(), t => t.StartsWith("DROP TABLE", StringComparison.Ordinal));
        Assert.All(tables, t => Assert.False(t.Dropped, t.Name));
        Assert.Contains(notices, n => n.Contains("참조 FK 를 떼어 내지 못해 복사를 멈췄습니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_child_whose_create_fails_after_the_drop_is_flashed_back_and_its_detached_foreign_key_is_listed_to_run()
    {
        var f = new Fixture();
        f.Execution.Responder = call => call.Statement.Text.StartsWith("CREATE TABLE \"SCOTT\".\"C\"", StringComparison.Ordinal)
            ? new ExecutionResult(call.Statement, ExecutionStatus.Failed) { Error = new DbError("TBR-2", "boom") }
            : new ExecutionResult(call.Statement, ExecutionStatus.Succeeded);

        var (tables, notices) = await f.RunAsync();

        var texts = f.Texts();
        Assert.Contains("FLASHBACK TABLE \"SCOTT\".\"BIN_C\" TO BEFORE DROP", texts); // 표가 없어지지 않게 그 자리에서 휴지통에서 되살린다
        var child = Assert.Single(tables, t => t.Name.EndsWith(Child, StringComparison.Ordinal));
        Assert.True(child.Dropped && !child.Created && child.Flashbacked);
        Assert.True(Assert.Single(tables, t => t.Name.EndsWith(Parent, StringComparison.Ordinal)).Created);
        Assert.DoesNotContain(JobFk, texts); // 만들지 못한 자식에는 붙이지 않는다
        Assert.Contains(notices, n => n.Contains("되살린 옛 테이블에 돌아오지 않는 FK", StringComparison.Ordinal) && n.Contains(ReferencingFk, StringComparison.Ordinal));
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Target = new FakeSession(TestData.Profile() with { Dbms = Dbms.Tibero, Name = "dev" },
                TestData.Server("6") with { Dbms = Dbms.Tibero, SessionUser = Owner });
            Source = new FakeSession(TestData.Profile(readOnly: true) with { Dbms = Dbms.Tibero },
                TestData.Server("6") with { Dbms = Dbms.Tibero, SessionUser = SourceOwner });
            Execution = new FakeExecutionService();
            var bulk = new NoBulk();
            var parts = new TiberoCopyParts(bulk).Parts with { Recreate = new FakeStep(this) };
            Runner = new CopyRunner(bulk, Execution, new NoRunLog(), parts, parts);
        }

        public FakeSession Target { get; }

        public FakeSession Source { get; }

        public FakeExecutionService Execution { get; }

        private CopyRunner Runner { get; }

        public List<string> Texts() => [.. Execution.Calls.Select(c => c.Statement.Text)];

        public Task<(IReadOnlyList<(string Name, bool Dropped, bool Created, bool Flashbacked)> Tables, IReadOnlyList<string> Notices)> RunAsync()
        {
            var parentRule = new TableRule(Parent, RuleKind.All, TargetAction.Recreate);
            var childRule = new TableRule(Child, RuleKind.All, TargetAction.Recreate);
            var job = new CopyJob
            {
                Id = "1", Name = "n", Source = new ConnectionRef("s", SourceOwner), Target = new ConnectionRef("t", Owner),
                SourceOwner = SourceOwner, TargetOwner = Owner, MaskSalt = "AA==", Tables = [parentRule, childRule],
            };
            var parent = new TableInfo(new DbObjectRef(SourceOwner, Parent, "TABLE"), [new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }])
            {
                Constraints = [new ConstraintInfo("P_PK", ConstraintKind.PrimaryKey, ["ID"])],
            };
            var child = new TableInfo(new DbObjectRef(SourceOwner, Child, "TABLE"),
                [new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("PID", 2, "NUMBER")])
            {
                Constraints =
                [
                    new ConstraintInfo("C_PK", ConstraintKind.PrimaryKey, ["ID"]),
                    new ConstraintInfo(ChildFk, ConstraintKind.ForeignKey, ["PID"]) { ReferencedTable = new DbObjectRef(SourceOwner, Parent, "TABLE") },
                ],
            };
            CopyTablePlan Plan(TableRule rule, TableInfo source) =>
                new(rule, source, source with { Table = source.Table with { Owner = Owner } }, [.. source.Columns.Select(c => c.Name)], ["ID"]);
            return Runner.RecreateAndRestoreForTestAsync(new CopySessions(Source, Target), job, [Plan(parentRule, parent), Plan(childRule, child)]);
        }

        /// <summary>
        /// 받아 두기: 부모는 자식의 FK 하나에 참조되고(받아 둔 참조 FK), 자식은 원본에서 그 부모를 참조하는 작업 안 FK 하나(ON DELETE CASCADE)를 가진다.
        /// 휴지통: 그 표의 DROP 을 보낸 뒤에만 <c>BIN_&lt;표&gt;</c> 하나가 생긴다.
        /// </summary>
        private sealed class FakeStep(Fixture fixture) : ICopyRecreateStep
        {
            public Task<PreservedDdl> CaptureAsync(ISession target, string owner, string table, CancellationToken ct) =>
                Task.FromResult(table == Parent
                    ? new PreservedDdl([], [], [ReferencingFk]) { ReferencingConstraints = [new DisabledObject(DisabledKind.ForeignKey, Owner, Child, ChildFk)] }
                    : PreservedDdl.Empty);

            public Task<IReadOnlyList<(string Parent, string Ddl, string? Name)>> SourceForeignKeysAsync(ISession source, string sourceOwner, string table,
                IReadOnlySet<string> parents, string targetOwner, CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<(string, string, string?)>>(table == Child && parents.Contains(Parent) ? [(Parent, JobFk, ChildFk)] : []);

            public Task<HashSet<string>> RecycleBinAsync(ISession target, string table, CancellationToken ct) =>
                Task.FromResult(fixture.Texts().Contains($"DROP TABLE \"SCOTT\".\"{table}\"")
                    ? new HashSet<string>(["BIN_" + table], StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal));
        }
    }

    /// <summary>이 시험 경로는 대량 실행을 쓰지 않는다(표가 있는지 다시 보기·부모 없는 행 세기는 실패 분기에서만).</summary>
    private sealed class NoBulk : IBulkExecutionService
    {
        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
                                                  ExecutionOptions options, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class NoRunLog : ICopyRunLog
    {
        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>기본 구현만 쓰는 방언(인터페이스 기본값 확인용). 문장 멤버는 부르지 않는다.</summary>
    private sealed class DefaultDialectProbe : ICopyDialect
    {
        public string BindPlaceholder(string bindName, string serverType) => throw new NotSupportedException();

        public SqlStatement DisableTrigger(string owner, string trigger) => throw new NotSupportedException();

        public SqlStatement DisableConstraint(DisabledObject fk) => throw new NotSupportedException();

        public SqlStatement DropTable(string owner, string table) => throw new NotSupportedException();

        public SqlStatement NextValues(string owner, string sequence) => throw new NotSupportedException();

        public SqlStatement SetTransactionReadOnly() => throw new NotSupportedException();

        public SqlStatement Savepoint(string name) => throw new NotSupportedException();

        public SqlStatement RollbackToSavepoint(string name) => throw new NotSupportedException();

        public SqlStatement EnableTrigger(string owner, string trigger) => throw new NotSupportedException();

        public SqlStatement DropConstraint(DisabledObject fk) => throw new NotSupportedException();

        public SqlStatement EnableConstraint(DisabledObject fk, bool validate) => throw new NotSupportedException();

        public SqlStatement RecycleBinOn() => throw new NotSupportedException();

        public string RestartIdentity(string owner, string table, string column) => throw new NotSupportedException();

        public string AddForeignKeyWithoutValidation(string addForeignKeyDdl) => throw new NotSupportedException();

        public bool IsParentKeysNotFound(DbError? dbError) => false;

        public bool IsReferencedByForeignKeys(DbError? dbError) => false;
    }
}
