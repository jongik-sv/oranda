using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 복사 원본 읽기의 LOB 미리 받기(PF3, 서버 없음·가짜 대량 실행): 테이블 원본 읽기가 <see cref="ExecutionOptions.LobPrefetchSize"/> 를 넘기고
/// (겹치기·번갈아 모두), 대상 COUNT 같은 다른 읽기는 넘기지 않으며, <see cref="CopyOptions.NoLobPrefetch"/> 면 넘기지 않는다.
/// 작업 파일: 끄는 설정은 기본값이면 파일에 쓰지 않고 스키마 버전을 올리지 않는다(결정 77).
/// </summary>
public sealed class CopyRunnerLobPrefetchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Table_source_reads_ask_for_lob_prefetch_by_default(bool serial)
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2, SerialReadWrite = serial }, ("A", 5), ("B", 1));

        var result = await f.RunAsync();

        Assert.All(result.Tables, t => Assert.Equal(CopyOutcome.Succeeded, t.Outcome));
        var source = f.Bulk.SourceReads.ToList();
        Assert.Equal(2, source.Count); // 테이블마다 원본 읽기 하나
        Assert.All(source, o =>
        {
            Assert.Equal(CopyRunner.LobPrefetchSize, o.LobPrefetchSize);
            Assert.Equal(HistorySource.DataCopy, o.Source); // 나머지 대량 옵션은 그대로
            Assert.True(o.SkipHistory);
        });
    }

    [Fact]
    public async Task Other_reads_such_as_the_target_count_do_not_ask_for_lob_prefetch()
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2 }, ("A", 3));

        await f.RunAsync();

        var target = f.Bulk.TargetReads.ToList();
        Assert.NotEmpty(target);
        Assert.All(target, o => Assert.Null(o.LobPrefetchSize));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_no_lob_prefetch_setting_reads_the_source_the_old_way(bool serial)
    {
        var f = new Fixture(new CopyOptions { BatchSize = 2, SerialReadWrite = serial, NoLobPrefetch = true }, ("A", 3));

        var result = await f.RunAsync();

        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
        var read = Assert.Single(f.Bulk.SourceReads);
        Assert.Null(read.LobPrefetchSize);
    }

    [Fact]
    public void The_no_lob_prefetch_setting_is_not_written_when_off_and_round_trips_when_on_without_a_schema_bump()
    {
        var off = Job(new CopyOptions());
        var offNode = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(off, CopyJobJsonContext.Default.CopyJob))!;
        Assert.False(offNode["options"]!.AsObject().ContainsKey("noLobPrefetch"));

        var on = Job(new CopyOptions { NoLobPrefetch = true });
        var json = JsonSerializer.Serialize(on, CopyJobJsonContext.Default.CopyJob);
        var onNode = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        Assert.True((bool)onNode["options"]!["noLobPrefetch"]!);
        Assert.Equal(5, (int)onNode["schemaVersion"]!);
        Assert.Equal(5, CopyJobSchema.Current);

        var back = JsonSerializer.Deserialize(json, CopyJobJsonContext.Default.CopyJob);
        Assert.NotNull(back);
        Assert.True(back!.Options.NoLobPrefetch);
    }

    private static CopyJob Job(CopyOptions options) => new()
    {
        Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
        SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==", Options = options,
    };

    // ---- 도우미(CopyRunnerPipelineTests 의 가짜와 같은 모양, 읽기 옵션만 모은다) ----

    private sealed class Fixture
    {
        public Fixture(CopyOptions options, params (string Table, int Rows)[] tables)
        {
            Tables = tables;
            var rules = tables.Select(t => new TableRule(t.Table, RuleKind.All, TargetAction.AppendOnly)).ToList();
            Job = CopyRunnerLobPrefetchTests.Job(options) with { Tables = rules };
            var plans = rules.Select(r => new CopyTablePlan(r, Info("SRC", r.Table), Info("DST", r.Table), ["ID", "NAME"], ["ID"])).ToList();
            Report = new PrecheckReport([], plans, [], new Dictionary<string, long>());
            Source = new FakeSession(TestData.Profile(readOnly: true), TestData.Server()) { DbKey = "SRCDB/PDB1/SRC" };
            Target = new FakeSession(TestData.Profile() with { Name = "dev" }, TestData.Server()) { DbKey = "DSTDB/PDB1/DST" };
            Bulk = new RecordingBulk(this);
            var parts = new CopyDbmsParts(Dbms.Oracle, new NoCatalog(), new FakeClassifier(), new FakeDialect(), new FakePrecheckDialect(), new SimpleCodec(),
                new FakeRecreateStep());
            Runner = new CopyRunner(Bulk, new FakeExecutionService(), new FakeRunLog(), parts, parts);
        }

        public IReadOnlyList<(string Table, int Rows)> Tables { get; }

        public CopyJob Job { get; }

        public PrecheckReport Report { get; }

        public FakeSession Source { get; }

        public FakeSession Target { get; }

        public RecordingBulk Bulk { get; }

        private CopyRunner Runner { get; }

        public Task<CopyRunResult> RunAsync() => Runner.RunAsync(new CopySessions(Source, Target), Job, Report,
            new CopyDecisions(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false),
            new ResolvedPeriod(null, null), null, new CopyRunControl(), CancellationToken.None);

        public string TableOf(string sql) =>
            Tables.Select(t => t.Table).FirstOrDefault(t => sql.Contains("\"" + t + "\"", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("테이블을 모르는 문장: " + sql);

        private static TableInfo Info(string owner, string table) =>
            new(new DbObjectRef(owner, table, "TABLE"),
            [
                new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false },
                new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 },
            ])
            {
                Constraints = [new ConstraintInfo("PK_" + table, ConstraintKind.PrimaryKey, ["ID"])],
            };
    }

    /// <summary>원본은 정한 수의 행을, 대상 읽기(COUNT)는 넣은 건수를 낸다. 읽기마다 받은 옵션을 원본·대상으로 나눠 남긴다.</summary>
    private sealed class RecordingBulk(Fixture fixture) : IBulkExecutionService
    {
        private readonly ConcurrentDictionary<string, long> _inserted = new(StringComparer.Ordinal);

        public ConcurrentQueue<ExecutionOptions> SourceReads { get; } = new();

        public ConcurrentQueue<ExecutionOptions> TargetReads { get; } = new();

        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            var table = fixture.TableOf(query.Text);
            if (!ReferenceEquals(s, fixture.Source))
            {
                TargetReads.Enqueue(options);
                yield return [_inserted.GetValueOrDefault(table)];
                yield break;
            }

            SourceReads.Enqueue(options);
            var rows = fixture.Tables.Single(t => t.Table == table).Rows;
            for (var i = 0; i < rows; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return [(long)(i + 1), "n" + i];
            }
        }

        public Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
                                                  ExecutionOptions options, CancellationToken ct)
        {
            if (columns.Count == 0)
            {
                return Task.FromResult(new BulkResult(ExecutionStatus.Succeeded, 0)); // 트랜잭션 제어
            }

            _inserted.AddOrUpdate(fixture.TableOf(stmt.Text), rows.Count, (_, n) => n + rows.Count);
            return Task.FromResult(new BulkResult(ExecutionStatus.Succeeded, rows.Count));
        }
    }

    private sealed class FakeRunLog : ICopyRunLog
    {
        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => Task.FromResult<ICopyRunRecord>(new Record());

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;

        private sealed class Record : ICopyRunRecord
        {
            public Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct) => Task.CompletedTask;

            public Task RecordStatementAsync(string sql, CancellationToken ct) => Task.CompletedTask;

            public Task RecordTableAsync(TableRunResult result, CancellationToken ct) => Task.CompletedTask;

            public Task RecordFailedRowAsync(FailedRow row, CancellationToken ct) => Task.CompletedTask;

            public Task CompleteAsync(CopyOutcome outcome, CancellationToken ct) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeClassifier : IDbErrorClassifier
    {
        public Dbms Dbms => Dbms.Oracle;

        public DbErrorKind Classify(int number) => DbErrorKind.Other;

        public DbError? FromException(Exception exception) => null;
    }

    private sealed class SimpleCodec : IBulkValueCodec
    {
        public bool IsSupported(string serverType) => true;

        public BulkKind KindOf(string serverType) => serverType.StartsWith("VARCHAR", StringComparison.Ordinal) ? BulkKind.Text : BulkKind.Other;

        public BindDataType BindTypeOf(string serverType) => BindDataType.Number;

        public string? ToKeyText(object? value, string serverType) => value is null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

        public object? FromKeyText(string? text, string serverType) => text;

        public long? ToInt64(object? value) => value is long l ? l : null;

        public string? ToText(object? value) => value?.ToString();

        public string? NumberText(object? value) => value is long l ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        public bool IsZero(object? value) => value is 0L;

        public long ToInt64Exact(object? value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

        public object? ToPortable(object? value, string serverType) => value;

        public object? FromPortable(object? portable, string bindType) => portable;
    }

    private sealed class NoCatalog : Oranda.Core.Catalog.ISqlCatalog
    {
        public Oranda.Core.Catalog.CatalogQuery Get(string id, ServerInfo server) => throw new NotSupportedException("이 시험은 카탈로그 조회를 쓰지 않는다: " + id);
    }

    private sealed class FakeRecreateStep : ICopyRecreateStep
    {
        public Task<PreservedDdl> CaptureAsync(ISession target, string owner, string table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<(string Parent, string Ddl, string? Name)>> SourceForeignKeysAsync(ISession source, string sourceOwner, string table,
            IReadOnlySet<string> parents, string targetOwner, CancellationToken ct) => throw new NotSupportedException();

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

    private sealed class FakeDialect : ICopyDialect
    {
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
