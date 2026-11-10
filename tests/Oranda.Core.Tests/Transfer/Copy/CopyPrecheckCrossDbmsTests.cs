using System.Runtime.CompilerServices;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 교차 DBMS 복사(원본·대상 DBMS 가 다름)의 사전 점검(xdb-copy XC7): 옮길 형식이 없는 열은 오류가 아니라 복사 열에서 빠지고 알림이 된다.
/// 같은 DBMS 의 <c>UnsupportedType</c> 오류는 그대로다. 서버 없음, 가짜 협력자.
/// </summary>
public sealed class CopyPrecheckCrossDbmsTests
{
    [Fact]
    public async Task Dropped_column_is_a_confirm_item_removed_from_the_plan_and_the_preview_select_and_is_not_an_unsupported_type_error()
    {
        var p = new Fixture(Dbms.Oracle, Dbms.Tibero,
            source: Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false }, new ColumnInfo("OLD_LONG", 2, "LONG")),
            target: Table("DST", new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false }, new ColumnInfo("OLD_LONG", 2, "CLOB")),
            unsupportedBySource: ["LONG"]);

        var report = await p.RunAsync();

        var dropped = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.ColumnDropped);
        Assert.Equal(CheckLevel.Confirm, dropped.Level);
        Assert.Equal("ORDERS", dropped.Table);
        Assert.Equal("OLD_LONG", dropped.Column);
        Assert.Contains("ORDERS.OLD_LONG", dropped.Message, StringComparison.Ordinal);
        Assert.Contains("LONG", dropped.Message, StringComparison.Ordinal);
        Assert.Contains("Tibero", dropped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Items, i => i.Code == "UnsupportedType");
        Assert.DoesNotContain(report.Items, i => i.Code == "TargetRequiredColumnMissing");

        // 계획의 열 = 실행기가 복사할 열. 미리 보기 SELECT 도 같은 열이다.
        var plan = Assert.Single(report.Order);
        Assert.Equal(["ID"], plan.Columns);
        var select = Assert.Single(report.Preview, x => x.Side == PreviewSide.Source);
        Assert.DoesNotContain("OLD_LONG", select.Sql, StringComparison.Ordinal);
        Assert.Contains("\"ID\"", select.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dropped_column_that_is_not_null_without_default_in_the_target_stays_a_required_column_error()
    {
        var p = new Fixture(Dbms.Oracle, Dbms.Tibero,
            source: Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("OLD_LONG", 2, "LONG")),
            target: Table("DST", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("OLD_LONG", 2, "CLOB") { IsNullable = false }),
            unsupportedBySource: ["LONG"]);

        var report = await p.RunAsync();

        Assert.True(report.HasErrors);
        var required = Assert.Single(report.Items, i => i.Code == "TargetRequiredColumnMissing");
        Assert.Equal(CheckLevel.Error, required.Level);
        Assert.Equal("OLD_LONG", required.Column);
        Assert.Contains(report.Items, i => i.Code == CopyCheckCodes.ColumnDropped);
        Assert.Equal(["ID"], Assert.Single(report.Order).Columns);
    }

    [Fact]
    public async Task Converted_columns_are_summarised_in_one_type_converted_item_per_table_and_stay_in_the_plan()
    {
        var p = new Fixture(Dbms.Oracle, Dbms.Tibero,
            source: Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("RATIO", 2, "BINARY_DOUBLE"), new ColumnInfo("AT", 3, "TIMESTAMP(6) WITH TIME ZONE")),
            target: Table("DST", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("RATIO", 2, "NUMBER"), new ColumnInfo("AT", 3, "TIMESTAMP(6) WITH TIME ZONE")));

        var report = await p.RunAsync();

        var converted = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.TypeConverted);
        Assert.Equal("ORDERS", converted.Table);
        Assert.Equal(CheckLevel.Confirm, converted.Level); // 값이 바뀌거나 행이 실패할 수 있는 변환
        Assert.Contains("RATIO", converted.Message, StringComparison.Ordinal);
        Assert.Contains("BINARY_DOUBLE", converted.Message, StringComparison.Ordinal);
        Assert.Contains("AT", converted.Message, StringComparison.Ordinal);
        Assert.Contains("오프셋", converted.Message, StringComparison.Ordinal);
        Assert.Equal(["ID", "RATIO", "AT"], Assert.Single(report.Order).Columns);
        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.ColumnDropped);
    }

    [Fact]
    public async Task Oracle_to_tibero_with_a_blob_and_text_columns_adds_the_empty_blob_note_and_one_charset_warning()
    {
        var p = new Fixture(Dbms.Oracle, Dbms.Tibero,
            source: Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 }, new ColumnInfo("IMG", 3, "BLOB")),
            target: Table("DST", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 }, new ColumnInfo("IMG", 3, "BLOB")));

        var report = await p.RunAsync();

        var blob = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.BlobEmptyToNull);
        Assert.Equal(CheckLevel.Pass, blob.Level);
        Assert.Contains("IMG", blob.Message, StringComparison.Ordinal);
        Assert.Contains("NULL", blob.Message, StringComparison.Ordinal);
        var charset = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.CrossCharset);
        Assert.Equal(CheckLevel.Confirm, charset.Level);
        Assert.Null(charset.Table);
        Assert.Contains("대상 문자셋 밖 글자는 실패하거나 바뀔 수 있습니다", charset.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tibero_to_oracle_adds_neither_the_blob_note_nor_the_charset_warning()
    {
        var p = new Fixture(Dbms.Tibero, Dbms.Oracle,
            source: Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 }, new ColumnInfo("IMG", 3, "BLOB")),
            target: Table("DST", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 }, new ColumnInfo("IMG", 3, "BLOB")));

        var report = await p.RunAsync();

        Assert.DoesNotContain(report.Items, i => i.Code is CopyCheckCodes.BlobEmptyToNull or CopyCheckCodes.CrossCharset or CopyCheckCodes.ColumnDropped);
    }

    [Fact]
    public async Task Same_dbms_unsupported_type_is_still_an_error_with_the_skip_choice_and_no_cross_items()
    {
        var p = new Fixture(Dbms.Oracle, Dbms.Oracle,
            source: Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("OLD_LONG", 2, "LONG"), new ColumnInfo("IMG", 3, "BLOB")),
            target: Table("DST", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("OLD_LONG", 2, "LONG"), new ColumnInfo("IMG", 3, "BLOB")),
            unsupportedBySource: ["LONG"]);

        var report = await p.RunAsync();

        var unsupported = Assert.Single(report.Items, i => i.Code == "UnsupportedType");
        Assert.Equal(CheckLevel.Error, unsupported.Level);
        Assert.Equal([CheckChoice.SkipColumn], unsupported.Choices);
        Assert.Equal(["ID", "IMG"], Assert.Single(report.Order).Columns);
        Assert.DoesNotContain(report.Items, i => i.Code is CopyCheckCodes.ColumnDropped or CopyCheckCodes.TypeConverted
                                                     or CopyCheckCodes.BlobEmptyToNull or CopyCheckCodes.CrossCharset);
    }

    [Fact]
    public async Task Cross_writability_is_judged_on_the_bind_type_not_on_the_target_column_type()
    {
        // 대상 코덱은 "TIMESTAMP WITH TIME ZONE" 을 쓰지 못한다.
        // AT: 원본 TZ 형식(바인드 형식 TIMESTAMP WITH TIME ZONE) → 대상 열이 VARCHAR2 여도 바인드가 실패하므로 UnsupportedType.
        // TS: 원본 TIMESTAMP(바인드 형식 TIMESTAMP) → 대상 열이 TZ 형식이어도 값은 TIMESTAMP 로 묶이므로 쓸 수 있다.
        var p = new Fixture(Dbms.Oracle, Dbms.Tibero,
            source: Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("AT", 2, "TIMESTAMP(6) WITH TIME ZONE"), new ColumnInfo("TS", 3, "TIMESTAMP(6)")),
            target: Table("DST", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("AT", 2, "VARCHAR2") { Length = 40 }, new ColumnInfo("TS", 3, "TIMESTAMP WITH TIME ZONE")),
            unsupportedByTarget: ["TIMESTAMP WITH TIME ZONE"]);

        var report = await p.RunAsync();

        var unsupported = Assert.Single(report.Items, i => i.Code == "UnsupportedType");
        Assert.Equal(CheckLevel.Error, unsupported.Level);
        Assert.Equal("AT", unsupported.Column);
        Assert.Contains("TIMESTAMP WITH TIME ZONE", unsupported.Message, StringComparison.Ordinal);
        Assert.Equal(["ID", "TS"], Assert.Single(report.Order).Columns);
    }

    [Fact]
    public async Task Cross_writability_uses_the_cross_writable_answer_of_the_target_codec()
    {
        // 대상 코덱이 같은 DBMS 로는 TZ 를 지원하지 않지만 교차 쓰기는 된다(XC5: Tibero TZ 교차 쓰기) → 교차 점검은 막지 않는다.
        var p = new Fixture(Dbms.Oracle, Dbms.Tibero,
            source: Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("AT", 2, "TIMESTAMP(6) WITH TIME ZONE")),
            target: Table("DST", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("AT", 2, "TIMESTAMP(6) WITH TIME ZONE")),
            unsupportedByTarget: ["TIMESTAMP WITH TIME ZONE"], crossWritableByTarget: ["TIMESTAMP WITH TIME ZONE"]);

        var report = await p.RunAsync();

        Assert.DoesNotContain(report.Items, i => i.Code == "UnsupportedType");
        Assert.Equal(["ID", "AT"], Assert.Single(report.Order).Columns);
    }

    [Fact]
    public async Task Dropped_primary_key_column_is_a_key_column_dropped_error_instead_of_a_no_primary_key_item()
    {
        var p = new Fixture(Dbms.Oracle, Dbms.Tibero,
            source: Table("SRC", new ColumnInfo("ID", 1, "LONG") { IsNullable = false }, new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 }),
            target: Table("DST", new ColumnInfo("ID", 1, "CLOB") { IsNullable = false }, new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 }),
            unsupportedBySource: ["LONG"]);

        var report = await p.RunAsync();

        var keyDropped = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.KeyColumnDropped);
        Assert.Equal(CheckLevel.Error, keyDropped.Level);
        Assert.Equal("ORDERS", keyDropped.Table);
        Assert.Equal("ID", keyDropped.Column);
        Assert.Contains("키", keyDropped.Message, StringComparison.Ordinal);
        Assert.Contains("Tibero", keyDropped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Items, i => i.Code == "NoPrimaryKey");
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void Dropped_period_or_parent_link_column_is_a_key_column_dropped_error_naming_the_role()
    {
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("DT", 2, "LONG"), new ColumnInfo("PID", 3, "LONG"));
        var target = Table("DST", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("DT", 2, "DATE"), new ColumnInfo("PID", 3, "NUMBER"));
        var rule = new TableRule("ORDERS", RuleKind.Period, TargetAction.DeleteRangeInsert)
        {
            PeriodColumns = [new PeriodColumn("DT")],
            Parent = new ParentLink("PARENT", ["PID"], ["ID"]),
        };
        var codec = new FakeCodec(["LONG"]);

        var items = PrecheckRules.CompareColumns(codec, new FakeCodec([]), source, target, rule, "ORDERS", Dbms.Oracle, Dbms.Tibero);

        var period = Assert.Single(items, i => i.Code == CopyCheckCodes.KeyColumnDropped && i.Column == "DT");
        Assert.Equal(CheckLevel.Error, period.Level);
        Assert.Contains("기간 조건", period.Message, StringComparison.Ordinal);
        var link = Assert.Single(items, i => i.Code == CopyCheckCodes.KeyColumnDropped && i.Column == "PID");
        Assert.Contains("부모 키 연결", link.Message, StringComparison.Ordinal);
        Assert.Contains(items, i => i.Code == CopyCheckCodes.ColumnDropped && i.Column == "DT");

        // 같은 DBMS 는 이 항목이 없다.
        Assert.DoesNotContain(PrecheckRules.CompareColumns(new FakeCodec([]), new FakeCodec([]), source, target, rule, "ORDERS"),
            i => i.Code == CopyCheckCodes.KeyColumnDropped);
    }

    [Fact]
    public void Masks_skip_the_dropped_columns_and_ddl_only_conversions_are_noted_only_when_the_target_table_is_created_from_the_source()
    {
        // 마스킹: 복사하지 않는 열(교차에서 빠지는 열)에는 Pass 도 오류도 없다.
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 });
        var rule = new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert) { Masks = [new MaskRule("NAME", MaskKind.Null)] };
        Assert.Contains(PrecheckRules.CheckMasks(rule, source, ["ID"], "ORDERS"), i => i.Code == "MaskedColumns" && i.Level == CheckLevel.Pass);
        Assert.Empty(PrecheckRules.CheckMasks(rule, source, ["ID"], "ORDERS", new HashSet<string>(StringComparer.Ordinal) { "NAME" }));

        // 형식 변환 안내: DDL 에만 해당하는 변환(RAW → BLOB)은 있는 대상 표에는 내지 않고, 값 변환(BINARY_DOUBLE → NUMBER)은 늘 낸다.
        var src = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("BIGRAW", 2, "RAW") { Length = 3000 }, new ColumnInfo("RATIO", 3, "BINARY_DOUBLE"));
        var dst = Table("DST", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("BIGRAW", 2, "BLOB"), new ColumnInfo("RATIO", 3, "BINARY_DOUBLE"));
        var all = new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert);
        var codec = new FakeCodec([]);

        var existing = PrecheckRules.CompareColumns(codec, codec, src, dst, all, "ORDERS", Dbms.Oracle, Dbms.Tibero, targetFromSource: false);
        var existingNote = Assert.Single(existing, i => i.Code == CopyCheckCodes.TypeConverted);
        Assert.Contains("RATIO", existingNote.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BIGRAW", existingNote.Message, StringComparison.Ordinal);

        var created = PrecheckRules.CompareColumns(codec, codec, src, dst, all, "ORDERS", Dbms.Oracle, Dbms.Tibero, targetFromSource: true);
        var createdNote = Assert.Single(created, i => i.Code == CopyCheckCodes.TypeConverted);
        Assert.Contains("RATIO", createdNote.Message, StringComparison.Ordinal);
        Assert.Contains("BIGRAW", createdNote.Message, StringComparison.Ordinal);

        // DDL 변환뿐이면 있는 대상 표에는 항목이 아예 없다.
        var rawOnly = PrecheckRules.CompareColumns(codec, codec, Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("BIGRAW", 2, "RAW") { Length = 3000 }),
            Table("DST", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("BIGRAW", 2, "BLOB")), all, "ORDERS", Dbms.Oracle, Dbms.Tibero);
        Assert.DoesNotContain(rawOnly, i => i.Code == CopyCheckCodes.TypeConverted);
    }

    // ---- 도우미 ----

    private static TableInfo Table(string owner, params ColumnInfo[] columns) =>
        new(new DbObjectRef(owner, "ORDERS", "TABLE"), columns)
        {
            Constraints = [new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ID"])],
        };

    private static CopyDbmsParts Parts(Dbms dbms, IBulkValueCodec codec) =>
        new(dbms, new FakeCatalog(), new FakeClassifier(dbms), new FakeDialect(), new FakePrecheckDialect(), codec, new FakeRecreateStep());

    private sealed class Fixture
    {
        public Fixture(Dbms sourceDbms, Dbms targetDbms, TableInfo source, TableInfo target, string[]? unsupportedBySource = null,
                       string[]? unsupportedByTarget = null, string[]? crossWritableByTarget = null)
        {
            Job = new CopyJob
            {
                Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
                SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==",
                Tables = [new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert)],
            };
            Source = new FakeSession(TestData.Profile(readOnly: true), TestData.Server()) { DbKey = "SRCDB/PDB1/SRC" };
            Target = new FakeSession(TestData.Profile(ConnectionEnvironment.Development) with { Name = "dev" }, TestData.Server()) { DbKey = "DSTDB/PDB1/DST" };
            var metadata = new FakeMetadataProvider();
            metadata.Tables[new DbObjectRef("SRC", "ORDERS", "TABLE")] = source;
            metadata.Tables[new DbObjectRef("DST", "ORDERS", "TABLE")] = target;
            Service = new CopyPrecheckService(new FakePrecheckBulk(Source, Target), metadata, new AllowPolicy(),
                Parts(sourceDbms, new FakeCodec(unsupportedBySource ?? [])), Parts(targetDbms, new FakeCodec(unsupportedByTarget ?? [], crossWritableByTarget ?? [])));
        }

        public CopyJob Job { get; }

        public FakeSession Source { get; }

        public FakeSession Target { get; }

        private CopyPrecheckService Service { get; }

        public Task<PrecheckReport> RunAsync() => Service.RunAsync(new CopySessions(Source, Target), Job, new ResolvedPeriod(null, null), null, CancellationToken.None);
    }

    // ---- 가짜 협력자 ----

    private sealed class FakeCatalog : Oranda.Core.Catalog.ISqlCatalog
    {
        public Oranda.Core.Catalog.CatalogQuery Get(string id, ServerInfo server) =>
            new(new Oranda.Core.Catalog.CatalogHeader(id, 11, [], Oranda.Core.Catalog.CatalogLicense.None, Oranda.Core.Catalog.CatalogRac.V, id + ".sql"), id);
    }

    /// <summary>점검 조회(id)에 답한다. 모르는 id 는 행 없음, <c>SELECT COUNT(*)</c> 는 2. 대상은 표다.</summary>
    private sealed class FakePrecheckBulk(ISession source, ISession target) : IBulkExecutionService
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

    private sealed class FakePrecheckDialect : ICopyPrecheckDialect
    {
        public bool SupportsReadPrivilege => false;

        public string? RecreateBlockedReason(ISession target) => null;

        public Task<IReadOnlyList<string>> ExtendedStringBlockersAsync(TableInfo source, Func<string, CancellationToken, Task<string?>> targetParameter,
                                                                       CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

        public IReadOnlyList<(string CatalogId, string Label)> NotRestoredProbes => [];

        public string RecycleBinNote(string? tablespace) => string.Empty;

        public string? ReadConsistencyParameter => null;

        public (CheckLevel Level, string Message) ReadConsistencyHint(long estimatedSeconds, long? retention) => (CheckLevel.Pass, "읽기 일관성");

        public string ChildRecordFoundError => "ORA-02292";
    }

    private sealed class FakeClassifier(Dbms dbms) : IDbErrorClassifier
    {
        public Dbms Dbms => dbms;

        public DbErrorKind Classify(int number) => DbErrorKind.Other;

        public DbError? FromException(Exception exception) => null;
    }

    /// <summary>
    /// <paramref name="unsupported"/> 에 든 형식(괄호 앞 이름)은 지원하지 않는 형식이다(읽기도 같다).
    /// <paramref name="crossWritable"/> 에 든 형식은 같은 DBMS 로는 지원하지 않아도 교차 쓰기는 된다(Tibero TIMESTAMP WITH TIME ZONE 처럼).
    /// </summary>
    private sealed class FakeCodec(string[] unsupported, string[]? crossWritable = null) : IBulkValueCodec
    {
        public bool IsSupported(string serverType) => !unsupported.Contains(serverType.Split('(')[0].Trim(), StringComparer.OrdinalIgnoreCase);

        public bool IsCrossWritable(string bindType)
            => IsSupported(bindType) || (crossWritable ?? []).Contains(bindType.Split('(')[0].Trim(), StringComparer.OrdinalIgnoreCase);

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
        public Task<PreservedDdl> CaptureAsync(ISession target, string owner, string table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<(string Parent, string Ddl, string? Name)>> SourceForeignKeysAsync(ISession source, string sourceOwner, string table,
            IReadOnlySet<string> parents, string targetOwner, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<(string Parent, string Ddl, string? Name)>>([]);

        public Task<HashSet<string>> RecycleBinAsync(ISession target, string table, CancellationToken ct) => throw new NotSupportedException();
    }

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
