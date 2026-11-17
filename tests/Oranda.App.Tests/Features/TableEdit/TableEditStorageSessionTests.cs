using Oranda.App.Features.TableEdit;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using static Oranda.App.Tests.Features.TableEdit.TableEditPartitionSessionTests;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 「저장」 페이지 편집(설계 18.2, 시안 ⑦)을 실제 Core 엔진으로: 안 고치면 0문장, 비운 칸·지금과 같은 값은 그대로, PCTFREE = 새 블록부터 한 문장,
/// 테이블스페이스·압축·LOB = MOVE, 방언이 못 하는 고르기는 모델을 건드리지 않고 이유, 틀린 숫자는 오류, 새 테이블은 STORAGE 까지 CREATE 저장 절로.
/// </summary>
public class TableEditStorageSessionTests
{
    /// <summary>SAMPLE.ORDERS + MEMO(CLOB, SECUREFILE · LOB01), 저장 속성 PCTFREE 10 · INITRANS 1 · 압축 없음 · LOGGING · 병렬 1, 테이블스페이스 USERS.</summary>
    internal static TableInfo StoredOrders(TableKind kind = TableKind.Heap)
    {
        var table = OrdersTable(kind);
        return table with
        {
            Columns = [.. table.Columns, new ColumnInfo("MEMO", table.Columns.Count + 1, "CLOB")],
            Tablespace = "USERS",
            Storage = new TableStorage { PctFree = 10, InitTrans = 1, Compression = TableCompression.None, Logging = true, Parallel = 1 },
            Lobs =
            [
                new LobInfo("MEMO", new LobStorage
                {
                    Kind = LobStorageKind.SecureFile, Tablespace = "LOB01", Compression = LobCompression.None, Deduplicate = false, InRow = true,
                }),
            ],
        };
    }

    internal static TableEditSession CreateStorage(TableInfo? table = null, ITableEditDialect? dialect = null, ServerInfo? server = null)
    {
        var d = dialect ?? new OracleRules();
        var engine = CoreTableEditEngine.Instance;
        return new TableEditSession(engine.FromTable(table ?? StoredOrders(), d), d, new TableEditContext(128, ConnectionEnvironment.Development, server ?? Oracle19), engine);
    }

    internal static StorageRowViewModel Row(TableEditSession s, StorageProperty attribute) => s.StorageRows.Single(r => r.Attribute == attribute);

    private static List<string> Texts(TableEditSession s) => [.. s.Script.Statements.Select(x => x.Statement.Text)];

    [Fact]
    public void Untouched_page_shows_current_values_and_passing_through_every_cell_makes_no_statement()
    {
        var s = CreateStorage();

        Assert.Equal(
            [StorageProperty.Tablespace, StorageProperty.PctFree, StorageProperty.InitTrans, StorageProperty.Compression, StorageProperty.Logging, StorageProperty.Parallel],
            s.StorageRows.Select(r => r.Attribute));
        Assert.Equal(["USERS", "10", "1", "없음", "예", "1"], s.StorageRows.Select(r => r.CurrentText));
        Assert.All(s.StorageRows, r => Assert.True(r.CanEdit));
        Assert.All(s.StorageRows.Where(r => r.IsText), r => Assert.Equal(string.Empty, r.Text));
        Assert.All(s.StorageRows.Where(r => r.IsChoice), r => Assert.Equal(0, r.ChoiceIndex));
        var lob = Assert.Single(s.LobRows);
        Assert.Equal("MEMO", lob.ColumnName);
        Assert.True(s.HasLobRows);

        foreach (var r in s.StorageRows)
        {
            if (r.IsText)
            {
                r.Text = r.Text;
            }
            else
            {
                r.ChoiceIndex = r.ChoiceIndex;
            }
        }

        lob.Tablespace = lob.Tablespace;
        lob.KindIndex = lob.KindIndex;
        lob.CompressionIndex = lob.CompressionIndex;
        lob.DeduplicateIndex = lob.DeduplicateIndex;
        lob.InRowIndex = lob.InRowIndex;

        Assert.True(s.Script.IsEmpty);
        Assert.Null(s.ScriptError);
        Assert.False(s.IsDirty);
        Assert.Equal(s.Original, s.Draft);
        Assert.False(s.HasStorageMove);
        Assert.Equal(0, s.CountFor(TableEditPage.Storage));
    }

    [Fact]
    public void Unknown_current_values_show_as_unknown()
    {
        var s = CreateStorage(StoredOrders() with { Storage = new TableStorage { PctFree = 10 }, Tablespace = null });

        Assert.Equal("알 수 없음", Row(s, StorageProperty.Tablespace).CurrentText);
        Assert.Equal("10", Row(s, StorageProperty.PctFree).CurrentText);
        Assert.Equal("알 수 없음", Row(s, StorageProperty.Compression).CurrentText);
        Assert.Equal("알 수 없음", Row(s, StorageProperty.Logging).CurrentText);
    }

    [Fact]
    public void Pctfree_20_is_one_alter_without_move_and_works_from_new_blocks()
    {
        var s = CreateStorage();
        var row = Row(s, StorageProperty.PctFree);

        row.Text = "20";

        var text = Assert.Single(Texts(s));
        Assert.StartsWith("ALTER TABLE SAMPLE.ORDERS", text, StringComparison.Ordinal);
        Assert.Contains("PCTFREE 20", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MOVE", text, StringComparison.Ordinal);
        Assert.Equal("20", row.Text);
        Assert.True(row.IsChanged);
        Assert.Equal("새 블록부터", row.EffectText);
        Assert.False(s.HasStorageMove);
        Assert.Equal(1, s.CountFor(TableEditPage.Storage));

        row.Text = string.Empty; // 비우면 그대로
        Assert.True(s.Script.IsEmpty);
        Assert.False(row.IsChanged);
        Assert.Equal(s.Original, s.Draft);
    }

    [Fact]
    public void Tablespace_change_is_a_move_and_the_hint_says_the_table_is_rewritten()
    {
        var s = CreateStorage();
        var row = Row(s, StorageProperty.Tablespace);

        row.Text = "data01";

        var move = Assert.Single(Texts(s), t => t.Contains("MOVE", StringComparison.Ordinal));
        Assert.Contains("TABLESPACE DATA01", move, StringComparison.Ordinal);
        Assert.Equal("MOVE 필요", row.EffectText);
        Assert.True(row.IsWarning);
        Assert.True(row.IsChanged);
        Assert.True(s.HasStorageMove);
        Assert.Contains("테이블 전체를 다시 씁니다", s.StorageMoveText, StringComparison.Ordinal);

        // MOVE 와 함께 바꾼 PCTFREE 는 새 블록부터가 아니다.
        Row(s, StorageProperty.PctFree).Text = "20";
        Assert.NotEqual("새 블록부터", Row(s, StorageProperty.PctFree).EffectText);
    }

    [Fact]
    public void Move_without_online_warns_that_dml_is_locked()
    {
        var s = CreateStorage(dialect: new OracleRules { Unsupported = f => f == TableEditFeature.OnlineMove ? "TableEdit.unsupported.oracle122" : null });

        Row(s, StorageProperty.Tablespace).Text = "DATA01";

        Assert.DoesNotContain(Texts(s), t => t.Contains("MOVE ONLINE", StringComparison.Ordinal));
        Assert.Contains("DML", s.StorageMoveText, StringComparison.Ordinal);
    }

    [Fact]
    public void Typing_the_current_value_leaves_no_statement_and_the_cell_empty()
    {
        var s = CreateStorage();
        var tablespace = Row(s, StorageProperty.Tablespace);
        var pctFree = Row(s, StorageProperty.PctFree);
        var logging = Row(s, StorageProperty.Logging);

        tablespace.Text = "users";
        pctFree.Text = " 10 ";
        logging.ChoiceIndex = 1; // 예 = 지금 값

        Assert.True(s.Script.IsEmpty);
        Assert.Equal(string.Empty, tablespace.Text);
        Assert.Equal(string.Empty, pctFree.Text);
        Assert.Equal(0, logging.ChoiceIndex);
        Assert.Equal(s.Original, s.Draft);
    }

    [Fact]
    public void Logging_and_parallel_are_one_alter()
    {
        var s = CreateStorage();

        Row(s, StorageProperty.Logging).ChoiceIndex = 2; // 아니오
        Row(s, StorageProperty.Parallel).Text = "4";

        var text = Assert.Single(Texts(s));
        Assert.Contains("NOLOGGING", text, StringComparison.Ordinal);
        Assert.Contains("PARALLEL 4", text, StringComparison.Ordinal);
        Assert.Equal(2, s.CountFor(TableEditPage.Storage));
    }

    [Fact]
    public void Compression_the_dialect_refuses_does_not_change_the_model_and_shows_the_reason()
    {
        var s = CreateStorage(dialect: new OracleRules
        {
            Unsupported = f => f == TableEditFeature.AdvancedRowCompression ? "TableEdit.unsupported.oracleEnterprise" : null,
        });
        var row = Row(s, StorageProperty.Compression);

        row.ChoiceIndex = 3; // ADVANCED

        Assert.True(s.Script.IsEmpty);
        Assert.Equal(TableCompression.None, s.Draft.Storage.Compression);
        Assert.Equal(0, row.ChoiceIndex);
        Assert.NotNull(row.Error);
        Assert.True(row.HasError);
        Assert.Equal(row.Error, row.Tip);

        // 되는 압축(BASIC)은 MOVE 로 들어가고 오류는 지워진다.
        row.ChoiceIndex = 2;
        Assert.Null(row.Error);
        var move = Assert.Single(Texts(s), t => t.Contains("MOVE", StringComparison.Ordinal));
        Assert.Contains("COMPRESS BASIC", move, StringComparison.Ordinal);
        Assert.Equal("MOVE 필요", row.EffectText);
    }

    [Fact]
    public void Invalid_number_is_an_error_and_the_model_stays()
    {
        var s = CreateStorage();
        var row = Row(s, StorageProperty.PctFree);

        row.Text = "abc";

        Assert.NotNull(row.Error);
        Assert.Equal(string.Empty, row.Text);
        Assert.True(s.Script.IsEmpty);
        Assert.Equal(s.Original, s.Draft);

        row.Text = "-1";
        Assert.NotNull(row.Error);
        Assert.True(s.Script.IsEmpty);

        row.Text = "15";
        Assert.Null(row.Error);
        Assert.Contains("PCTFREE 15", Assert.Single(Texts(s)), StringComparison.Ordinal);
    }

    [Fact]
    public void Lob_tablespace_change_is_a_move_with_the_lob_clause()
    {
        var s = CreateStorage();
        var lob = Assert.Single(s.LobRows);

        lob.Tablespace = "LOB02";

        var move = Assert.Single(Texts(s), t => t.Contains("MOVE", StringComparison.Ordinal));
        Assert.Contains("LOB (MEMO) STORE AS", move, StringComparison.Ordinal);
        Assert.Contains("TABLESPACE LOB02", move, StringComparison.Ordinal);
        Assert.True(lob.IsTablespaceChanged);
        Assert.Equal("LOB02", lob.Tablespace);
        Assert.True(s.HasStorageMove);
        Assert.Equal(1, s.CountFor(TableEditPage.Storage));
        Assert.Equal(0, s.CountFor(TableEditPage.Columns));

        lob.Tablespace = "lob01"; // 지금 값
        Assert.True(s.Script.IsEmpty);
        Assert.Equal(string.Empty, lob.Tablespace);
        Assert.False(s.IsDirty);
        Assert.Equal("LOB01", s.Draft.Columns.Single(c => c.Name == "MEMO").Lob!.Tablespace);
    }

    [Fact]
    public void Lob_choices_go_to_the_column_and_keep_the_others_unchanged()
    {
        var s = CreateStorage();
        var lob = Assert.Single(s.LobRows);

        lob.CompressionIndex = 3; // MEDIUM
        lob.InRowIndex = 2; // 아니오

        var memo = s.Draft.Columns.Single(c => c.Name == "MEMO").Lob!;
        Assert.Equal(LobCompression.Medium, memo.Compression);
        Assert.False(memo.InRow);
        Assert.Equal("LOB01", memo.Tablespace);
        var move = Assert.Single(Texts(s), t => t.Contains("MOVE", StringComparison.Ordinal));
        Assert.Contains("COMPRESS MEDIUM", move, StringComparison.Ordinal);
        Assert.Contains("DISABLE STORAGE IN ROW", move, StringComparison.Ordinal);
        Assert.True(lob.IsCompressionChanged);
        Assert.True(lob.IsInRowChanged);
        Assert.False(lob.IsKindChanged);

        lob.CompressionIndex = 0;
        lob.InRowIndex = 0;
        Assert.True(s.Script.IsEmpty);
    }

    [Fact]
    public void Lob_kind_is_disabled_with_the_reason_when_securefile_words_are_not_supported()
    {
        var s = CreateStorage(dialect: new OracleRules { Unsupported = f => f == TableEditFeature.SecureFileLob ? "TableEdit.unsupported.tibero.secureFileLob" : null });
        var lob = Assert.Single(s.LobRows);

        Assert.False(lob.CanEditKind);
        Assert.NotNull(lob.KindTip);
        Assert.False(lob.CanEditCompression); // SECUREFILE 전용 옵션
        Assert.True(lob.CanEditTablespace);

        lob.KindIndex = 2; // BASICFILE
        Assert.True(s.Script.IsEmpty);
        Assert.Equal(LobStorageKind.SecureFile, s.Draft.Columns.Single(c => c.Name == "MEMO").Lob!.Kind);
    }

    [Fact]
    public void Table_without_lob_columns_has_no_lob_rows()
    {
        var s = CreateStorage(StoredOrders() with { Columns = OrdersTable().Columns, Lobs = [] });

        Assert.Empty(s.LobRows);
        Assert.False(s.HasLobRows);
    }

    [Fact]
    public void Special_table_locks_the_whole_page_with_the_restriction_text()
    {
        var s = CreateStorage(StoredOrders(TableKind.Temporary));

        Assert.False(s.CanEditStorage);
        Assert.True(s.IsStorageLocked);
        Assert.Equal(s.RestrictionText, s.StorageLockedReason);
        Assert.All(s.StorageRows, r => Assert.False(r.CanEdit));
        Assert.All(s.LobRows, r => Assert.False(r.CanEditTablespace));

        Row(s, StorageProperty.PctFree).Text = "20";
        Row(s, StorageProperty.Compression).ChoiceIndex = 2;
        s.LobRows[0].Tablespace = "LOB02";
        Assert.True(s.Script.IsEmpty);
        Assert.Equal(s.Original, s.Draft);
    }

    [Fact]
    public void Row_objects_stay_the_same_across_recomputes()
    {
        var s = CreateStorage();
        var rows = s.StorageRows.ToList();
        var lob = s.LobRows[0];

        Row(s, StorageProperty.PctFree).Text = "20";
        Row(s, StorageProperty.Tablespace).Text = "DATA01";
        s.LobRows[0].Tablespace = "LOB02";
        s.RevertAll();

        Assert.Equal(rows, s.StorageRows);
        Assert.Same(lob, s.LobRows[0]);
        Assert.True(s.Script.IsEmpty);
        Assert.All(s.StorageRows.Where(r => r.IsText), r => Assert.Equal(string.Empty, r.Text));
    }

    [Fact]
    public void New_table_shows_extent_rows_and_the_values_become_the_create_storage_clause()
    {
        var dialect = new OracleRules();
        var draft = new TableDraft("SAMPLE", "T")
        {
            IsNewTable = true,
            Columns =
            [
                new ColumnDraft("n1", "ID") { DataType = "NUMBER", Precision = 10 },
                new ColumnDraft("n2", "DOC") { DataType = "CLOB" },
            ],
        };
        var s = new TableEditSession(TableDraftFactory.Empty("SAMPLE"), draft, dialect,
            new TableEditContext(128, ConnectionEnvironment.Development, Oracle19), CoreTableEditEngine.Instance);

        Assert.Equal(
            [
                StorageProperty.Tablespace, StorageProperty.PctFree, StorageProperty.InitTrans, StorageProperty.Compression, StorageProperty.Logging,
                StorageProperty.Parallel, StorageProperty.InitialExtent, StorageProperty.NextExtent,
            ],
            s.StorageRows.Select(r => r.Attribute));
        Assert.All(s.StorageRows, r => Assert.Equal(string.Empty, r.CurrentText));
        var doc = Assert.Single(s.LobRows);
        Assert.Equal("DOC", doc.ColumnName);

        Row(s, StorageProperty.Tablespace).Text = "DATA01";
        Row(s, StorageProperty.PctFree).Text = "5";
        Row(s, StorageProperty.InitialExtent).Text = "64K";
        Row(s, StorageProperty.NextExtent).Text = "1M";
        doc.Tablespace = "LOB01";

        var create = Assert.Single(Texts(s));
        Assert.StartsWith("CREATE TABLE", create, StringComparison.Ordinal);
        Assert.Contains("TABLESPACE DATA01", create, StringComparison.Ordinal);
        Assert.Contains("PCTFREE 5", create, StringComparison.Ordinal);
        Assert.Contains("STORAGE (INITIAL 64K NEXT 1M)", create, StringComparison.Ordinal);
        Assert.Contains("LOB (DOC) STORE AS", create, StringComparison.Ordinal);
        Assert.Contains("TABLESPACE LOB01", create, StringComparison.Ordinal);
        Assert.True(Row(s, StorageProperty.InitialExtent).IsChanged);
        Assert.Equal("64K", Row(s, StorageProperty.InitialExtent).Text);
        Assert.False(s.HasStorageMove);
    }
}
