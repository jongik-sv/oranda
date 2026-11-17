using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Xunit;
using static Oranda.Core.Tests.Schema.TableEdit.Samples;

namespace Oranda.Core.Tests.Schema.TableEdit;

/// <summary>「저장」 페이지 Core(설계 18.2·시안 ⑦): 저장 속성·LOB 저장 절의 차이·문장·검사.</summary>
public sealed class TableEditStorageTests
{
    private const string T = "ALTER TABLE SAMPLE.ORDERS ";
    private static readonly TableEditCheckOptions Dev = new() { Environment = ConnectionEnvironment.Development };

    private static readonly ServerInfo Oracle23 = new(
        "23.5", null, null, null, null, null, false, 1, ServerCapabilities.From(new HashSet<string>(), new HashSet<string>(), false, false));

    /// <summary>시안 ⑦ 의 테이블: USERS, PCTFREE 10, INITRANS 1, 압축 없음, LOGGING, 병렬 1, MEMO(SECUREFILE)·ATTACH(BASICFILE, 행 밖).</summary>
    private static TableInfo Stored() => Orders() with
    {
        Columns =
        [
            .. Orders().Columns,
            new ColumnInfo("MEMO", 10, "CLOB"),
            new ColumnInfo("ATTACH", 11, "BLOB"),
        ],
        Storage = new TableStorage { PctFree = 10, InitTrans = 1, Compression = TableCompression.None, Logging = true, Parallel = 1 },
        Lobs =
        [
            new LobInfo("MEMO", new LobStorage { Kind = LobStorageKind.SecureFile, Tablespace = "USERS", Compression = LobCompression.None, Deduplicate = false, InRow = true }),
            new LobInfo("ATTACH", new LobStorage { Kind = LobStorageKind.BasicFile, Tablespace = "USERS", InRow = false }),
        ],
    };

    private static TableDraft WithStorage(TableDraft d, Func<TableStorage, TableStorage> change) => d with { Storage = change(d.Storage) };

    private static IReadOnlyList<string> Build23(TableDraft original, TableDraft edited, ITableEditDialect? dialect = null) =>
        TableEditScriptBuilder.Build(original, edited, dialect ?? Dialect, Oracle23).Texts();

    [Fact]
    public void Mockup_seven_gathers_move_changes_into_one_statement()
    {
        var (original, edited) = Start(Stored());
        edited = WithStorage(edited with { Tablespace = "DATA01" }, s => s with { PctFree = 20, Compression = TableCompression.Basic })
            .WithColumn("MEMO", c => c with { Lob = c.Lob! with { Tablespace = "LOB01" } });

        // 시안은 PCTFREE 를 따로 썼지만 MOVE·압축이 PCTFREE 를 기본값으로 되돌리므로(Tibero 실측 12, BASIC 압축 기본 0) MOVE 안에 쓴다.
        Assert.Equal(
            [T + "MOVE ONLINE TABLESPACE DATA01 PCTFREE 20 ROW STORE COMPRESS BASIC LOB (MEMO) STORE AS SECUREFILE (TABLESPACE LOB01) UPDATE INDEXES"],
            Build23(original, edited));

        var changes = TableDraftDiff.Compute(original, edited, Dialect);
        Assert.Equal(StorageChange.Tablespace | StorageChange.PctFree | StorageChange.Compression, changes.Storage);
        Assert.Equal((0, 4), (changes.CountFor(TableEditPage.Columns), changes.CountFor(TableEditPage.Storage)));
        Assert.Equal((TableEditPage.Storage, TableEditEffectKind.Move), (TableEditScriptBuilder.Build(original, edited, Dialect, Oracle23).Statements[0] is var s ? (s.Page, s.Effect.Kind) : default));
    }

    [Fact]
    public void Blank_or_same_values_make_no_statement()
    {
        // 「바꿀 값」만 비교: 비운 칸(null)·지금과 같은 값(대소문자만 다른 테이블스페이스)은 문장이 없다.
        var (original, edited) = Start(Stored());

        Assert.Empty(Build(original, edited with { Storage = new TableStorage(), Tablespace = null }));
        Assert.Empty(Build(original, edited with { Tablespace = "users" }));
        Assert.Empty(Build(original, edited.WithColumn("MEMO", c => c with { Lob = new LobStorage { Tablespace = "users", InRow = true } })));
        Assert.True(TableDraftDiff.Compute(original, edited with { Storage = new TableStorage() }, Dialect).IsEmpty);
    }

    [Fact]
    public void Attributes_without_move_are_one_statement()
    {
        var (original, edited) = Start(Stored());
        edited = WithStorage(edited, s => s with { InitTrans = 4, Logging = false, Parallel = 4 });

        Assert.Equal([T + "INITRANS 4 NOLOGGING PARALLEL 4"], Build(original, edited));
        var parallel = Start(Stored() with { Storage = new TableStorage { Parallel = 4 } });
        Assert.Equal([T + "NOPARALLEL"], Build(parallel.Original, parallel.Edited with { Storage = new TableStorage { Parallel = 1 } }));
        Assert.Equal([T + "PARALLEL"], Build(parallel.Original, parallel.Edited with { Storage = new TableStorage { Parallel = 0 } }));
        Assert.DoesNotContain(TableEditCheckPlanner.Plan(original, edited, Dialect, Dev).Checks, c => c.Kind == TableEditCheckKind.TableRewrite);
    }

    [Fact]
    public void Without_online_and_update_indexes_the_move_rebuilds_the_remaining_indexes()
    {
        // Tibero 실측 12: MOVE 뒤 인덱스 UNUSABLE → REBUILD. 이 편집에서 지우는 인덱스는 빼고, 방식 낱말(BASICFILE)도 쓰지 않는다.
        var dialect = new FakeTableEditDialect();
        dialect.Unsupported[TableEditFeature.OnlineMove] = "tableEdit.unsupported.onlineMove";
        dialect.Unsupported[TableEditFeature.MoveUpdateIndexes] = "tableEdit.unsupported.moveUpdateIndexes";
        dialect.Unsupported[TableEditFeature.SecureFileLob] = "tableEdit.unsupported.secureFile";
        var (original, edited) = Start(Stored(), dialect);
        edited = (edited with { Tablespace = "DATA01" }).WithIndex("IX_ORDERS_02", i => i with { IsDropped = true });

        Assert.Equal(
            [
                "DROP INDEX SAMPLE.IX_ORDERS_02",
                T + "MOVE TABLESPACE DATA01 PCTFREE 10",
                "ALTER INDEX SAMPLE.PK_ORDERS REBUILD",
                "ALTER INDEX SAMPLE.SYS_C0013 REBUILD",
                "ALTER INDEX SAMPLE.IX_ORDERS_01 REBUILD",
            ],
            Build(original, edited, dialect));

        var plan = TableEditCheckPlanner.Plan(original, edited, dialect, Dev);
        Assert.Single(plan.Findings, f => f is { Severity: CheckSeverity.Warning, Code: CheckCode.MoveLocksDml });
        Assert.Single(plan.Checks, c => c is { Kind: TableEditCheckKind.TableRewrite, Code: CheckCode.TableRewrite });

        var lob = edited.WithColumn("ATTACH", c => c with { Lob = c.Lob! with { Tablespace = "LOB02" } }) with { Tablespace = null };
        Assert.Contains(T + "MOVE PCTFREE 10 LOB (ATTACH) STORE AS (TABLESPACE LOB02 DISABLE STORAGE IN ROW)", Build(original, lob, dialect));
    }

    [Fact]
    public void Lob_move_keeps_non_default_current_settings_and_writes_the_kind()
    {
        // MOVE 가 쓰지 않은 LOB 속성은 기본값으로 돌아갈 수 있어(Tibero 실측 13) 기본값이 아닌 지금 값을 같이 쓴다.
        var (original, edited) = Start(Stored());

        Assert.Equal([T + "MOVE ONLINE PCTFREE 10 LOB (ATTACH) STORE AS BASICFILE (TABLESPACE LOB02 DISABLE STORAGE IN ROW) UPDATE INDEXES"],
            Build23(original, edited.WithColumn("ATTACH", c => c with { Lob = c.Lob! with { Tablespace = "LOB02" } })));
        Assert.Equal([T + "MOVE ONLINE PCTFREE 10 LOB (ATTACH) STORE AS SECUREFILE (TABLESPACE USERS COMPRESS MEDIUM DEDUPLICATE DISABLE STORAGE IN ROW) UPDATE INDEXES"],
            Build23(original, edited.WithColumn("ATTACH", c => c with
            {
                Lob = c.Lob! with { Kind = LobStorageKind.SecureFile, Compression = LobCompression.Medium, Deduplicate = true },
            })));
    }

    [Fact]
    public void New_table_storage_clause()
    {
        var table = (TableDraftFactory.Empty("SAMPLE") with
            {
                Name = "T1",
                Tablespace = "USERS",
                Storage = new TableStorage
                {
                    PctFree = 20, InitTrans = 4, InitialExtent = "64k", NextExtent = "1M", Logging = false, Compression = TableCompression.Basic, Parallel = 4,
                },
            })
            .AddColumn(new ColumnDraft("c1", "ID") { DataType = "NUMBER", IsNullable = false })
            .AddColumn(new ColumnDraft("c2", "DOC") { DataType = "CLOB", Lob = new LobStorage { Kind = LobStorageKind.SecureFile, Tablespace = "LOB01", Compression = LobCompression.Medium } });

        Assert.Equal(
            ["CREATE TABLE SAMPLE.T1 (ID NUMBER NOT NULL, DOC CLOB) TABLESPACE USERS PCTFREE 20 INITRANS 4 STORAGE (INITIAL 64K NEXT 1M) NOLOGGING "
             + "ROW STORE COMPRESS BASIC LOB (DOC) STORE AS SECUREFILE (TABLESPACE LOB01 COMPRESS MEDIUM) PARALLEL 4"],
            Build23(TableDraftFactory.Empty("SAMPLE"), table));
        Assert.Equal(["CREATE TABLE SAMPLE.T1 (ID NUMBER NOT NULL, DOC CLOB) TABLESPACE USERS"],
            Build(TableDraftFactory.Empty("SAMPLE"), table with { Storage = new TableStorage(), Columns = [.. table.Columns.Select(c => c with { Lob = null })] }));

        var gtt = table with { Kind = TableKind.Temporary, Tablespace = null };
        Assert.DoesNotContain("PCTFREE", Build(TableDraftFactory.Empty("SAMPLE"), gtt)[0], StringComparison.Ordinal);
        Assert.Single(TableEditCheckPlanner.Plan(TableDraftFactory.Empty("SAMPLE"), gtt, Dialect, Dev).Findings,
            f => f is { Code: CheckCode.SettingIgnored, Detail: "temporaryStorage" });
    }

    [Fact]
    public void New_lob_column_on_an_existing_table_takes_its_storage_clause()
    {
        var (original, edited) = Start(Stored());
        edited = edited.AddColumn(new ColumnDraft("n1", "DOC") { DataType = "CLOB", Lob = new LobStorage { InRow = false } });

        Assert.Equal([T + "ADD (DOC CLOB) LOB (DOC) STORE AS (DISABLE STORAGE IN ROW)"], Build(original, edited));
    }

    [Fact]
    public void Storage_checks_block_bad_values_and_report_ignored_extents()
    {
        var (original, edited) = Start(Stored());
        Findings(original, WithStorage(edited, s => s with { PctFree = 120 }), "pctFreeRange");
        Findings(original, WithStorage(edited, s => s with { InitTrans = 0 }), "initTransRange");
        Findings(original, edited.WithColumn("ATTACH", c => c with { Lob = c.Lob! with { Compression = LobCompression.Low } }), "basicFileLobOption");

        var extents = WithStorage(edited, s => s with { InitialExtent = "1M" });
        Assert.Empty(Build(original, extents));
        Assert.Single(TableEditCheckPlanner.Plan(original, extents, Dialect, Dev).Findings,
            f => f is { Severity: CheckSeverity.Warning, Code: CheckCode.SettingIgnored, Detail: "existingStorage" });

        var partitioned = Start(Stored() with { IsPartitioned = true });
        Findings(partitioned.Original, partitioned.Edited with { Tablespace = "DATA01" }, "partitionedMove");

        var bad = (TableDraftFactory.Empty("SAMPLE") with { Name = "T1", Storage = new TableStorage { InitialExtent = "64 kb" } })
            .AddColumn(new ColumnDraft("c1", "ID") { DataType = "NUMBER" });
        Findings(TableDraftFactory.Empty("SAMPLE"), bad, "extentSize");

        var dialect = new FakeTableEditDialect();
        dialect.Unsupported[TableEditFeature.AdvancedRowCompression] = "tableEdit.unsupported.advancedCompression";
        Assert.Contains(TableEditCheckPlanner.Plan(original, WithStorage(edited, s => s with { Compression = TableCompression.Advanced }), dialect, Dev).Findings,
            f => f is { Code: CheckCode.Unsupported, Detail: "tableEdit.unsupported.advancedCompression" });

        var gtt = Start(Stored() with { Kind = TableKind.Temporary });
        Assert.Contains(TableEditCheckPlanner.Plan(gtt.Original, WithStorage(gtt.Edited, s => s with { PctFree = 20 }), Dialect, Dev).Findings,
            f => f is { Code: CheckCode.Unsupported, Detail: TableEditCheckPlanner.SpecialTableReasonKey });
    }

    // ---------- 리뷰 반영 ----------

    [Fact]
    public void Core_gate_turns_off_online_update_indexes_and_kind_words_when_unsure()
    {
        // 방언이 새 기능 값을 몰라 「된다」로 답해도 서버를 모르거나 11g·Tibero 면 ONLINE·UPDATE INDEXES·방식 낱말을 쓰지 않는다.
        var (original, edited) = Start(Stored());
        edited = (edited with { Tablespace = "data01" }).WithColumn("MEMO", c => c with { Lob = c.Lob! with { InRow = false } });
        var oracle11 = Oracle23 with { Version = "11.2.0.4" };
        var tibero = Oracle23 with { Version = "6.0", Dbms = Dbms.Tibero };

        string Move(ServerInfo? server) => TableEditScriptBuilder.Build(original, edited, Dialect, server).Texts().Single(t => t.Contains("MOVE", StringComparison.Ordinal));
        foreach (var server in (ServerInfo?[])[null, tibero])
        {
            Assert.Equal(T + "MOVE TABLESPACE DATA01 PCTFREE 10 LOB (MEMO) STORE AS (TABLESPACE USERS DISABLE STORAGE IN ROW)", Move(server));
        }

        // 11g: SECUREFILE 은 되지만 ONLINE·UPDATE INDEXES 는 안 된다.
        Assert.Equal(T + "MOVE TABLESPACE DATA01 PCTFREE 10 LOB (MEMO) STORE AS SECUREFILE (TABLESPACE USERS DISABLE STORAGE IN ROW)", Move(oracle11));

        Assert.Equal("COMPRESS", TableEditDefaults.CompressionClause(TableCompression.Basic, tibero));
        Assert.Equal("tableEdit.unsupported.advancedRowCompression", TableEditDefaults.StorageReasonKey(TableEditFeature.AdvancedRowCompression, tibero));
        Assert.Null(TableEditDefaults.StorageReasonKey(TableEditFeature.OnlineMove, Oracle23));
        Assert.NotNull(TableEditDefaults.StorageReasonKey(TableEditFeature.OnlineMove, Oracle23 with { Edition = "Standard" }));

        // 11g XE 실측: 기본 압축도 ORA-00439(Enterprise 만). Tibero 는 됨.
        Assert.Equal("tableEdit.unsupported.basicTableCompression",
            TableEditDefaults.StorageReasonKey(TableEditFeature.BasicTableCompression, oracle11 with { Edition = "Express" }));
        Assert.Null(TableEditDefaults.StorageReasonKey(TableEditFeature.BasicTableCompression, tibero));
        var (o, e) = Start(Stored());
        Assert.Contains(TableEditCheckPlanner.Plan(o, WithStorage(e, s => s with { Compression = TableCompression.Basic }), Dialect,
                Dev with { Server = oracle11 with { Edition = "Express" } }).Findings,
            f => f is { Code: CheckCode.Unsupported, Detail: "tableEdit.unsupported.basicTableCompression" });
    }

    [Fact]
    public void Switching_to_basicfile_does_not_carry_securefile_options()
    {
        var table = Stored() with
        {
            Lobs = [new LobInfo("MEMO", new LobStorage { Kind = LobStorageKind.SecureFile, Tablespace = "USERS", Compression = LobCompression.Medium, Deduplicate = true })],
        };
        var (original, edited) = Start(table);
        edited = edited.WithColumn("MEMO", c => c with { Lob = c.Lob! with { Kind = LobStorageKind.BasicFile } });

        Assert.Equal([T + "MOVE ONLINE PCTFREE 10 LOB (MEMO) STORE AS BASICFILE (TABLESPACE USERS) UPDATE INDEXES"], Build23(original, edited));
        Assert.DoesNotContain(TableEditCheckPlanner.Plan(original, edited, Dialect, Dev with { Server = Oracle23 }).Findings, f => f.Severity == CheckSeverity.Block);
    }

    [Fact]
    public void Rebuilds_left_after_a_failure_are_carried_by_the_rebase()
    {
        // MOVE 성공 + 첫 REBUILD 뒤 실패 → 다시 읽은 원본은 이미 DATA01 이라 MOVE 가 없지만 남은 REBUILD 는 나온다.
        var (original, edited) = Start(Stored());
        edited = edited with { Tablespace = "DATA01" };
        var script = TableEditScriptBuilder.Build(original, edited, Dialect);
        Assert.Equal(["MOVE", "REBUILD", "REBUILD", "REBUILD", "REBUILD"], script.Texts().Select(t => t.Split(' ')[^1] == "REBUILD" ? "REBUILD" : "MOVE"));

        var reread = TableDraftFactory.FromTable(Stored() with { Tablespace = "DATA01" }, Dialect);
        var rebased = TableDraftRebase.Rebase(original, edited, script, 2, reread);

        Assert.Equal(["SYS_C0013", "IX_ORDERS_01", "IX_ORDERS_02"], rebased.RebuildIndexes);
        Assert.Equal(
            ["ALTER INDEX SAMPLE.SYS_C0013 REBUILD", "ALTER INDEX SAMPLE.IX_ORDERS_01 REBUILD", "ALTER INDEX SAMPLE.IX_ORDERS_02 REBUILD"],
            Build(reread, rebased));
        Assert.Equal(1, TableDraftDiff.Compute(reread, rebased, Dialect).CountFor(TableEditPage.Storage));
        Assert.Empty(TableDraftRebase.Rebase(original, edited, script, 0, original).RebuildIndexes);
    }

    [Fact]
    public void Function_index_on_a_dropped_column_is_not_rebuilt()
    {
        // IX_ORDERS_02 = UPPER("NOTE"): NOTE 를 지우면 서버가 인덱스도 지운다 — REBUILD 하면 ORA-01418.
        var (original, edited) = Start(Stored());
        edited = (edited with { Tablespace = "DATA01" }).WithColumn("NOTE", c => c with { IsDropped = true });

        var texts = Build(original, edited);

        Assert.DoesNotContain("ALTER INDEX SAMPLE.IX_ORDERS_02 REBUILD", texts);
        Assert.Contains("ALTER INDEX SAMPLE.IX_ORDERS_01 REBUILD", texts);
    }

    [Fact]
    public void Move_checks_long_columns_added_lobs_and_tablespace_case()
    {
        var withLong = Stored() with { Columns = [.. Stored().Columns, new ColumnInfo("OLD_TEXT", 12, "LONG")] };
        var l = Start(withLong);
        Findings(l.Original, l.Edited with { Tablespace = "DATA01" }, "longColumnMove");

        var (original, edited) = Start(Stored());
        Findings(original, edited.AddColumn(new ColumnDraft("n1", "X") { DataType = "VARCHAR2", Length = 10, Lob = new LobStorage { InRow = false } }), "lobOnNonLob");
        Assert.Equal([T + "ADD (X VARCHAR2(10))"],
            Build(original, edited.AddColumn(new ColumnDraft("n1", "X") { DataType = "VARCHAR2", Length = 10, Lob = new LobStorage { InRow = false } })));
        Findings(original, edited.AddColumn(new ColumnDraft("n1", "DOC") { DataType = "BLOB", Lob = new LobStorage { Kind = LobStorageKind.BasicFile, Deduplicate = true } }),
            "basicFileLobOption");

        var gtt = Start(Stored() with { Kind = TableKind.Temporary });
        var gttAdd = gtt.Edited.AddColumn(new ColumnDraft("n1", "DOC") { DataType = "CLOB", Lob = new LobStorage { Tablespace = "LOB01" } });
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS ADD (DOC CLOB)"], Build(gtt.Original, gttAdd));
        Assert.Single(TableEditCheckPlanner.Plan(gtt.Original, gttAdd, Dialect, Dev).Findings, f => f is { Code: CheckCode.SettingIgnored, Detail: "temporaryStorage" });

        Assert.Contains("TABLESPACE \"data01\"", Build(original, edited with { Tablespace = "\"data01\"" })[0], StringComparison.Ordinal);
    }

    private static void Findings(TableDraft original, TableDraft edited, string reason) =>
        Assert.Single(TableEditCheckPlanner.Plan(original, edited, Dialect, Dev).Findings,
            f => f is { Severity: CheckSeverity.Block, Code: CheckCode.InvalidDefinition } && f.Detail == reason);

    [Fact]
    public void Factory_reads_storage_and_lobs_but_copy_structure_does_not()
    {
        var draft = TableDraftFactory.FromTable(Stored(), Dialect);

        Assert.Equal(10, draft.Storage.PctFree);
        Assert.Equal(LobStorageKind.BasicFile, draft.Columns.Single(c => c.Name == "ATTACH").Lob!.Kind);
        Assert.Null(draft.Columns.Single(c => c.Name == "NOTE").Lob);

        var copy = TableDraftFactory.CopyStructure(Stored(), "SAMPLE", "ORDERS_BAK", Dialect, 30);
        Assert.Equal(new TableStorage(), copy.Storage);
        Assert.All(copy.Columns, c => Assert.Null(c.Lob));
        Assert.Equal(TableCompression.None, TableDraftFactory.FromTable(Orders(), Dialect).Storage.Compression ?? TableCompression.None);
    }

    [Fact]
    public void Compression_clause_follows_the_server_version()
    {
        Assert.Equal("COMPRESS BASIC", TableEditDefaults.CompressionClause(TableCompression.Basic, null));
        Assert.Equal("COMPRESS FOR OLTP", TableEditDefaults.CompressionClause(TableCompression.Advanced, Oracle23 with { Version = "11.2.0.4" }));
        Assert.Equal("ROW STORE COMPRESS ADVANCED", TableEditDefaults.CompressionClause(TableCompression.Advanced, Oracle23));
        Assert.Equal("NOCOMPRESS", TableEditDefaults.CompressionClause(TableCompression.None, Oracle23));
    }
}
