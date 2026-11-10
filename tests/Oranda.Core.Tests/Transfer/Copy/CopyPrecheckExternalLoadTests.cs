using System.Runtime.CompilerServices;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Core.Transfer.Copy.ExternalLoad;
using Oranda.Testing;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 사전 점검의 적재 방식 판정(조정 결정 88·89, <c>CopyPrecheckService.External.cs</c>): 도구를 쓸 수 있으면 확인 항목·외부 도구 적재·제어 파일 미리 보기,
/// 못 쓰면(도구 확인 실패·준비물 없음·키로 맞추기·「모두 끝난 뒤 한 번」 커밋) 기본 방식으로 두고 통과 항목으로 이유를 알린다. 도구 확인은 고른 테이블이 있을 때만 한다.
/// </summary>
public sealed class CopyPrecheckExternalLoadTests
{
    private static readonly ExternalToolInfo Usable = new(ExternalLoaderKind.SqlLoader, @"C:\ic\sqlldr.exe", 19, "SQL*Loader: Release 19.0.0.0.0", null);

    [Fact]
    public async Task Usable_tool_makes_the_table_external_with_a_confirm_item_and_a_control_file_preview()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, LoadMethod.ExternalTool);

        var report = await f.RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ExternalLoad);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Equal("ORDERS", item.Table);
        Assert.Contains("SQL*Loader 19", item.Message, StringComparison.Ordinal);
        Assert.Contains("트리거가 없고 테이블 전체를 비운 뒤 넣어 direct path", item.Message, StringComparison.Ordinal); // 이미 있는 테이블(결정 96)
        Assert.Contains("DELETE", item.Message, StringComparison.Ordinal); // 정리를 적재 전에 확정한다는 알림
        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.ExternalLoadFallback);

        var plan = Assert.Single(report.Order);
        Assert.Equal(LoadMethod.ExternalTool, plan.Load);
        Assert.Same(Usable, report.ExternalTool);

        var preview = Assert.Single(report.Preview, p => p.Sql.StartsWith("OPTIONS (", StringComparison.Ordinal));
        Assert.Equal(PreviewSide.Target, preview.Side);
        Assert.Contains("INTO TABLE \"DST\".\"ORDERS\"", preview.Sql, StringComparison.Ordinal);
        Assert.Contains("DIRECT=TRUE", preview.Sql, StringComparison.Ordinal);
        Assert.Contains("REENABLE DISABLED_CONSTRAINTS", preview.Sql, StringComparison.Ordinal);

        var probe = Assert.Single(f.Probe.Checks);
        Assert.Equal(ExternalLoaderKind.SqlLoader, probe.Kind);
        Assert.Equal(@"D:\tools\sqlldr.exe", probe.Path); // 설정 경로를 넘긴다
    }

    [Fact]
    public async Task Existing_table_with_a_target_trigger_stays_on_the_conventional_path()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, LoadMethod.ExternalTool) { Triggers = true };

        var report = await f.RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ExternalLoad);
        Assert.Contains("트리거가 있어", item.Message, StringComparison.Ordinal);
        Assert.Contains("일반 경로", item.Message, StringComparison.Ordinal);
        Assert.Contains(report.Preview, p => p.Sql.StartsWith("OPTIONS (DIRECT=FALSE", StringComparison.Ordinal));
        Assert.False(CopyLoad.Direct(Assert.Single(report.Order)));
    }

    [Fact]
    public async Task Append_only_keeps_other_rows_so_the_existing_table_stays_conventional()
    {
        var report = await new Fixture(TargetAction.AppendOnly, LoadMethod.ExternalTool).RunAsync();

        Assert.Contains(report.Preview, p => p.Sql.StartsWith("OPTIONS (DIRECT=FALSE", StringComparison.Ordinal));
        Assert.Contains("남는 행이 있어", Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ExternalLoad).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Append_only_existing_table_says_a_failed_tool_run_is_not_retried()
    {
        var report = await new Fixture(TargetAction.AppendOnly, LoadMethod.ExternalTool).RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ExternalLoad);
        Assert.Contains("다시 넣지 않습니다", item.Message, StringComparison.Ordinal);
        Assert.Equal(LoadMethod.ExternalTool, Assert.Single(report.Order).Load);
    }

    [Fact]
    public async Task Table_default_external_is_followed_when_the_table_does_not_choose()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, load: null, defaults: new TableDefaults(null, null, false) { Load = LoadMethod.ExternalTool });

        var report = await f.RunAsync();

        Assert.Equal(LoadMethod.ExternalTool, Assert.Single(report.Order).Load);
    }

    [Fact]
    public async Task Probe_problem_falls_back_to_standard_with_a_pass_item()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, LoadMethod.ExternalTool,
            probeResult: new ExternalToolInfo(ExternalLoaderKind.SqlLoader, null, null, null, "sqlldr 을(를) 찾지 못했습니다"));

        var report = await f.RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ExternalLoadFallback);
        Assert.Equal(CheckLevel.Pass, item.Level);
        Assert.Contains("기본 방식", item.Message, StringComparison.Ordinal);
        Assert.Contains("찾지 못했습니다", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.ExternalLoad);
        Assert.Equal(LoadMethod.Standard, Assert.Single(report.Order).Load);
        Assert.DoesNotContain(report.Preview, p => p.Sql.StartsWith("OPTIONS (", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_external_load_kit_in_the_sessions_falls_back()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, LoadMethod.ExternalTool, withKit: false);

        var report = await f.RunAsync();

        Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ExternalLoadFallback && i.Level == CheckLevel.Pass);
        Assert.Equal(LoadMethod.Standard, Assert.Single(report.Order).Load);
        Assert.Null(report.ExternalTool);
        Assert.Empty(f.Probe.Checks);
    }

    [Fact]
    public async Task Merge_into_an_existing_table_falls_back_but_the_tool_is_still_checked()
    {
        var f = new Fixture(TargetAction.MergeByPk, LoadMethod.ExternalTool);

        var report = await f.RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ExternalLoadFallback);
        Assert.Contains("MERGE", item.Message, StringComparison.Ordinal);
        Assert.Equal(LoadMethod.Standard, Assert.Single(report.Order).Load);
        Assert.Single(f.Probe.Checks);
    }

    [Fact]
    public async Task Commit_at_end_falls_back_without_checking_the_tool()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, LoadMethod.ExternalTool, commit: CommitMode.AtEnd);

        var report = await f.RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ExternalLoadFallback);
        Assert.Contains("모두 끝난 뒤 한 번", item.Message, StringComparison.Ordinal);
        Assert.Equal(LoadMethod.Standard, Assert.Single(report.Order).Load);
        Assert.Empty(f.Probe.Checks);
    }

    [Fact]
    public async Task No_table_wants_the_tool_so_the_probe_is_never_called()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, LoadMethod.Standard, defaults: new TableDefaults(null, null, false) { Load = LoadMethod.ExternalTool });

        var report = await f.RunAsync();

        Assert.Empty(f.Probe.Checks);
        Assert.DoesNotContain(report.Items, i => i.Code is CopyCheckCodes.ExternalLoad or CopyCheckCodes.ExternalLoadFallback);
        Assert.Equal(LoadMethod.Standard, Assert.Single(report.Order).Load);
        Assert.Null(report.ExternalTool);
    }

    // ---- 가짜 협력자 ----

    private sealed class Fixture
    {
        public Fixture(TargetAction action, LoadMethod? load, TableDefaults? defaults = null, CommitMode commit = CommitMode.PerTable,
                       ExternalToolInfo? probeResult = null, bool withKit = true)
        {
            Job = new CopyJob
            {
                Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
                SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==",
                Tables = [new TableRule("ORDERS", RuleKind.All, action) { Load = load }],
                TableDefaults = defaults,
                Options = new CopyOptions { Commit = commit },
            };
            Source = new FakeSession(TestData.Profile(readOnly: true), TestData.Server()) { DbKey = "SRCDB/PDB1/SRC" };
            Target = new FakeSession(TestData.Profile() with { Name = "dev" }, TestData.Server()) { DbKey = "DSTDB/PDB1/DST" };
            var metadata = new FakeMetadataProvider();
            metadata.Tables[new DbObjectRef("SRC", "ORDERS", "TABLE")] = Orders("SRC");
            metadata.Tables[new DbObjectRef("DST", "ORDERS", "TABLE")] = Orders("DST");
            Probe = new FakeProbe(probeResult ?? Usable);
            Kit = withKit
                ? new CopyExternalLoad(Probe, new NeverRunner(), new ExternalToolPaths(@"D:\tools\sqlldr.exe", null), _ => Task.FromResult<ExternalLoadLogin?>(null))
                : null;
            var parts = new CopyDbmsParts(Dbms.Oracle, new IdCatalog(), new FakeClassifier(), new FakeDialect(), new FakePrecheckDialect(), new FakeCodec(), new FakeRecreateStep());
            _bulk = new FakePrecheckBulk(Source, Target);
            Service = new CopyPrecheckService(_bulk, metadata, new AllowPolicy(), parts, parts);
        }

        public CopyJob Job { get; }

        /// <summary>대상 테이블에 켜진 트리거가 하나 있다고 답한다(<c>copy.enabled_triggers</c>).</summary>
        public bool Triggers
        {
            get => _bulk.Triggers;
            init => _bulk.Triggers = value;
        }

        public FakeSession Source { get; }

        public FakeSession Target { get; }

        public FakeProbe Probe { get; }

        public CopyExternalLoad? Kit { get; }

        private readonly FakePrecheckBulk _bulk;

        private CopyPrecheckService Service { get; }

        public Task<PrecheckReport> RunAsync() =>
            Service.RunAsync(new CopySessions(Source, Target) { ExternalLoad = Kit }, Job, new ResolvedPeriod(null, null), null, CancellationToken.None);

        private static TableInfo Orders(string owner) =>
            new(new DbObjectRef(owner, "ORDERS", "TABLE"),
            [
                new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false },
                new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 },
            ])
            {
                Constraints = [new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ID"])],
            };
    }

    private sealed class FakeProbe(ExternalToolInfo result) : IExternalToolProbe
    {
        public List<(ExternalLoaderKind Kind, string? Path)> Checks { get; } = [];

        public ExternalToolLocation Locate(ExternalLoaderKind kind, string? configuredPath) => new(kind, configuredPath, configuredPath is not null, []);

        public Task<ExternalToolInfo> CheckAsync(ExternalLoaderKind kind, string? configuredPath, CancellationToken ct)
        {
            Checks.Add((kind, configuredPath));
            return Task.FromResult(result);
        }
    }

    /// <summary>사전 점검은 도구를 실행하지 않는다(버전 확인은 탐색기 몫).</summary>
    private sealed class NeverRunner : IExternalProcessRunner
    {
        public Task<ExternalProcessResult> RunAsync(ExternalProcessStart start, CancellationToken ct) => throw new InvalidOperationException("사전 점검이 도구를 실행했다.");
    }

    /// <summary>카탈로그 글 대신 id 를 그대로 글로 돌려준다(가짜 대량 읽기가 id 로 답한다).</summary>
    private sealed class IdCatalog : Oranda.Core.Catalog.ISqlCatalog
    {
        public Oranda.Core.Catalog.CatalogQuery Get(string id, ServerInfo server) =>
            new(new Oranda.Core.Catalog.CatalogHeader(id, 11, [], Oranda.Core.Catalog.CatalogLicense.None, Oranda.Core.Catalog.CatalogRac.V, id + ".sql"), id);
    }

    /// <summary>점검 조회(id)에 답한다. 모르는 id 는 행 없음, 원본 <c>SELECT COUNT(*)</c> 는 2.</summary>
    private sealed class FakePrecheckBulk(ISession source, ISession target) : IBulkExecutionService
    {
        public bool Triggers { get; set; }

        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var isTarget = ReferenceEquals(s, target);
            object?[][] rows = query.Text switch
            {
                "copy.session_user" => [[isTarget ? "DST" : "SRC"]],
                "copy.target_is_table" => [[1L]],
                "copy.enabled_triggers" when Triggers && isTarget => [["DST", "TRG_ORDERS"]],
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

    /// <summary>문장 글만 만드는 방언(값 자리 = <c>:이름</c>).</summary>
    private sealed class FakeDialect : ICopyDialect
    {
        public bool IsServerName(string name, string owner) => CreateTableDdl.IsServerName(name);

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
