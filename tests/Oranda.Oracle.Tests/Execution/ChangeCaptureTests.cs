using Oranda.Core.Journal;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Tests.Execution;

// 소유: TSK-13-07. 실행 전 DDL 보관 규칙(계획·실패 격리·제한 시간·취소). DB 없이 가짜 DDL 원천으로 본다.
public class ChangeCaptureTests
{
    private static readonly JournalEntry Template = new() { ProfileName = "p", DbKey = "FREE/FREEPDB1/APP", StatementSummary = "ALTER TABLE emp ADD x NUMBER" };

    private static Task<IReadOnlyList<JournalEntry>> Capture(ChangeTarget target, FakeSource source, TimeSpan? timeout = null, CancellationToken ct = default)
        => OracleExecutionService.ChangeCapture.CaptureAsync(target, Template, "APP", source, timeout ?? TimeSpan.FromSeconds(5), ct);

    [Fact]
    public async Task Alter_table_stores_one_row_with_resolved_owner()
    {
        var source = new FakeSource().Add("TABLE", new OracleExecutionService.DdlRead("CREATE TABLE \"APP\".\"EMP\" (ID NUMBER)", "APP"));

        var row = Assert.Single(await Capture(new ChangeTarget(null, "EMP", "TABLE", ChangeOperation.Alter), source));

        Assert.Equal("APP", row.Owner);
        Assert.Equal("EMP", row.Name);
        Assert.Equal("TABLE", row.ObjectType);
        Assert.Equal(JournalCause.Ddl, row.Cause);
        Assert.Equal("CREATE TABLE \"APP\".\"EMP\" (ID NUMBER)", row.PreviousDdl);
        Assert.Equal("ALTER TABLE emp ADD x NUMBER", row.StatementSummary);
        Assert.False(row.IsFailure);
    }

    [Fact]
    public async Task Drop_package_stores_spec_and_body_but_tolerates_missing_body()
    {
        var both = new FakeSource()
            .Add("PACKAGE_SPEC", new OracleExecutionService.DdlRead("spec", "APP"))
            .Add("PACKAGE_BODY", new OracleExecutionService.DdlRead("body", "APP"));
        var rows = await Capture(new ChangeTarget(null, "PKG", "PACKAGE", ChangeOperation.Drop), both);
        Assert.Equal([("PACKAGE", "spec"), ("PACKAGE BODY", "body")], rows.Select(r => (r.ObjectType, r.PreviousDdl!)));

        var noBody = new FakeSource()
            .Add("PACKAGE_SPEC", new OracleExecutionService.DdlRead("spec", "APP"))
            .Add("PACKAGE_BODY", NotFound());
        var only = Assert.Single(await Capture(new ChangeTarget(null, "PKG", "PACKAGE", ChangeOperation.Drop), noBody));
        Assert.Equal("PACKAGE", only.ObjectType);
    }

    [Fact]
    public async Task Create_or_replace_package_reads_spec_only_and_skips_new_objects()
    {
        var source = new FakeSource().Add("PACKAGE_SPEC", new OracleExecutionService.DdlRead("spec", "APP"));
        var row = Assert.Single(await Capture(new ChangeTarget(null, "PKG", "PACKAGE", ChangeOperation.CreateOrReplace), source));
        Assert.Equal("PACKAGE", row.ObjectType);
        Assert.Equal(["PACKAGE_SPEC"], source.Reads);

        var fresh = new FakeSource().Add("PACKAGE_BODY", NotFound());
        Assert.Empty(await Capture(new ChangeTarget("APP", "NEWPKG", "PACKAGE BODY", ChangeOperation.CreateOrReplace), fresh)); // 새 객체: 이전 DDL 없음
    }

    [Theory]
    [InlineData("PACKAGE BODY", "PACKAGE_BODY")]
    [InlineData("DATABASE LINK", "DB_LINK")]
    [InlineData("MATERIALIZED VIEW", "MATERIALIZED_VIEW")]
    [InlineData("TYPE BODY", "TYPE_BODY")]
    public async Task Maps_object_type_to_dbms_metadata_name(string type, string getDdlType)
    {
        var source = new FakeSource().Add(getDdlType, new OracleExecutionService.DdlRead("x", "APP"));
        var row = Assert.Single(await Capture(new ChangeTarget("APP", "O", type, ChangeOperation.Alter), source));
        Assert.Equal(type, row.ObjectType);
        Assert.Equal([getDdlType], source.Reads);
    }

    [Fact]
    public async Task Missing_object_or_privilege_error_becomes_a_failure_row()
    {
        var missing = new FakeSource().Add("TABLE", NotFound());
        var row = Assert.Single(await Capture(new ChangeTarget("HR", "GONE", "TABLE", ChangeOperation.Drop), missing));
        Assert.True(row.IsFailure);
        Assert.Null(row.PreviousDdl);
        Assert.Equal("HR", row.Owner);
        Assert.Contains("ORA-31603", row.FailureReason, StringComparison.Ordinal);

        var denied = new FakeSource().Add("TABLE", new OracleExecutionService.DdlRead(null, null, 31603 + 1, "ORA-31604 insufficient privileges"));
        var denial = Assert.Single(await Capture(new ChangeTarget(null, "T", "TABLE", ChangeOperation.Alter), denied));
        Assert.Equal("APP", denial.Owner); // 서버가 소유자를 못 알려 주면 접속 사용자
        Assert.Contains("insufficient privileges", denial.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_existing_object_is_a_failure_row_even_for_create_or_replace()
    {
        // 객체는 ALL_OBJECTS 에 있는데 DDL 을 못 읽음(예: SYS 객체): 「새 객체」로 오인해 조용히 넘기면 안 된다.
        var unreadable = new OracleExecutionService.DdlRead(null, "HR", OracleExecutionService.ChangeCapture.InsufficientPrivileges, "ORA-31603: 객체는 있으나 DDL 을 읽을 수 없다(권한 부족 등)");
        var source = new FakeSource().Add("VIEW", unreadable);
        var row = Assert.Single(await Capture(new ChangeTarget("HR", "V", "VIEW", ChangeOperation.CreateOrReplace), source));
        Assert.True(row.IsFailure);
        Assert.Contains("읽을 수 없다", row.FailureReason, StringComparison.Ordinal);

        // 본문 없는 패키지(NULL, ALL_OBJECTS 에도 없음 = 객체 없음 취급)는 DROP·ALTER 에서도 조용히 넘어간다.
        var spec = new FakeSource().Add("PACKAGE_SPEC", new OracleExecutionService.DdlRead("spec", "APP")).Add("PACKAGE_BODY", NotFound());
        Assert.Single(await Capture(new ChangeTarget(null, "P", "PACKAGE", ChangeOperation.Alter), spec));
    }

    [Fact]
    public async Task Unexpected_exception_is_contained_as_failure_row()
    {
        var source = new FakeSource { Throw = new InvalidOperationException("boom") };
        var row = Assert.Single(await Capture(new ChangeTarget(null, "T", "TABLE", ChangeOperation.Alter), source));
        Assert.True(row.IsFailure);
        Assert.Contains("boom", row.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_resolves_type_first_and_reports_unresolved_objects()
    {
        var source = new FakeSource { ResolvedType = "VIEW" }.Add("VIEW", new OracleExecutionService.DdlRead("view ddl", "APP"));
        var row = Assert.Single(await Capture(new ChangeTarget(null, "V1", null, ChangeOperation.Rename), source));
        Assert.Equal("VIEW", row.ObjectType);

        var unresolved = Assert.Single(await Capture(new ChangeTarget(null, "NOPE", null, ChangeOperation.Rename), new FakeSource()));
        Assert.True(unresolved.IsFailure);
        Assert.Equal("NOPE", unresolved.Name);
    }

    [Fact]
    public async Task Slow_server_hits_timeout_and_leaves_a_failure_row()
    {
        var source = new FakeSource { Block = true };
        var row = Assert.Single(await Capture(new ChangeTarget(null, "T", "TABLE", ChangeOperation.Alter), source, TimeSpan.FromMilliseconds(50)));
        Assert.True(row.IsFailure);
        Assert.Contains("시간 초과", row.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        var source = new FakeSource { Block = true };
        var pending = Capture(new ChangeTarget(null, "T", "TABLE", ChangeOperation.Alter), source, ct: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void Summary_is_first_nonblank_line_capped_at_200_chars()
    {
        Assert.Equal("ALTER TABLE t", OracleExecutionService.ChangeCapture.Summarize("\n  ALTER TABLE t  \nADD x NUMBER"));
        Assert.Equal(200, OracleExecutionService.ChangeCapture.Summarize(new string('a', 500)).Length);
    }

    private static OracleExecutionService.DdlRead NotFound()
        => new(null, null, OracleExecutionService.ChangeCapture.ObjectNotFound, "ORA-31603: object \"X\" of type TABLE not found in schema \"APP\"");

    private sealed class FakeSource : OracleExecutionService.IDdlSource
    {
        private readonly Dictionary<string, OracleExecutionService.DdlRead> _reads = [];

        public List<string> Reads { get; } = [];

        public string? ResolvedType { get; init; }

        public bool Block { get; init; }

        public Exception? Throw { get; init; }

        public FakeSource Add(string getDdlType, OracleExecutionService.DdlRead read)
        {
            _reads[getDdlType] = read;
            return this;
        }

        public Task<string?> ResolveTypeAsync(string? owner, string name, CancellationToken ct) => Task.FromResult(ResolvedType);

        public async Task<OracleExecutionService.DdlRead> ReadAsync(string getDdlType, string? owner, string name, CancellationToken ct)
        {
            Reads.Add(getDdlType);
            if (Throw is not null)
            {
                throw Throw;
            }

            if (Block)
            {
                await Task.Delay(Timeout.Infinite, ct); // 취소되면 OperationCanceledException(드라이버 Break 와 같은 효과).
            }

            return _reads.TryGetValue(getDdlType, out var read) ? read : NotFound();
        }
    }
}
