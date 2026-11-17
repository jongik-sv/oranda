using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.TableEdit;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 테이블 편집 화면 시험의 가짜들(Oracle·Core 몸 없음). 차이·문장은 <see cref="FakeTableEditEngine"/> 가 칸을 그대로 비교해 만든다 —
/// 화면이 엔진 결과를 옮기는지만 보고, 문장 꼴·정규화는 Core 시험(te-core)이 본다.
/// </summary>
internal static class TableEditTestSupport
{
    public static readonly DbObjectRef Orders = new("SAMPLE", "ORDERS", "TABLE");

    /// <summary>예시 테이블 SAMPLE.ORDERS: 열 4개(가상 1), PK·FK·CHECK, 일반·PK 인덱스.</summary>
    public static TableInfo OrdersTable(TableKind kind = TableKind.Heap) => new(Orders,
    [
        new ColumnInfo("ORDER_ID", 1, "NUMBER") { Precision = 10, IsNullable = false, Comment = "주문 번호" },
        new ColumnInfo("CUSTOMER_ID", 2, "NUMBER") { Precision = 8, IsNullable = false },
        new ColumnInfo("STATUS", 3, "VARCHAR2") { Length = 50, CharSemantics = CharSemantics.Bytes, Comment = "상태" },
        new ColumnInfo("OLD_FLAG", 4, "CHAR") { Length = 1, CharSemantics = CharSemantics.Bytes },
    ])
    {
        Comment = "주문",
        Kind = kind,
        Constraints =
        [
            new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ORDER_ID"]),
            new ConstraintInfo("SYS_C0012345", ConstraintKind.Check, ["STATUS"]) { Condition = "STATUS IN ('A','B')" },
        ],
        Indexes =
        [
            new IndexInfo("PK_ORDERS", true, ["ORDER_ID"]) { ConstraintName = "PK_ORDERS", IndexType = "NORMAL" },
            new IndexInfo("IX_ORDERS_01", false, ["CUSTOMER_ID"]) { IndexType = "NORMAL" },
        ],
    };

    public static Oranda.Core.Metadata.ObjectDetail OrdersDetail() => new(Orders,
    [
        new DetailSection("일반", ["항목", "값"], [["상태", "VALID"]]),
        new DetailSection("설명", ["항목", "값"], [["테이블 주석", "주문"]]),
        new DetailSection("열", ["이름", "형식"], [["ORDER_ID", "NUMBER(10)"]]),
        new DetailSection("제약", ["이름", "종류"], [["PK_ORDERS", "P"]]),
        new DetailSection("인덱스", ["이름", "유일"], [["PK_ORDERS", "Y"]]),
        new DetailSection("통계", ["항목", "값"], []),
    ]);

    /// <summary>편집기가 붙은 객체 상세(가짜 메타데이터·방언·엔진, 확인 답은 <see cref="Probe.Answer"/>).</summary>
    public static (ObjectDetailViewModel Vm, FakeMeta Meta, Probe Probe) CreateDetail(TableKind kind = TableKind.Heap, bool dialect = true)
    {
        var meta = new FakeMeta { Table = OrdersTable(kind) };
        var probe = new Probe();
        var vm = new ObjectDetailViewModel(meta)
        {
            TableEditEngine = new FakeTableEditEngine(),
            TableEditDialect = dialect ? new FakeDialect() : null,
            InvalidateTableCache = (_, table, _) =>
            {
                probe.Calls.Add("invalidate " + table.Name);
                return Task.CompletedTask;
            },
            Confirm = (message, _) =>
            {
                probe.Asked.Add(message);
                return Task.FromResult(probe.Answer);
            },
        };
        meta.Calls = probe.Calls;
        return (vm, meta, probe);
    }

    /// <summary>편집 세션 하나(객체 상세 없이).</summary>
    public static TableEditSession CreateSession(TableKind kind = TableKind.Heap, int maxBytes = 128) =>
        new(new FakeTableEditEngine().FromTable(OrdersTable(kind), new FakeDialect()), new FakeDialect(),
            new TableEditContext(maxBytes, ConnectionEnvironment.Development), new FakeTableEditEngine());

    internal sealed class Probe
    {
        public List<string> Calls { get; } = [];

        public List<string> Asked { get; } = [];

        public bool Answer { get; set; } = true;
    }

    internal sealed class FakeSession(ConnectionEnvironment environment = ConnectionEnvironment.Development) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        private ConnectionProfile? _profile;

        /// <summary>읽기 전용 접속.</summary>
        public bool ReadOnly { get; init; }

        /// <summary>커밋하지 않은 변경이 있다.</summary>
        public bool Pending { get; init; }

        public ConnectionProfile Profile => _profile ??= new("dev-example", "SAMPLE", "h:1521/s", Id: Guid.NewGuid(), Environment: environment, ReadOnly: ReadOnly);

        public ServerInfo Server { get; } = new("19.0.0.0.0", null, null, null, null, null, false, 1, new ServerCapabilities(false, false, false, false, false));

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "ORCL/PDB1/SAMPLE";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => Pending;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class FakeMeta : IMetadataProvider
    {
        public TableInfo Table { get; set; } = OrdersTable();

        public List<string> Calls { get; set; } = [];

        public int DetailReads { get; private set; }

        /// <summary>참이면 테이블 읽기가 실패한다.</summary>
        public bool FailReads { get; set; }

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct)
        {
            Calls.Add("read " + table.Name);
            if (FailReads)
            {
                throw new InvalidOperationException("ORA-03113: end-of-file on communication channel");
            }

            return Task.FromResult(Table);
        }

        /// <summary>돌려줄 상세(null = <see cref="OrdersDetail"/>).</summary>
        public Oranda.Core.Metadata.ObjectDetail? Detail { get; set; }

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        {
            DetailReads++;
            return Task.FromResult(Detail ?? OrdersDetail());
        }

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => Task.FromResult("CREATE TABLE SAMPLE.ORDERS (…)");

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Task.FromResult<IReadOnlyList<CompileError>>([]);

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }

    internal sealed class FakeDialect : ITableEditDialect
    {
        public Dbms Dbms => Dbms.Oracle;

        public IReadOnlyList<string> TypeNames { get; } = ["VARCHAR2", "CHAR", "NUMBER", "DATE", "CLOB"];

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => null;

        public bool IsServerName(string name) => name.StartsWith("SYS_", StringComparison.Ordinal);

        public bool IsReservedWord(string upperName) => false;

        public string NormalizeType(ColumnDraft column) => column.DataType ?? string.Empty;

        public string? NormalizeDefault(string? serverText) => string.IsNullOrWhiteSpace(serverText) ? null : serverText.Trim();

        public string? NormalizeExpression(string? serverText) => serverText?.Trim();

        public string? NormalizeCondition(string? serverText) => serverText?.Trim();
    }
}

/// <summary>
/// 칸을 그대로 비교하는 가짜 엔진. 문장 글 = <c>ALTER TABLE 소유자.테이블 동사 이름</c>(Core 빌더 꼴이 아님). 줄 키·페이지·순서(열 → 제약 → 인덱스 → 주석)만 맞춘다.
/// 검사는 <see cref="Findings"/> 를 그대로 돌려주고 받은 계획을 <see cref="LastPlan"/> 에 남긴다.
/// </summary>
internal sealed class FakeTableEditEngine : ITableEditEngine
{
    public IReadOnlyList<CheckFinding> Findings { get; set; } = [];

    public TableEditCheckPlan? LastPlan { get; private set; }

    public int CheckRuns { get; private set; }

    public TableDraft FromTable(TableInfo table, ITableEditDialect dialect)
    {
        var columns = table.Columns.Select((c, i) => new ColumnDraft("c" + (i + 1), c.Name)
        {
            OriginalPosition = c.Position,
            DataType = c.DataType,
            Length = c.Length,
            CharSemantics = c.CharSemantics,
            Precision = c.Precision,
            Scale = c.Scale,
            IsNullable = c.IsNullable,
            DefaultValue = c.IsVirtual ? null : c.DefaultValue,
            VirtualExpression = c.IsVirtual ? c.DefaultValue : null,
            Comment = c.Comment,
        }).ToList();
        string KeyOf(string name) => columns.First(c => c.Name == name).RowKey;
        return new TableDraft(table.Table.Owner, table.Table.Name)
        {
            Comment = table.Comment,
            Kind = table.Kind,
            Columns = columns,
            Constraints = [.. table.Constraints.Select((k, i) => new ConstraintDraft("k" + (i + 1), k.Name, k.Kind)
            {
                ColumnKeys = [.. k.Columns.Select(KeyOf)],
                ReferencedTable = k.ReferencedTable,
                ReferencedColumns = k.ReferencedColumns,
                DeleteRule = k.DeleteRule,
                Condition = k.Condition,
                IsEnabled = k.IsEnabled,
            })],
            Indexes = [.. table.Indexes.Select((x, i) => new IndexDraft("i" + (i + 1), x.Name)
            {
                IsUnique = x.IsUnique,
                Columns = [.. x.Columns.Select((c, j) => new IndexColumnDraft(KeyOf(c), j < x.Descending.Count && x.Descending[j]))],
                ConstraintName = x.ConstraintName,
                Tablespace = x.Tablespace,
                Kind = x.IndexType is null or "NORMAL" ? IndexKind.Normal : IndexKind.Other,
            })],
        };
    }

    public TableDraft Empty(string owner) => new(owner, string.Empty) { IsNewTable = true };

    public TableDraftChanges Diff(TableDraft original, TableDraft edited, ITableEditDialect dialect)
    {
        var columns = new List<ColumnDiff>();
        foreach (var e in edited.Columns)
        {
            var o = original.Column(e.RowKey);
            if (o is null)
            {
                if (!e.IsDropped)
                {
                    columns.Add(new ColumnDiff(e.RowKey, DraftRowState.Added, ColumnChange.None, null, e));
                }

                continue;
            }

            if (e.IsDropped)
            {
                columns.Add(new ColumnDiff(e.RowKey, DraftRowState.Dropped, ColumnChange.None, o, e));
                continue;
            }

            var ch = ColumnChange.None;
            ch |= o.Name != e.Name ? ColumnChange.Name : 0;
            ch |= o.DataType != e.DataType || o.Length != e.Length || o.Precision != e.Precision || o.Scale != e.Scale ? ColumnChange.Type : 0;
            ch |= o.IsNullable != e.IsNullable ? ColumnChange.Nullable : 0;
            ch |= dialect.NormalizeDefault(o.DefaultValue) != dialect.NormalizeDefault(e.DefaultValue) ? ColumnChange.Default : 0;
            ch |= o.VirtualExpression != e.VirtualExpression ? ColumnChange.VirtualExpression : 0;
            ch |= (o.Comment ?? string.Empty) != (e.Comment ?? string.Empty) ? ColumnChange.Comment : 0;
            if (ch != ColumnChange.None)
            {
                columns.Add(new ColumnDiff(e.RowKey, DraftRowState.Modified, ch, o, e));
            }
        }

        var constraints = new List<ConstraintDiff>();
        foreach (var e in edited.Constraints)
        {
            var o = original.Constraint(e.RowKey);
            if (o is null)
            {
                if (!e.IsDropped)
                {
                    constraints.Add(new ConstraintDiff(e.RowKey, DraftRowState.Added, ConstraintChange.None, null, e));
                }

                continue;
            }

            if (e.IsDropped)
            {
                constraints.Add(new ConstraintDiff(e.RowKey, DraftRowState.Dropped, ConstraintChange.None, o, e));
                continue;
            }

            var ch = ConstraintChange.None;
            ch |= o.Name != e.Name ? ConstraintChange.Name : 0;
            ch |= !o.ColumnKeys.SequenceEqual(e.ColumnKeys) || o.Condition != e.Condition || o.ReferencedTable != e.ReferencedTable
                  || !o.ReferencedColumns.SequenceEqual(e.ReferencedColumns) || o.DeleteRule != e.DeleteRule ? ConstraintChange.Definition : 0;
            ch |= o.IsEnabled != e.IsEnabled ? ConstraintChange.Enabled : 0;
            if (ch != ConstraintChange.None)
            {
                constraints.Add(new ConstraintDiff(e.RowKey, DraftRowState.Modified, ch, o, e));
            }
        }

        var indexes = new List<IndexDiff>();
        foreach (var e in edited.Indexes)
        {
            var o = original.Index(e.RowKey);
            if (o is null)
            {
                if (!e.IsDropped)
                {
                    indexes.Add(new IndexDiff(e.RowKey, DraftRowState.Added, IndexChange.None, null, e));
                }

                continue;
            }

            if (e.IsDropped)
            {
                indexes.Add(new IndexDiff(e.RowKey, DraftRowState.Dropped, IndexChange.None, o, e));
                continue;
            }

            var ch = IndexChange.None;
            ch |= o.Name != e.Name ? IndexChange.Name : 0;
            ch |= o.IsUnique != e.IsUnique || !o.Columns.SequenceEqual(e.Columns) || o.Tablespace != e.Tablespace ? IndexChange.Definition : 0;
            if (ch != IndexChange.None)
            {
                indexes.Add(new IndexDiff(e.RowKey, DraftRowState.Modified, ch, o, e));
            }
        }

        return new TableDraftChanges
        {
            Columns = columns,
            Constraints = constraints,
            Indexes = indexes,
            CommentChanged = (original.Comment ?? string.Empty) != (edited.Comment ?? string.Empty),
        };
    }

    public TableEditScript Build(TableDraft original, TableDraft edited, ITableEditDialect dialect)
    {
        var changes = Diff(original, edited, dialect);
        var t = $"{edited.Owner}.{edited.Name}";
        var list = new List<TableEditStatement>();

        void Add(string text, string key, TableEditPage page, TableEditEffect effect, TableEditMarks marks = TableEditMarks.None) =>
            list.Add(new TableEditStatement(new SqlStatement(text, new TextRange(0, text.Length), StatementKind.Ddl) { Verb = text.Split(' ')[0] }, key, page, marks)
            {
                Effect = effect,
            });

        // 효과(지움·이름 바꿈·만듦)는 Core 중간 실패 다시 계산(TableDraftRebase)이 쓴다.
        static TableEditEffect EffectOf(DraftRowState state, string name, bool renamed) => state switch
        {
            DraftRowState.Added => new TableEditEffect(TableEditEffectKind.Create, name),
            DraftRowState.Dropped => new TableEditEffect(TableEditEffectKind.Drop),
            _ when renamed => new TableEditEffect(TableEditEffectKind.Rename, name),
            _ => TableEditEffect.None,
        };

        foreach (var c in changes.Columns)
        {
            var verb = c.State switch { DraftRowState.Added => "ADD", DraftRowState.Dropped => "DROP COLUMN", _ => "MODIFY" };
            var name = (c.Edited ?? c.Original)!.Name;
            Add($"ALTER TABLE {t} {verb} {name}", c.RowKey, TableEditPage.Columns, EffectOf(c.State, name, c.Changes.HasFlag(ColumnChange.Name)),
                c.State == DraftRowState.Dropped ? TableEditMarks.Irreversible : TableEditMarks.None);
        }

        foreach (var c in changes.Constraints)
        {
            var verb = c.State switch { DraftRowState.Added => "ADD CONSTRAINT", DraftRowState.Dropped => "DROP CONSTRAINT", _ => "MODIFY CONSTRAINT" };
            var name = (c.Edited ?? c.Original)!.Name;
            Add($"ALTER TABLE {t} {verb} {name}", c.RowKey, TableEditPage.Constraints, EffectOf(c.State, name, c.Changes.HasFlag(ConstraintChange.Name)));
        }

        foreach (var i in changes.Indexes)
        {
            var verb = i.State switch { DraftRowState.Added => "CREATE INDEX", DraftRowState.Dropped => "DROP INDEX", _ => "ALTER INDEX" };
            var name = (i.Edited ?? i.Original)!.Name;
            Add($"{verb} {edited.Owner}.{name}", i.RowKey, TableEditPage.Indexes, EffectOf(i.State, name, i.Changes.HasFlag(IndexChange.Name)));
        }

        if (changes.CommentChanged)
        {
            Add($"COMMENT ON TABLE {t} IS '{edited.Comment}'", TableDraft.TableRowKey, TableEditPage.Description, TableEditEffect.None);
        }

        return list.Count == 0 ? TableEditScript.Empty : new TableEditScript(list);
    }

    public TableEditCheckPlan Plan(TableDraft original, TableDraft edited, ITableEditDialect dialect, TableEditCheckOptions options) =>
        LastPlan = new TableEditCheckPlan([], [new TableEditCheck(TableEditCheckKind.Dependents, CheckCode.Passed, TableDraft.TableRowKey, null)]);

    public Task<IReadOnlyList<CheckFinding>> RunChecksAsync(
        TableEditCheckPlan plan, DbObjectRef table, ITableEditChecks checks, ISession session, TableEditCheckOptions options, CancellationToken ct)
    {
        CheckRuns++;
        return Task.FromResult(Findings);
    }
}
