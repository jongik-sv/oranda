using System.Runtime.CompilerServices;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Core.Transfer.Copy.CrossDbms;
using Oranda.Testing;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 실행기의 교차 복사 경로(xdb-copy XC6, 서버 없음·가짜 두 DBMS 부품): 바인드 형식 옮기기, 옮길 형식이 없는 열 빼기·알림,
/// 값 변환(원본 코덱 ToPortable → 대상 코덱 FromPortable), 값 하나 변환 실패 = 그 행만 실패, 실패 행 다시 넣기의 같은 변환, 같은 DBMS 는 변환 없음.
/// </summary>
public sealed class CopyRunnerCrossDbmsTests
{
    // ---- 적재 ----

    [Fact]
    public async Task Converted_values_reach_the_dml_array_with_the_mapped_bind_types_and_the_dropped_column_is_left_out_and_noticed()
    {
        var f = new Fixture(Dbms.Oracle, Dbms.Tibero);
        f.Bulk.SourceRows = [[1L, "kim", 1.5], [3L, "park", 2.5]];

        var result = await f.RunAsync();

        // ROWID 열(RID)은 원본 SELECT 와 대상 DML 에서 빠진다.
        var select = Assert.Single(f.Bulk.SourceQueries);
        Assert.DoesNotContain("RID", select, StringComparison.Ordinal);
        var dml = Assert.Single(f.Bulk.Dml);
        Assert.StartsWith("INSERT INTO \"DST\".\"ORDERS\" (\"ID\", \"NAME\", \"V\") VALUES (:C1, :C2, :C3)", dml.Text, StringComparison.Ordinal);

        // 바인드 형식 = 원본 형식을 대상 DBMS 로 옮긴 형식(BINARY_DOUBLE → NUMBER, D6), 값 = 대상 코덱이 만든 새 값.
        Assert.Equal(["NUMBER", "VARCHAR2", "NUMBER"], dml.ServerTypes);
        Assert.Equal([new TargetValue("NUMBER", 1L), new TargetValue("VARCHAR2", "kim"), new TargetValue("NUMBER", 1.5)], dml.Rows[0]);
        Assert.Equal(2, dml.Rows.Count);
        Assert.Contains("BINARY_DOUBLE", f.SourceCodec.ToPortableTypes);
        Assert.Empty(f.TargetCodec.ToPortableTypes); // 원본 값은 원본 코덱만 읽는다
        Assert.Empty(f.SourceCodec.FromPortableTypes);

        var notice = Assert.Single(result.Notices);
        Assert.Contains("RID", notice, StringComparison.Ordinal);
        Assert.Contains("ROWID", notice, StringComparison.Ordinal);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Equal(2, table.TargetRows);
    }

    [Fact]
    public async Task A_value_that_cannot_be_converted_fails_only_its_row_and_the_others_are_loaded()
    {
        var f = new Fixture(Dbms.Oracle, Dbms.Tibero);
        f.Bulk.SourceRows = [[1L, "kim", 1.5], [2L, "lee", double.NaN], [3L, "park", 2.5]];

        var result = await f.RunAsync();

        var dml = Assert.Single(f.Bulk.Dml); // 못 바꾼 행은 보내지 않는다
        Assert.Equal([new TargetValue("NUMBER", 1L), new TargetValue("NUMBER", 3L)], dml.Rows.Select(r => r[0]));
        var failed = Assert.Single(result.FailedRows);
        Assert.Equal("2", failed.Key["ID"]); // 키는 원본 값(원본 코덱)
        Assert.Contains("열 V", failed.Error, StringComparison.Ordinal);
        Assert.Contains("바꾸지 못했습니다", failed.Error, StringComparison.Ordinal);
        Assert.Single(f.RunRecord.FailedRows);

        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.PartiallyFailed, table.Outcome);
        Assert.Equal(3, table.SourceRows);
        Assert.Equal(2, table.TargetRows);
    }

    [Fact]
    public async Task Batch_failure_retries_converted_rows_one_by_one_and_keys_failed_rows_by_source_values()
    {
        var f = new Fixture(Dbms.Tibero, Dbms.Oracle);
        f.Bulk.SourceRows = [[1L, "kim", 1.5], [3L, "park", 2.5]];
        f.Bulk.OnArray = call => call.Rows.Count > 1 || Equals(call.Rows[0][0], new TargetValue("NUMBER", 3L))
            ? new BulkResult(ExecutionStatus.Failed, 0) { Error = new DbError("ORA-00001", "unique constraint violated") }
            : null;

        var result = await f.RunAsync();

        Assert.Equal([2, 1, 1], f.Bulk.Dml.Select(d => d.Rows.Count));
        var failed = Assert.Single(result.FailedRows);
        Assert.Equal("3", failed.Key["ID"]);
        Assert.Equal(CopyOutcome.PartiallyFailed, Assert.Single(result.Tables).Outcome);
    }

    [Fact]
    public async Task Conversion_failure_and_batch_failure_in_one_batch_key_each_failed_row_by_its_own_source_row()
    {
        var f = new Fixture(Dbms.Oracle, Dbms.Tibero);
        f.Bulk.SourceRows = [[1L, "kim", 1.5], [2L, "lee", double.NaN], [3L, "park", 2.5], [4L, "choi", 3.5]];
        // 변환을 통과한 3행(1, 3, 4)을 한꺼번에 보내면 실패, 한 행씩은 4 만 실패한다.
        f.Bulk.OnArray = call => call.Rows.Count > 1 || Equals(call.Rows[0][0], new TargetValue("NUMBER", 4L))
            ? new BulkResult(ExecutionStatus.Failed, 0) { Error = new DbError("ORA-00001", "unique constraint violated") }
            : null;

        var result = await f.RunAsync();

        Assert.Equal([3, 1, 1, 1], f.Bulk.Dml.Select(d => d.Rows.Count)); // 못 바꾼 행(2)은 보내지 않는다
        // 변환 실패는 행 2, 배치 실패는 한 행씩 다시 넣은 4(변환된 행 목록의 3번째 = 원본 4번째 행).
        Assert.Equal(["2", "4"], result.FailedRows.Select(r => r.Key["ID"]).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Contains("바꾸지 못했습니다", result.FailedRows.Single(r => r.Key["ID"] == "2").Error, StringComparison.Ordinal);
        Assert.DoesNotContain("바꾸지 못했습니다", result.FailedRows.Single(r => r.Key["ID"] == "4").Error, StringComparison.Ordinal);
        Assert.Equal(2, f.RunRecord.FailedRows.Count);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.PartiallyFailed, table.Outcome);
        Assert.Equal(4, table.SourceRows);
        Assert.Equal(2, table.TargetRows); // 1, 3
    }

    [Theory]
    [InlineData(CommitMode.PerTable)]
    [InlineData(CommitMode.PerBatch)]
    public async Task A_batch_where_every_row_fails_conversion_sends_nothing_records_the_rows_and_the_run_continues(CommitMode commit)
    {
        var f = new Fixture(Dbms.Oracle, Dbms.Tibero, new CopyOptions { Commit = commit });
        f.Bulk.SourceRows = [[1L, "kim", double.NaN], [2L, "lee", double.NaN]];

        var result = await f.RunAsync();

        Assert.Empty(f.Bulk.Dml); // 보낼 행이 없으면 배열 DML 을 보내지 않는다
        Assert.Equal(["1", "2"], result.FailedRows.Select(r => r.Key["ID"]).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(2, f.RunRecord.FailedRows.Count);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.PartiallyFailed, table.Outcome);
        Assert.Equal(2, table.SourceRows);
        Assert.Equal(0, table.TargetRows);
    }

    [Fact]
    public async Task Failed_row_reason_is_a_fixed_text_without_the_value_and_codec_cast_format_errors_fail_only_that_row()
    {
        var f = new Fixture(Dbms.Oracle, Dbms.Tibero);
        // 코덱 예외 글에 값(-777.5 등)이 들어 있다: PortableConversionException, InvalidCastException, FormatException.
        f.Bulk.SourceRows = [[1L, "kim", -777.5], [2L, "lee", -888.5], [3L, "park", -999.5], [4L, "choi", 1.5]];

        var result = await f.RunAsync();

        Assert.Equal(3, result.FailedRows.Count);
        const string Fixed = "열 V 의 값(원본 형식 BINARY_DOUBLE)을 대상 형식(NUMBER)으로 바꾸지 못했습니다.";
        Assert.All(result.FailedRows, r => Assert.Equal(Fixed, r.Error));
        Assert.All(f.RunRecord.FailedRows, r => Assert.Equal(Fixed, r.Error));
        foreach (var value in new[] { "777.5", "888.5", "999.5" })
        {
            Assert.DoesNotContain(result.FailedRows, r => r.Error.Contains(value, StringComparison.Ordinal));
        }

        var dml = Assert.Single(f.Bulk.Dml); // 4 만 보낸다
        Assert.Equal([new TargetValue("NUMBER", 4L)], dml.Rows.Select(r => r[0]));
        Assert.Equal(CopyOutcome.PartiallyFailed, Assert.Single(result.Tables).Outcome);
    }

    [Fact]
    public async Task Retry_with_a_conversion_failure_fails_that_row_again_with_the_fixed_reason()
    {
        var f = new Fixture(Dbms.Oracle, Dbms.Tibero);
        f.Bulk.SourceRows = [[2L, "lee", -777.5]];

        var result = await f.RetryAsync(new FailedRow("ORDERS", new Dictionary<string, string?> { ["ID"] = "2" }, "이전 실패"));

        Assert.Empty(f.Bulk.Dml);
        var failed = Assert.Single(result.FailedRows);
        Assert.Equal("2", failed.Key["ID"]);
        Assert.Equal("열 V 의 값(원본 형식 BINARY_DOUBLE)을 대상 형식(NUMBER)으로 바꾸지 못했습니다.", failed.Error);
        Assert.DoesNotContain("777.5", failed.Error, StringComparison.Ordinal);
        Assert.Equal(CopyOutcome.PartiallyFailed, Assert.Single(result.Tables).Outcome);
    }

    [Fact]
    public async Task Retry_with_an_invalid_cast_from_the_codec_fails_that_row_again_instead_of_faulting_the_run()
    {
        var f = new Fixture(Dbms.Oracle, Dbms.Tibero);
        f.Bulk.SourceRows = [[2L, "lee", -888.5]];

        var result = await f.RetryAsync(new FailedRow("ORDERS", new Dictionary<string, string?> { ["ID"] = "2" }, "이전 실패"));

        Assert.Single(result.FailedRows);
        Assert.Empty(f.Bulk.Dml);
    }

    // ---- 세션·부품 DBMS 일치 ----

    [Theory]
    [InlineData(Dbms.Tibero, Dbms.Tibero)] // 원본 세션이 부품(Oracle)과 다르다
    [InlineData(Dbms.Oracle, Dbms.Oracle)] // 대상 세션이 부품(Tibero)과 다르다
    public async Task Session_dbms_that_differs_from_the_parts_dbms_is_refused_before_anything_is_done(Dbms sourceSession, Dbms targetSession)
    {
        var f = new Fixture(Dbms.Oracle, Dbms.Tibero, sourceSessionDbms: sourceSession, targetSessionDbms: targetSession);
        f.Bulk.SourceRows = [[1L, "kim", 1.5]];

        var run = await Assert.ThrowsAsync<InvalidOperationException>(() => f.RunAsync());
        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.RetryAsync(new FailedRow("ORDERS", new Dictionary<string, string?> { ["ID"] = "1" }, "이전 실패")));

        Assert.Contains("DBMS", run.Message, StringComparison.Ordinal);
        Assert.Contains("DBMS", retry.Message, StringComparison.Ordinal);
        Assert.Empty(f.Bulk.SourceQueries);
        Assert.Empty(f.Bulk.Dml);
    }

    // ---- 점검 계획과 실행기의 열 ----

    [Fact]
    public async Task Precheck_plan_columns_equal_the_columns_the_runner_selects_and_inserts_for_a_dropped_column()
    {
        var f = new Fixture(Dbms.Oracle, Dbms.Tibero);
        var metadata = new FakeMetadataProvider();
        metadata.Tables[new DbObjectRef("SRC", "ORDERS", "TABLE")] = Fixture.Orders("SRC");
        metadata.Tables[new DbObjectRef("DST", "ORDERS", "TABLE")] = Fixture.Orders("DST");
        var precheck = new CopyPrecheckService(new PrecheckBulk(f.Source, f.Target), metadata, new AllowPolicy(),
            Fixture.Parts(Dbms.Oracle, new CrossCodec()), Fixture.Parts(Dbms.Tibero, new CrossCodec()));

        var report = await precheck.RunAsync(new CopySessions(f.Source, f.Target), f.Job, new ResolvedPeriod(null, null), null, CancellationToken.None);

        Assert.False(report.HasErrors);
        var plan = Assert.Single(report.Order);
        Assert.Equal(["ID", "NAME", "V"], plan.Columns); // RID(ROWID)는 점검이 뺀다

        f.Bulk.SourceRows = [[1L, "kim", 1.5]];
        await f.RunAsync(report);

        var select = Assert.Single(f.Bulk.SourceQueries);
        Assert.Contains(string.Join(", ", plan.Columns.Select(c => "\"" + c + "\"")), select, StringComparison.Ordinal);
        Assert.DoesNotContain("RID", select, StringComparison.Ordinal);
        var dml = Assert.Single(f.Bulk.Dml);
        Assert.Contains("(" + string.Join(", ", plan.Columns.Select(c => "\"" + c + "\"")) + ")", dml.Text, StringComparison.Ordinal);
        Assert.Equal(plan.Columns.Count, dml.ServerTypes.Count);
    }

    // ---- 실패 행 다시 넣기 ----

    [Fact]
    public async Task Retry_of_failed_rows_applies_the_same_column_mapping_and_value_conversion()
    {
        var f = new Fixture(Dbms.Oracle, Dbms.Tibero);
        f.Bulk.SourceRows = [[2L, "lee", 4.5]];

        var result = await f.RetryAsync(new FailedRow("ORDERS", new Dictionary<string, string?> { ["ID"] = "2" }, "이전 실패"));

        var select = Assert.Single(f.Bulk.SourceQueries);
        Assert.DoesNotContain("RID", select, StringComparison.Ordinal);
        var dml = Assert.Single(f.Bulk.Dml);
        Assert.Equal(["NUMBER", "VARCHAR2", "NUMBER"], dml.ServerTypes);
        Assert.Equal([new TargetValue("NUMBER", 2L), new TargetValue("VARCHAR2", "lee"), new TargetValue("NUMBER", 4.5)], dml.Rows[0]);
        Assert.Empty(result.FailedRows);
        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
    }

    // ---- 같은 DBMS: 변환 없음(회귀 0) ----

    [Fact]
    public async Task Same_dbms_copy_sends_the_read_values_as_they_are_with_source_types_and_no_dropped_column()
    {
        var f = new Fixture(Dbms.Oracle, Dbms.Oracle);
        f.Bulk.SourceRows = [[1L, "kim", 1.5, "AAAR1"]];

        var result = await f.RunAsync();

        var dml = Assert.Single(f.Bulk.Dml);
        Assert.Equal(["NUMBER", "VARCHAR2", "BINARY_DOUBLE", "ROWID"], dml.ServerTypes);
        Assert.Equal([1L, "kim", 1.5, "AAAR1"], dml.Rows[0]);
        Assert.Empty(f.SourceCodec.ToPortableTypes);
        Assert.Empty(f.SourceCodec.FromPortableTypes);
        Assert.Empty(result.Notices);
    }

    // ---- 같은 DB 판정 ----

    [Fact]
    public void Different_dbms_is_never_the_same_database_even_with_the_same_db_key()
    {
        var server = TestData.Server();
        Assert.True(CopySameDatabase.IsSameDatabase(Dbms.Oracle, "FREE/FREEPDB1/A", server, Dbms.Oracle, "FREE/FREEPDB1/B", server));
        Assert.False(CopySameDatabase.IsSameDatabase(Dbms.Oracle, "FREE/FREEPDB1/A", server, Dbms.Tibero, "FREE/FREEPDB1/B", server));
    }

    // ---- 가짜 협력자 ----

    /// <summary>원본 코덱의 이식 값(원본 형식·값).</summary>
    private sealed record Portable(string SourceType, object Value);

    /// <summary>대상 코덱이 만든 쓰기 값(바인드 형식·값).</summary>
    private sealed record TargetValue(string BindType, object Value);

    private sealed record DmlCall(string Text, IReadOnlyList<string> ServerTypes, IReadOnlyList<object?[]> Rows);

    private sealed class Fixture
    {
        public Fixture(Dbms sourceDbms, Dbms targetDbms, CopyOptions? options = null, Dbms? sourceSessionDbms = null, Dbms? targetSessionDbms = null)
        {
            Rule = new TableRule("ORDERS", RuleKind.All, TargetAction.AppendOnly);
            Job = new CopyJob
            {
                Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
                SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==", Tables = [Rule], Options = options ?? new CopyOptions(),
            };
            var plan = new CopyTablePlan(Rule, Orders("SRC"), Orders("DST"), ["ID", "NAME", "V", "RID"], ["ID"]);
            Report = new PrecheckReport([], [plan], [], new Dictionary<string, long>());

            Source = new FakeSession(TestData.Profile(readOnly: true) with { Dbms = sourceSessionDbms ?? sourceDbms }, TestData.Server()) { DbKey = "SRCDB/PDB1/SRC" };
            Target = new FakeSession(TestData.Profile() with { Name = "dev", Dbms = targetSessionDbms ?? targetDbms }, TestData.Server()) { DbKey = "DSTDB/PDB1/DST" };
            Bulk = new FakeBulk(this);
            RunRecord = new FakeRunRecord();
            SourceCodec = new CrossCodec();
            TargetCodec = sourceDbms == targetDbms ? SourceCodec : new CrossCodec();
            var source = Parts(sourceDbms, SourceCodec);
            var target = sourceDbms == targetDbms ? source : Parts(targetDbms, TargetCodec);
            Runner = new CopyRunner(Bulk, new FakeExecutionService(), new FakeRunLog(RunRecord), source, target);
        }

        public TableRule Rule { get; }

        public CopyJob Job { get; }

        public PrecheckReport Report { get; }

        public FakeSession Source { get; }

        public FakeSession Target { get; }

        public FakeBulk Bulk { get; }

        public FakeRunRecord RunRecord { get; }

        public CrossCodec SourceCodec { get; }

        public CrossCodec TargetCodec { get; }

        private CopyRunner Runner { get; }

        public Task<CopyRunResult> RunAsync(PrecheckReport? report = null) => Runner.RunAsync(new CopySessions(Source, Target), Job, report ?? Report,
            new CopyDecisions(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false),
            new ResolvedPeriod(null, null), null, new CopyRunControl(), CancellationToken.None);

        public Task<CopyRunResult> RetryAsync(params FailedRow[] rows) =>
            Runner.RetryFailedRowsAsync(new CopySessions(Source, Target), Job, Report, rows, null, CancellationToken.None);

        public static CopyDbmsParts Parts(Dbms dbms, IBulkValueCodec codec) =>
            new(dbms, new NoCatalog(), new FakeClassifier(dbms), new FakeDialect(), new FakePrecheckDialect(), codec, new FakeRecreateStep());

        /// <summary>ID NUMBER(키)·NAME VARCHAR2·V BINARY_DOUBLE(Oracle→Tibero 는 NUMBER 바인드)·RID ROWID(교차면 Dropped).</summary>
        public static TableInfo Orders(string owner) =>
            new(new DbObjectRef(owner, "ORDERS", "TABLE"),
            [
                new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false },
                new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 },
                new ColumnInfo("V", 3, "BINARY_DOUBLE"),
                new ColumnInfo("RID", 4, "ROWID"),
            ])
            {
                Constraints = [new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ID"])],
            };
    }

    private sealed class FakeBulk(Fixture fixture) : IBulkExecutionService
    {
        private long _inserted;

        /// <summary>원본 읽기가 낼 행(읽을 때마다 새 배열로 낸다).</summary>
        public IReadOnlyList<object?[]> SourceRows { get; set; } = [];

        /// <summary>배열 DML 응답. null 이면 성공(행 수만큼 영향).</summary>
        public Func<DmlCall, BulkResult?>? OnArray { get; set; }

        public List<string> SourceQueries { get; } = [];

        public List<DmlCall> Dml { get; } = [];

        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            if (ReferenceEquals(s, fixture.Source))
            {
                SourceQueries.Add(query.Text);
                foreach (var row in SourceRows)
                {
                    yield return [.. row];
                }

                yield break;
            }

            yield return [_inserted]; // 대상 COUNT(*)
        }

        public Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
                                                  ExecutionOptions options, CancellationToken ct)
        {
            if (columns.Count == 0)
            {
                return Task.FromResult(new BulkResult(ExecutionStatus.Succeeded, 0)); // 트랜잭션 제어
            }

            var call = new DmlCall(stmt.Text, [.. columns.Select(c => c.ServerType)], [.. rows.Select(r => r.ToArray())]);
            Dml.Add(call);
            var result = OnArray?.Invoke(call) ?? new BulkResult(ExecutionStatus.Succeeded, rows.Count);
            if (result.Status == ExecutionStatus.Succeeded && ReferenceEquals(s, fixture.Target))
            {
                _inserted += rows.Count;
            }

            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// 이식 값을 아는 가짜 코덱: <c>ToPortable</c> = <see cref="Portable"/>, <c>FromPortable</c> = <see cref="TargetValue"/>(NaN double 은 <see cref="PortableConversionException"/>).
    /// 어느 벌의 코덱이 불렸는지 형식 목록으로 남긴다.
    /// </summary>
    private sealed class CrossCodec : IBulkValueCodec
    {
        public List<string> ToPortableTypes { get; } = [];

        public List<string> FromPortableTypes { get; } = [];

        public bool IsSupported(string serverType) => true;

        public BulkKind KindOf(string serverType) => serverType.StartsWith("VARCHAR", StringComparison.Ordinal) ? BulkKind.Text : BulkKind.Other;

        public BindDataType BindTypeOf(string serverType) => BindDataType.Number;

        public string? ToKeyText(object? value, string serverType) =>
            value is null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

        public object? FromKeyText(string? text, string serverType) => text;

        public long? ToInt64(object? value) => value is long l ? l : null;

        public string? ToText(object? value) => value?.ToString();

        public string? NumberText(object? value) => value is long l ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        public bool IsZero(object? value) => value is 0L;

        public long ToInt64Exact(object? value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

        public object? ToPortable(object? value, string serverType)
        {
            ToPortableTypes.Add(serverType);
            return value is null ? null : new Portable(serverType, value);
        }

        public object? FromPortable(object? portable, string bindType)
        {
            FromPortableTypes.Add(bindType);
            return portable switch
            {
                null => null,
                Portable { Value: double d } when double.IsNaN(d) => throw new PortableConversionException("NaN 은 NUMBER 로 옮길 수 없습니다."),
                // 코덱 예외 글에 행 값이 들어 있는 경우(실제 코덱처럼): 실패 사유에 그 값이 새면 안 된다.
                Portable { Value: -777.5 } => throw new PortableConversionException("값 -777.5 는 범위 밖입니다."),
                Portable { Value: -888.5 } => throw new InvalidCastException("값 -888.5 의 형식이 맞지 않습니다."),
                Portable { Value: -999.5 } => throw new FormatException("값 -999.5 를 읽을 수 없습니다."),
                Portable p => new TargetValue(bindType, p.Value),
                _ => throw new InvalidOperationException("이식 값이 아닙니다."),
            };
        }
    }

    private sealed class FakeRunRecord : ICopyRunRecord
    {
        public List<FailedRow> FailedRows { get; } = [];

        public Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct) => Task.CompletedTask;

        public Task RecordStatementAsync(string sql, CancellationToken ct) => Task.CompletedTask;

        public Task RecordTableAsync(TableRunResult result, CancellationToken ct) => Task.CompletedTask;

        public Task RecordFailedRowAsync(FailedRow row, CancellationToken ct)
        {
            FailedRows.Add(row);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CopyOutcome outcome, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRunLog(FakeRunRecord record) : ICopyRunLog
    {
        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => Task.FromResult<ICopyRunRecord>(record);

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>조회 id 를 그대로 문장으로 돌려주는 카탈로그(실행기는 쓰지 않고, 점검 서비스가 쓴다).</summary>
    private sealed class NoCatalog : Oranda.Core.Catalog.ISqlCatalog
    {
        public Oranda.Core.Catalog.CatalogQuery Get(string id, ServerInfo server) =>
            new(new Oranda.Core.Catalog.CatalogHeader(id, 11, [], Oranda.Core.Catalog.CatalogLicense.None, Oranda.Core.Catalog.CatalogRac.V, id + ".sql"), id);
    }

    /// <summary>점검 조회(id)에 답한다. 모르는 id 는 행 없음, <c>SELECT COUNT(*)</c> 는 2.</summary>
    private sealed class PrecheckBulk(ISession source, ISession target) : IBulkExecutionService
    {
        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var isTarget = ReferenceEquals(s, target);
            object?[][] rows = query.Text switch
            {
                "copy.session_user" => [[isTarget ? "DST" : "SRC"]],
                "copy.target_is_table" => [[1L]],
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

    private sealed class FakeClassifier(Dbms dbms) : IDbErrorClassifier
    {
        public Dbms Dbms => dbms;

        public DbErrorKind Classify(int number) => DbErrorKind.Other;

        public DbError? FromException(Exception exception) => null;
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
