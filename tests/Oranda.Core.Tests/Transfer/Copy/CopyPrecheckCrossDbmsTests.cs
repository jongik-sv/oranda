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

    [Fact]
    public async Task Xmltype_column_that_the_oracle_catalog_marks_virtual_is_dropped_with_a_confirm_item_in_both_cross_and_same_dbms()
    {
        // Oracle 카탈로그(*_TAB_COLS)는 XMLTYPE 열의 VIRTUAL_COLUMN 을 YES 로 돌려준다. 예전에는 이 열이 가상 열로 조용히 사라졌다.
        ColumnInfo[] Columns(string xmlType) =>
            [new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("X", 2, xmlType) { IsVirtual = true }];
        ColumnInfo[] TargetColumns() =>
            [new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("X", 2, "VARCHAR2") { Length = 40 }];

        var cross = new Fixture(Dbms.Oracle, Dbms.Tibero, source: Table("SRC", Columns("XMLTYPE")), target: Table("DST", TargetColumns()),
            unsupportedBySource: ["XMLTYPE"]);
        var crossReport = await cross.RunAsync();

        var dropped = Assert.Single(crossReport.Items, i => i.Code == CopyCheckCodes.ColumnDropped);
        Assert.Equal("X", dropped.Column);
        Assert.Equal(CheckLevel.Confirm, dropped.Level);
        Assert.Contains("XMLTYPE", dropped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(crossReport.Items, i => i.Code == "UnsupportedType");
        Assert.Equal(["ID"], Assert.Single(crossReport.Order).Columns);

        var same = new Fixture(Dbms.Oracle, Dbms.Oracle, source: Table("SRC", Columns("XMLTYPE")), target: Table("DST", Columns("XMLTYPE")),
            unsupportedBySource: ["XMLTYPE"], unsupportedByTarget: ["XMLTYPE"]);
        var sameReport = await same.RunAsync();

        // 같은 DBMS: 예전처럼 빼고 복사하되(막지 않음) 확인 항목으로 알린다.
        var note = Assert.Single(sameReport.Items, i => i.Code == CopyCheckCodes.ColumnDropped);
        Assert.Equal("X", note.Column);
        Assert.Equal(CheckLevel.Confirm, note.Level);
        Assert.Contains("XMLTYPE", note.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sameReport.Items, i => i.Level == CheckLevel.Error);
        Assert.Equal(["ID"], Assert.Single(sameReport.Order).Columns);
    }

    [Fact]
    public void National_varchar_dictionary_names_do_not_look_like_a_type_change_only_in_a_cross_copy()
    {
        var rule = new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert);
        var codec = new FakeCodec([]);
        var oracle = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("N", 2, "NVARCHAR2") { Length = 20 }, new ColumnInfo("V", 3, "VARCHAR2") { Length = 20 });
        var tibero = Table("DST", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("N", 2, "NVARCHAR") { Length = 20 }, new ColumnInfo("V", 3, "VARCHAR") { Length = 20 });

        Assert.DoesNotContain(PrecheckRules.CompareColumns(codec, codec, oracle, tibero, rule, "ORDERS", Dbms.Oracle, Dbms.Tibero), i => i.Code == "NarrowerType");
        Assert.DoesNotContain(PrecheckRules.CompareColumns(codec, codec, tibero, oracle, rule, "ORDERS", Dbms.Tibero, Dbms.Oracle), i => i.Code == "NarrowerType");

        // 진짜 차이(NUMBER → VARCHAR2)는 교차에서도 그대로 알린다.
        var changed = Table("DST", new ColumnInfo("ID", 1, "VARCHAR2") { Length = 20, IsNullable = false });
        var real = PrecheckRules.CompareColumns(codec, codec, oracle, changed, rule, "ORDERS", Dbms.Oracle, Dbms.Tibero);
        Assert.Contains(real, i => i.Code == "NarrowerType" && i.Column == "ID");
    }

    [Fact]
    public async Task Tibero_char_length_columns_over_the_oracle_byte_limit_get_one_confirm_item_for_created_and_existing_target_tables()
    {
        // 1000자 x 4바이트 = 4000 은 넘지 않는다. 1001자부터 넘을 수 있다. 4000자 넘는 VARCHAR 는 CLOB 으로 옮기므로 대상이 아니다.
        ColumnInfo[] Columns() =>
        [
            new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false },
            new ColumnInfo("FITS", 2, "VARCHAR") { Length = 1000 },
            new ColumnInfo("V2000", 3, "VARCHAR") { Length = 2000 },
            new ColumnInfo("V4000", 4, "VARCHAR") { Length = 4000 },
            new ColumnInfo("BIG", 5, "VARCHAR") { Length = 4001 },
        ];
        ColumnInfo[] OracleColumns() =>
        [
            new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false },
            new ColumnInfo("FITS", 2, "VARCHAR2") { Length = 1000 },
            new ColumnInfo("V2000", 3, "VARCHAR2") { Length = 2000 },
            new ColumnInfo("V4000", 4, "VARCHAR2") { Length = 4000 },
            new ColumnInfo("BIG", 5, "CLOB"),
        ];

        // 있는 대상 표: 대상 열도 VARCHAR2(n) 라 같은 한도를 본다.
        var existing = await new Fixture(Dbms.Tibero, Dbms.Oracle, source: Table("SRC", Columns()), target: Table("DST", OracleColumns())).RunAsync();
        var item = Assert.Single(existing.Items, i => i.Code == CopyCheckCodes.CrossStringBytes);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Equal("ORDERS", item.Table);
        Assert.Contains("V2000, V4000", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("FITS", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BIG", item.Message, StringComparison.Ordinal);
        Assert.Contains("4000바이트", item.Message, StringComparison.Ordinal);
        Assert.Contains("그 행이 실패", item.Message, StringComparison.Ordinal);

        // 표를 새로 만드는 경우: 대상 열 = 원본 열.
        var created = await new Fixture(Dbms.Tibero, Dbms.Oracle, source: Table("SRC", Columns()), target: null, createMissing: true).RunAsync();
        var createdItem = Assert.Single(created.Items, i => i.Code == CopyCheckCodes.CrossStringBytes);
        Assert.Contains("V2000, V4000", createdItem.Message, StringComparison.Ordinal);
        Assert.Contains(created.Items, i => i.Code == CopyCheckCodes.CreateTable);

        // 대상 열이 더 짧으면 짧은 쪽으로 본다: 대상 VARCHAR2(500) 은 500 x 4 = 2000 바이트라 넘지 않는다.
        var narrow = await new Fixture(Dbms.Tibero, Dbms.Oracle,
            source: Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("V", 2, "VARCHAR") { Length = 2000 }),
            target: Table("DST", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("V", 2, "VARCHAR2") { Length = 500 })).RunAsync();
        Assert.DoesNotContain(narrow.Items, i => i.Code == CopyCheckCodes.CrossStringBytes);

        // CHAR 는 Oracle 한도가 2000바이트다: CHAR(500) 은 넘지 않고 CHAR(501) 부터 넘을 수 있다.
        var chars = await new Fixture(Dbms.Tibero, Dbms.Oracle,
            source: Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("C500", 2, "CHAR") { Length = 500 }, new ColumnInfo("C501", 3, "CHAR") { Length = 501 }),
            target: null, createMissing: true).RunAsync();
        var charItem = Assert.Single(chars.Items, i => i.Code == CopyCheckCodes.CrossStringBytes);
        Assert.Contains("C501", charItem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("C500", charItem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Char_length_byte_limit_note_is_only_for_tibero_to_oracle_copies()
    {
        ColumnInfo[] Columns(string type) => [new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("V", 2, type) { Length = 2000 }];

        var oracleToTibero = await new Fixture(Dbms.Oracle, Dbms.Tibero, source: Table("SRC", Columns("VARCHAR2")), target: Table("DST", Columns("VARCHAR2"))).RunAsync();
        var sameOracle = await new Fixture(Dbms.Oracle, Dbms.Oracle, source: Table("SRC", Columns("VARCHAR2")), target: Table("DST", Columns("VARCHAR2"))).RunAsync();
        var sameTibero = await new Fixture(Dbms.Tibero, Dbms.Tibero, source: Table("SRC", Columns("VARCHAR")), target: Table("DST", Columns("VARCHAR"))).RunAsync();

        Assert.DoesNotContain(oracleToTibero.Items, i => i.Code == CopyCheckCodes.CrossStringBytes);
        Assert.DoesNotContain(sameOracle.Items, i => i.Code == CopyCheckCodes.CrossStringBytes);
        Assert.DoesNotContain(sameTibero.Items, i => i.Code == CopyCheckCodes.CrossStringBytes);
    }

    [Fact]
    public async Task Oracle_char_columns_into_tibero_get_a_pass_note_about_trailing_space_padding_and_no_other_direction_does()
    {
        ColumnInfo[] Columns() => [new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("C", 2, "CHAR") { Length = 2000 }, new ColumnInfo("V", 3, "VARCHAR2") { Length = 20 }];

        var report = await new Fixture(Dbms.Oracle, Dbms.Tibero, source: Table("SRC", Columns()), target: Table("DST", Columns())).RunAsync();

        var note = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.CharPadDiffers);
        Assert.Equal(CheckLevel.Pass, note.Level);
        Assert.Equal("ORDERS", note.Table);
        Assert.Contains("ORDERS: CHAR(n CHAR) 열 C ", note.Message, StringComparison.Ordinal);
        Assert.Contains("뒤 공백 수가 달라질 수 있습니다", note.Message, StringComparison.Ordinal);

        var reverse = await new Fixture(Dbms.Tibero, Dbms.Oracle, source: Table("SRC", Columns()), target: Table("DST", Columns())).RunAsync();
        var same = await new Fixture(Dbms.Oracle, Dbms.Oracle, source: Table("SRC", Columns()), target: Table("DST", Columns())).RunAsync();
        Assert.DoesNotContain(reverse.Items, i => i.Code == CopyCheckCodes.CharPadDiffers);
        Assert.DoesNotContain(same.Items, i => i.Code == CopyCheckCodes.CharPadDiffers);
    }

    /// <summary>
    /// TR1b: 바이트 길이가 글자 길이와 같은 큰 CHAR(Oracle <c>CHAR(2000 CHAR)</c> 는 2000바이트 한도에 걸려 DATA_LENGTH = CHAR_LENGTH = 2000)는 Tibero 에
    /// <c>CHAR(2000 BYTE)</c> 로 만들어져(<see cref="CrossTableMap.TiberoByteKeptColumns"/>) 바이트로 채우므로 공백 안내를 내지 않는다. 작은 CHAR 는 그대로 안내한다.
    /// </summary>
    [Fact]
    public async Task A_large_char_kept_in_bytes_for_tibero_gets_no_trailing_space_note_but_a_small_char_still_does()
    {
        ColumnInfo[] Columns() =>
        [
            new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false },
            new ColumnInfo("BIG", 2, "CHAR") { Length = 2000, ByteLength = 2000 },
            new ColumnInfo("SMALL", 3, "CHAR") { Length = 10, ByteLength = 40 },
        ];

        var report = await new Fixture(Dbms.Oracle, Dbms.Tibero, source: Table("SRC", Columns()), target: Table("DST", Columns())).RunAsync();

        var note = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.CharPadDiffers);
        Assert.Contains("CHAR(n CHAR) 열 SMALL ", note.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BIG", note.Message, StringComparison.Ordinal);

        ColumnInfo[] OnlyBig() => [new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("BIG", 2, "CHAR") { Length = 2000, ByteLength = 2000 }];
        var bigOnly = await new Fixture(Dbms.Oracle, Dbms.Tibero, source: Table("SRC", OnlyBig()), target: Table("DST", OnlyBig())).RunAsync();
        Assert.DoesNotContain(bigOnly.Items, i => i.Code == CopyCheckCodes.CharPadDiffers);
    }

    [Theory]
    [InlineData(Dbms.Tibero, Dbms.Oracle, "SEQ_ORD.NEXTVAL", "\"ID\" NUMBER DEFAULT SEQ_ORD.NEXTVAL NOT NULL")]
    [InlineData(Dbms.Oracle, Dbms.Tibero, "\"SRC\".\"SEQ_ORD\".\"NEXTVAL\"", "\"ID\" NUMBER DEFAULT \"DST\".\"SEQ_ORD\".\"NEXTVAL\" NOT NULL")]
    public async Task Cross_create_keeps_a_sequence_default_when_the_target_schema_has_the_sequence(Dbms sourceDbms, Dbms targetDbms, string defaultValue, string expectedColumn)
    {
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false, DefaultValue = defaultValue }, new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 });

        var report = await new Fixture(sourceDbms, targetDbms, source, target: null, createMissing: true, targetSequences: ["SEQ_ORD"]).RunAsync();

        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.SequenceDefaultDropped);
        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.CreateBlocked);
        Assert.Contains(report.Items, i => i.Code == CopyCheckCodes.CreateTable);
        Assert.Contains(expectedColumn, CreateDdl(report), StringComparison.Ordinal);
        Assert.Equal(defaultValue, Assert.Single(report.Order).Source.Columns.First(c => c.Name == "ID").DefaultValue);
    }

    [Theory]
    [InlineData(Dbms.Tibero, Dbms.Oracle, "SEQ_ORD.NEXTVAL")]
    [InlineData(Dbms.Tibero, Dbms.Oracle, "\"SRC\".\"SEQ_ORD\".NEXTVAL")]
    [InlineData(Dbms.Oracle, Dbms.Tibero, "\"SRC\".\"SEQ_ORD\".\"NEXTVAL\"")]
    public async Task Cross_create_drops_only_the_default_whose_sequence_is_missing_in_the_target_and_previews_the_same_ddl(Dbms sourceDbms, Dbms targetDbms, string defaultValue)
    {
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false, DefaultValue = defaultValue },
            new ColumnInfo("QTY", 2, "NUMBER") { DefaultValue = "0" }, new ColumnInfo("NAME", 3, "VARCHAR2") { Length = 20, DefaultValue = "'x'" });

        var report = await new Fixture(sourceDbms, targetDbms, source, target: null, createMissing: true, targetSequences: ["OTHER_SEQ"]).RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.SequenceDefaultDropped);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Equal("ORDERS", item.Table);
        Assert.Equal("ID", item.Column);
        Assert.Contains("SEQ_ORD", item.Message, StringComparison.Ordinal);
        Assert.Contains("NEXTVAL", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.CreateBlocked);
        Assert.False(report.HasErrors);

        // 미리 보기 DDL 은 실행기가 보낼 문장과 같은 입력(계획의 원본 구조)에서 나온다: ID 의 DEFAULT 만 빠진다.
        var plan = Assert.Single(report.Order);
        Assert.True(plan.CreateMissing);
        Assert.Null(plan.Source.Columns.First(c => c.Name == "ID").DefaultValue);
        Assert.Equal("0", plan.Source.Columns.First(c => c.Name == "QTY").DefaultValue);
        var ddl = CreateDdl(report);
        Assert.DoesNotContain("NEXTVAL", ddl, StringComparison.Ordinal);
        Assert.Contains("\"QTY\" NUMBER DEFAULT 0", ddl, StringComparison.Ordinal);
        // 새 표의 PK·UK·인덱스는 적재 뒤에 붙인다(PF4a): 실행기·미리 보기 모두 keysAfterLoad 로 만든다.
        var sent = CrossTableMap.Build(sourceDbms, targetDbms, plan.Source, "DST", new HashSet<string>(StringComparer.Ordinal), keysAfterLoad: true).Statements;
        Assert.Equal(sent[0], ddl);
    }

    [Theory]
    [InlineData("11.2.0.4.0", true)]
    [InlineData("19.0.0.0.0", false)]
    public async Task Cross_create_into_oracle_11g_drops_the_sequence_default_even_when_the_sequence_exists(string targetVersion, bool dropped)
    {
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false, DefaultValue = "SEQ_ORD.NEXTVAL" }, new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 });

        var report = await new Fixture(Dbms.Tibero, Dbms.Oracle, source, target: null, createMissing: true, targetSequences: ["SEQ_ORD"], targetVersion: targetVersion).RunAsync();

        Assert.False(report.HasErrors);
        if (dropped)
        {
            var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.SequenceDefaultDropped);
            Assert.Equal(CheckLevel.Confirm, item.Level);
            Assert.Equal("ID", item.Column);
            Assert.Contains("Oracle 11", item.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("NEXTVAL", CreateDdl(report), StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.SequenceDefaultDropped);
            Assert.Contains("DEFAULT SEQ_ORD.NEXTVAL", CreateDdl(report), StringComparison.Ordinal);
        }
    }

    // ---- SQ1: 연결된 시퀀스도 대상에 만들기(조정 결정 76) ----

    /// <summary>원본 시퀀스 속성 행(copy.sequence_attrs: min, max, inc, cycle, order, cache, last).</summary>
    private static object?[] SequenceRow(string last = "21", string cache = "20") => ["1", "9999999999999999999999999999", "1", "N", "N", cache, last];

    private static SequenceAnswers CanCreate(string name = "SEQ_ORD", string last = "21") => new()
    {
        TargetPrivileges = ["CREATE SEQUENCE"],
        SourceAttributes = new() { [name] = SequenceRow(last) },
    };

    [Fact]
    public async Task Same_dbms_create_without_create_sequence_privilege_drops_the_default_instead_of_blocking()
    {
        // 결정 76 이 결정 50 의 「없으면 DEFAULT 빼기」를 대신한다: 같은 DBMS 도 만들 수 없으면 막지 않고 DEFAULT 만 뺀다(예전에는 CreateBlocked).
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false, DefaultValue = "\"SRC\".\"SEQ_ORD\".\"NEXTVAL\"" });

        var report = await new Fixture(Dbms.Oracle, Dbms.Oracle, source, target: null, createMissing: true, targetSequences: ["OTHER_SEQ"]).RunAsync();

        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.CreateBlocked);
        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.SequenceCreated);
        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.SequenceDefaultDropped);
        Assert.Contains("CREATE SEQUENCE 권한이 없어", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NEXTVAL", CreateDdl(report), StringComparison.Ordinal);
        Assert.Empty(Assert.Single(report.Order).CreateSequences);
    }

    [Theory]
    [InlineData(Dbms.Oracle, Dbms.Oracle, "\"SRC\".\"SEQ_ORD\".\"NEXTVAL\"", "DEFAULT \"DST\".\"SEQ_ORD\".\"NEXTVAL\"")]
    [InlineData(Dbms.Tibero, Dbms.Oracle, "SEQ_ORD.NEXTVAL", "DEFAULT SEQ_ORD.NEXTVAL")]
    [InlineData(Dbms.Oracle, Dbms.Tibero, "\"SRC\".\"SEQ_ORD\".\"NEXTVAL\"", "DEFAULT \"DST\".\"SEQ_ORD\".\"NEXTVAL\"")]
    // 같은 DBMS Tibero→Tibero(실사용: d55f4231 은 같은 DBMS 면 시퀀스를 보지 않아 CREATE TABLE 이 TBR-8034 로 실패했다)
    [InlineData(Dbms.Tibero, Dbms.Tibero, "SEQ_ORD.NEXTVAL", "DEFAULT SEQ_ORD.NEXTVAL")]
    [InlineData(Dbms.Tibero, Dbms.Tibero, "\"SRC\".\"SEQ_ORD\".\"NEXTVAL\"", "DEFAULT \"DST\".\"SEQ_ORD\".\"NEXTVAL\"")]
    public async Task Missing_linked_sequence_is_created_before_the_table_with_the_source_attributes_and_the_default_is_kept(
        Dbms sourceDbms, Dbms targetDbms, string defaultValue, string expectedDefault)
    {
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false, DefaultValue = defaultValue }, new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 });

        var report = await new Fixture(sourceDbms, targetDbms, source, target: null, createMissing: true, sequences: CanCreate()).RunAsync();

        Assert.False(report.HasErrors);
        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.SequenceDefaultDropped);
        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.SequenceCreated);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Contains("DST.SEQ_ORD(START WITH 21)", item.Message, StringComparison.Ordinal);

        var plan = Assert.Single(report.Order);
        var sequence = Assert.Single(plan.CreateSequences);
        Assert.Equal(("DST", "SEQ_ORD"), (sequence.Owner, sequence.Name));
        Assert.Equal(["ID"], sequence.KeyColumns);
        Assert.Equal(["ID"], sequence.DefaultColumns);
        Assert.Contains(expectedDefault, CreateDdl(report), StringComparison.Ordinal);

        // 미리 보기: CREATE SEQUENCE 가 CREATE TABLE 보다 먼저, 정리용 DROP SEQUENCE(위험 명령)도 들어 있다.
        var texts = report.Preview.Select(p => p.Sql).ToList();
        const string Create = "CREATE SEQUENCE \"DST\".\"SEQ_ORD\" MINVALUE 1 NOMAXVALUE INCREMENT BY 1 START WITH 21 CACHE 20 NOORDER NOCYCLE";
        Assert.Contains(Create, texts);
        Assert.True(texts.IndexOf(Create) < texts.FindIndex(t => t.StartsWith("CREATE TABLE", StringComparison.Ordinal)));
        Assert.Contains("DROP SEQUENCE \"DST\".\"SEQ_ORD\"", texts);
        Assert.Equal(Create, sequence.CreateDdl);
    }

    // ---- 시퀀스 보정 연결의 시퀀스가 대상에 없으면 만든다(SQ3, 결정 84) ----

    private static readonly SequenceRule OrdersRule = new("SEQ_ORD", "ORDERS", "ID");

    private static TableInfo PlainOrders(string owner) => Table(owner, new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 });

    [Theory]
    [InlineData(Dbms.Oracle, Dbms.Oracle)]
    [InlineData(Dbms.Tibero, Dbms.Tibero)]
    [InlineData(Dbms.Tibero, Dbms.Oracle)]
    [InlineData(Dbms.Oracle, Dbms.Tibero)]
    public async Task Rule_sequence_missing_in_the_target_is_created_from_the_source_attributes_before_the_load(Dbms sourceDbms, Dbms targetDbms)
    {
        // 원본 다음 값 21 < 원본 키 최댓값 50: 만들 때부터 최댓값 + 증가값(51)에서 시작한다(적재 뒤 보정이 NEXTVAL 을 덜 쓴다).
        var answers = CanCreate() with { SourceMax = 50 };

        var report = await new Fixture(sourceDbms, targetDbms, PlainOrders("SRC"), PlainOrders("DST"), sequences: answers, sequenceRules: [OrdersRule]).RunAsync();

        Assert.False(report.HasErrors);
        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.SequenceCreated);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Equal(("ORDERS", "ID", "SEQ_ORD"), (item.Table, item.Column, item.Sequence));
        Assert.Contains("DST.SEQ_ORD", item.Message, StringComparison.Ordinal);
        Assert.Contains("원본 시퀀스 SRC.SEQ_ORD", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Items, i => i.Code == "SequenceGap" && i.Message.Contains("볼 수 없어", StringComparison.Ordinal));

        var plan = Assert.Single(report.Order);
        Assert.Empty(plan.CreateSequences);
        var sequence = Assert.Single(plan.RuleSequences);
        Assert.Equal(("DST", "SEQ_ORD", "51"), (sequence.Owner, sequence.Name, sequence.Spec.StartWith));
        Assert.Equal(["ID"], sequence.KeyColumns);

        const string Create = "CREATE SEQUENCE \"DST\".\"SEQ_ORD\" MINVALUE 1 NOMAXVALUE INCREMENT BY 1 START WITH 51 CACHE 20 NOORDER NOCYCLE";
        Assert.Equal(Create, sequence.CreateDdl);
        Assert.Contains(report.Preview, p => p.Side == PreviewSide.Target && p.Sql == Create);
        // 이미 있는 표가 쓰는 시퀀스는 적재가 실패해도 지우지 않는다: 지우는 문장이 없다.
        Assert.DoesNotContain(report.Preview, p => p.Sql.StartsWith("DROP SEQUENCE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rule_sequence_missing_in_both_is_created_with_default_attributes_after_the_source_key_maximum()
    {
        var answers = new SequenceAnswers { TargetPrivileges = ["CREATE SEQUENCE"], SourceMax = 50 };

        var report = await new Fixture(Dbms.Tibero, Dbms.Tibero, PlainOrders("SRC"), PlainOrders("DST"), sequences: answers, sequenceRules: [OrdersRule]).RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.SequenceCreated);
        Assert.Contains("기본 속성", item.Message, StringComparison.Ordinal);
        var sequence = Assert.Single(Assert.Single(report.Order).RuleSequences);
        Assert.True(sequence.Spec.DefaultCache);
        // 캐시 절이 없다(DBMS 기본), 증가 1, 시작 = 원본 키 최댓값 + 1.
        Assert.Equal("CREATE SEQUENCE \"DST\".\"SEQ_ORD\" INCREMENT BY 1 START WITH 51 NOORDER NOCYCLE", sequence.CreateDdl);
        Assert.Contains(report.Preview, p => p.Sql == sequence.CreateDdl);
    }

    [Fact]
    public async Task Rule_sequence_missing_without_create_sequence_privilege_keeps_the_cannot_adjust_notice()
    {
        var answers = new SequenceAnswers { SourceAttributes = CanCreate().SourceAttributes, SourceMax = 50 };

        var report = await new Fixture(Dbms.Oracle, Dbms.Oracle, PlainOrders("SRC"), PlainOrders("DST"), sequences: answers, sequenceRules: [OrdersRule]).RunAsync();

        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.SequenceCreated);
        var gap = Assert.Single(report.Items, i => i.Code == "SequenceGap" && i.Message.Contains("볼 수 없어", StringComparison.Ordinal));
        Assert.Equal(CheckLevel.Confirm, gap.Level);
        Assert.Empty(Assert.Single(report.Order).RuleSequences);
        Assert.DoesNotContain(report.Preview, p => p.Sql.StartsWith("CREATE SEQUENCE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rule_sequence_already_planned_by_the_table_create_is_not_planned_twice()
    {
        // 「없으면 만들기」가 열 기본값 때문에 이미 만드는 시퀀스(SQ1)를 보정 연결도 쓰면 한 번만 만든다(그 시퀀스는 만든 뒤 지금처럼 보정한다).
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false, DefaultValue = "\"SRC\".\"SEQ_ORD\".\"NEXTVAL\"" });

        var report = await new Fixture(Dbms.Oracle, Dbms.Oracle, source, target: null, createMissing: true, sequences: CanCreate() with { SourceMax = 50 },
            sequenceRules: [OrdersRule]).RunAsync();

        var plan = Assert.Single(report.Order);
        Assert.Single(plan.CreateSequences);
        Assert.Empty(plan.RuleSequences);
        Assert.Single(report.Items, i => i.Code == CopyCheckCodes.SequenceCreated);
        Assert.DoesNotContain(report.Items, i => i.Code == "SequenceGap" && i.Message.Contains("볼 수 없어", StringComparison.Ordinal));
        Assert.Single(report.Preview, p => p.Sql.StartsWith("CREATE SEQUENCE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rule_sequence_of_a_table_the_run_creates_is_dropped_with_it_in_the_preview()
    {
        var report = await new Fixture(Dbms.Oracle, Dbms.Oracle, PlainOrders("SRC"), target: null, createMissing: true, sequences: CanCreate() with { SourceMax = 50 },
            sequenceRules: [OrdersRule]).RunAsync();

        var sequence = Assert.Single(Assert.Single(report.Order).RuleSequences);
        var texts = report.Preview.Select(p => p.Sql).ToList();
        Assert.Contains(sequence.CreateDdl, texts);
        Assert.Contains("DROP SEQUENCE \"DST\".\"SEQ_ORD\"", texts);
    }

    [Fact]
    public async Task Oracle_11g_target_creates_the_sequence_but_still_drops_the_default()
    {
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false, DefaultValue = "SEQ_ORD.NEXTVAL" });

        var report = await new Fixture(Dbms.Tibero, Dbms.Oracle, source, target: null, createMissing: true, targetVersion: "11.2.0.4.0", sequences: CanCreate()).RunAsync();

        Assert.Single(report.Items, i => i.Code == CopyCheckCodes.SequenceCreated);
        var dropped = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.SequenceDefaultDropped);
        Assert.Contains("Oracle 11", dropped.Message, StringComparison.Ordinal);
        Assert.Contains("시퀀스는 만듭니다", dropped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NEXTVAL", CreateDdl(report), StringComparison.Ordinal);
        Assert.Single(Assert.Single(report.Order).CreateSequences);
    }

    [Fact]
    public async Task Sequence_in_another_owner_is_not_created_and_its_missing_default_is_dropped()
    {
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false, DefaultValue = "\"OTHER\".\"SEQ_ORD\".\"NEXTVAL\"" });

        var report = await new Fixture(Dbms.Oracle, Dbms.Tibero, source, target: null, createMissing: true, sequences: CanCreate()).RunAsync();

        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.SequenceCreated);
        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.SequenceDefaultDropped);
        Assert.Contains("다른 계정의 시퀀스는 만들지 않습니다", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_source_sequence_attributes_drop_the_default()
    {
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false, DefaultValue = "SEQ_ORD.NEXTVAL" });

        var report = await new Fixture(Dbms.Tibero, Dbms.Oracle, source, target: null, createMissing: true,
            sequences: new SequenceAnswers { TargetPrivileges = ["CREATE SEQUENCE"] }).RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.SequenceDefaultDropped);
        Assert.Contains("속성을 읽지 못해", item.Message, StringComparison.Ordinal);
        Assert.Empty(Assert.Single(report.Order).CreateSequences);
    }

    [Fact]
    public async Task Sequence_used_only_by_a_source_trigger_is_created_for_a_new_table_with_the_numeric_pk_as_key()
    {
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 });
        var answers = CanCreate("SEQ_TRG") with { TriggerSequences = [("SRC", "SEQ_TRG")] };

        var report = await new Fixture(Dbms.Oracle, Dbms.Oracle, source, target: null, createMissing: true, sequences: answers).RunAsync();

        var sequence = Assert.Single(Assert.Single(report.Order).CreateSequences);
        Assert.Equal("SEQ_TRG", sequence.Name);
        Assert.Equal(["ID"], sequence.KeyColumns);
        Assert.Empty(sequence.DefaultColumns);
    }

    [Fact]
    public async Task Existing_target_sequence_is_left_alone_and_warned_when_it_is_behind_the_source_keys()
    {
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false, DefaultValue = "SEQ_ORD.NEXTVAL" });

        var behind = await new Fixture(Dbms.Tibero, Dbms.Oracle, source, target: null, createMissing: true, targetSequences: ["SEQ_ORD"],
            sequences: CanCreate() with { SourceMax = 500, TargetLastNumber = 100 }).RunAsync();
        var ahead = await new Fixture(Dbms.Tibero, Dbms.Oracle, source, target: null, createMissing: true, targetSequences: ["SEQ_ORD"],
            sequences: CanCreate() with { SourceMax = 500, TargetLastNumber = 1000 }).RunAsync();

        Assert.DoesNotContain(behind.Items, i => i.Code == CopyCheckCodes.SequenceCreated);
        Assert.Empty(Assert.Single(behind.Order).CreateSequences);
        var warning = Assert.Single(behind.Items, i => i.Code == CopyCheckCodes.SequenceBehind);
        Assert.Equal(CheckLevel.Confirm, warning.Level);
        Assert.Contains("건드리지 않습니다", warning.Message, StringComparison.Ordinal);
        Assert.Equal("SEQ_ORD", warning.Sequence);
        Assert.DoesNotContain(ahead.Items, i => i.Code == CopyCheckCodes.SequenceBehind);
        Assert.DoesNotContain(behind.Preview, p => p.Sql.Contains("SEQUENCE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recreate_also_creates_a_missing_linked_sequence_and_does_not_block()
    {
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false, DefaultValue = "\"SRC\".\"SEQ_ORD\".\"NEXTVAL\"" });

        var report = await new Fixture(Dbms.Oracle, Dbms.Oracle, source, target: Table("DST", new ColumnInfo("ID", 1, "NUMBER")),
            action: TargetAction.Recreate, sequences: CanCreate()).RunAsync();

        Assert.DoesNotContain(report.Items, i => i.Code == "RecreateBlocked");
        Assert.Single(report.Items, i => i.Code == CopyCheckCodes.SequenceCreated);
        Assert.Single(Assert.Single(report.Order).CreateSequences);
        Assert.Contains(report.Preview, p => p.Sql.StartsWith("CREATE SEQUENCE \"DST\".\"SEQ_ORD\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Sequence_references_are_read_from_default_expressions_with_or_without_owner_and_quotes()
    {
        Assert.Equal([((string?)null, "ORT_SEQ")], PrecheckRules.SequenceReferences("ORT_SEQ.NEXTVAL"));
        Assert.Equal([((string?)null, "ort_seq")], PrecheckRules.SequenceReferences("\"ort_seq\".nextval"));
        Assert.Equal([("SRC", "SEQ")], PrecheckRules.SequenceReferences("src.seq.NEXTVAL"));
        Assert.Equal([("SRC", "My Seq")], PrecheckRules.SequenceReferences("\"SRC\".\"My Seq\".\"NEXTVAL\""));
        Assert.Equal([("A", "S1"), ((string?)null, "S2")], PrecheckRules.SequenceReferences("A.S1.NEXTVAL + S2.NEXTVAL"));
        Assert.Empty(PrecheckRules.SequenceReferences("0"));
        Assert.Empty(PrecheckRules.SequenceReferences("'x'"));
        Assert.Empty(PrecheckRules.SequenceReferences(null));
    }

    // ---- SQ2: 만드는 표에 적재 뒤 함께 만드는 객체(조정 결정 78) ----

    private const string SourceTrigger = "CREATE OR REPLACE TRIGGER \"SRC\".\"TRG_ORD\" BEFORE INSERT ON \"SRC\".\"ORDERS\" FOR EACH ROW BEGIN :NEW.NAME := UPPER(:NEW.NAME); END;";
    private const string TargetTrigger = "CREATE OR REPLACE TRIGGER \"DST\".\"TRG_ORD\" BEFORE INSERT ON \"DST\".\"ORDERS\" FOR EACH ROW BEGIN :NEW.NAME := UPPER(:NEW.NAME); END;";
    private const string FunctionIndexDdl = "CREATE INDEX \"DST\".\"IX_ORD_UPNAME\" ON \"DST\".\"ORDERS\" (UPPER(\"NAME\"))";
    private const string OuterForeignKey = "ALTER TABLE \"DST\".\"ORDERS\" ADD CONSTRAINT \"FK_ORD_CUST\" FOREIGN KEY (\"CUST_ID\") REFERENCES \"DST\".\"CUSTOMERS\" (\"ID\")";
    private const string CompanionTail = " 함께 만들지 못한 것이 있어도 테이블은 실패로 보지 않고 알림에 문장을 남깁니다(만드는 문장은 미리 보기에 있습니다).";

    private static ColumnInfo IdColumn() => new("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false };

    private static ColumnInfo NameColumn() => new("NAME", 2, "VARCHAR2") { Length = 20 };

    /// <summary>원본 표에 함수 기반 인덱스 IX_ORD_UPNAME 이 있다(카탈로그 TableInfo 에는 식이 없고 숨은 열 이름만 있다).</summary>
    private static TableInfo WithFunctionIndex(TableInfo table) =>
        table with { Indexes = [new IndexInfo("IX_ORD_UPNAME", false, ["SYS_NC00003$"]) { IndexType = "FUNCTION-BASED NORMAL" }] };

    /// <summary>IX_ORD_UPNAME 의 식(원본 인덱스 열 조회 답).</summary>
    private static CompanionAnswers FunctionIndexAnswer(string expression = "UPPER(\"NAME\")") => new()
    {
        IndexColumns = new() { ["IX_ORD_UPNAME"] = [new IndexColumnDetail(1, "SYS_NC00003$", expression, false)] },
    };

    /// <summary>원본 트리거 TRG_ORD 와 <paramref name="grants"/> 권한(원본 세션 받아 두기 답).</summary>
    private static PreservedDdl SourceTriggerAndGrants(params string[] grants) =>
        new([SourceTrigger], grants, []) { TriggerObjects = [new DisabledObject(DisabledKind.Trigger, "SRC", "ORDERS", "TRG_ORD")] };

    [Fact]
    public async Task Function_index_of_a_created_table_is_planned_from_the_source_expression_and_previewed_after_the_create_table()
    {
        var p = new Fixture(Dbms.Oracle, Dbms.Oracle, WithFunctionIndex(Table("SRC", IdColumn(), NameColumn())), target: null, createMissing: true,
            companions: FunctionIndexAnswer());

        var report = await p.RunAsync();

        Assert.False(report.HasErrors);
        var plan = Assert.Single(report.Order);
        Assert.Equal([FunctionIndexDdl], plan.Companions.FunctionIndexes);
        Assert.Empty(plan.Companions.Triggers);
        Assert.Empty(plan.Companions.Grants);
        Assert.Empty(plan.Companions.OuterForeignKeys);

        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.CompanionObjects);
        Assert.Equal(CheckLevel.Pass, item.Level);
        Assert.Equal("ORDERS", item.Table);
        Assert.Equal("ORDERS: 적재가 끝난 뒤 함께 만듭니다: 함수 기반 인덱스 IX_ORD_UPNAME." + CompanionTail, item.Message);

        // CREATE TABLE(과 적재 뒤 키)에는 없고, 미리 보기에 CREATE TABLE 뒤 대상 문장으로 한 번 들어 있다.
        Assert.DoesNotContain("UPPER", CreateDdl(report), StringComparison.Ordinal);
        var texts = report.Preview.Select(x => x.Sql).ToList();
        Assert.Equal(PreviewSide.Target, Assert.Single(report.Preview, x => x.Sql == FunctionIndexDdl).Side);
        Assert.True(texts.FindIndex(t => t.StartsWith("CREATE TABLE", StringComparison.Ordinal)) < texts.IndexOf(FunctionIndexDdl));
    }

    [Fact]
    public async Task Function_index_on_a_column_the_cross_copy_does_not_create_is_skipped_with_a_confirm_item()
    {
        // 가상 열은 교차 복사에서 만들지 않는다: 그 열을 쓰는 식의 인덱스는 대상에 만들 수 없다. 같은 DBMS 는 가상 열을 만들므로 인덱스도 만든다(대조).
        var source = WithFunctionIndex(Table("SRC", IdColumn(), NameColumn(),
            new ColumnInfo("UP_NAME", 3, "VARCHAR2") { Length = 20, IsVirtual = true, DefaultValue = "UPPER(\"NAME\")" }));
        var answers = FunctionIndexAnswer("LOWER(\"UP_NAME\")");

        var cross = await new Fixture(Dbms.Oracle, Dbms.Tibero, source, target: null, createMissing: true, companions: answers).RunAsync();
        var same = await new Fixture(Dbms.Oracle, Dbms.Oracle, source, target: null, createMissing: true, companions: answers).RunAsync();

        var plan = Assert.Single(cross.Order);
        Assert.True(plan.Companions.IsEmpty);
        var item = Assert.Single(cross.Items, i => i.Code == CopyCheckCodes.CompanionObjects);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Equal("ORDERS: 적재가 끝난 뒤 함께 만듭니다: 없음. 만들지 않는 것: 함수 기반 인덱스 IX_ORD_UPNAME(대상에 만들지 않거나 LOB 로 옮기는 열 UP_NAME 을 씀)." + CompanionTail,
            item.Message);
        Assert.DoesNotContain(cross.Preview, x => x.Sql.Contains("IX_ORD_UPNAME", StringComparison.Ordinal));

        Assert.Equal(["CREATE INDEX \"DST\".\"IX_ORD_UPNAME\" ON \"DST\".\"ORDERS\" (LOWER(\"UP_NAME\"))"], Assert.Single(same.Order).Companions.FunctionIndexes);
        Assert.Equal(CheckLevel.Pass, Assert.Single(same.Items, i => i.Code == CopyCheckCodes.CompanionObjects).Level);
    }

    [Fact]
    public async Task Unreadable_function_index_expression_is_skipped_with_a_confirm_item()
    {
        var report = await new Fixture(Dbms.Oracle, Dbms.Oracle, WithFunctionIndex(Table("SRC", IdColumn(), NameColumn())), target: null, createMissing: true,
            companions: new CompanionAnswers()).RunAsync();

        Assert.True(Assert.Single(report.Order).Companions.IsEmpty);
        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.CompanionObjects);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Contains("만들지 않는 것: 함수 기반 인덱스 IX_ORD_UPNAME(식을 읽지 못함)", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Source_triggers_and_grants_of_a_created_table_are_read_on_the_source_session_and_reowned_to_the_target()
    {
        const string OffTrigger = "CREATE OR REPLACE TRIGGER \"SRC\".\"TRG_OFF\" AFTER UPDATE ON \"SRC\".\"ORDERS\" FOR EACH ROW BEGIN NULL; END;";
        var on = new DisabledObject(DisabledKind.Trigger, "SRC", "ORDERS", "TRG_ORD");
        var off = new DisabledObject(DisabledKind.Trigger, "SRC", "ORDERS", "TRG_OFF");
        var captured = new PreservedDdl(
            [SourceTrigger, OffTrigger, "ALTER TRIGGER \"SRC\".\"TRG_OFF\" DISABLE"],
            ["GRANT SELECT ON \"SRC\".\"ORDERS\" TO \"APP_USER\"", "GRANT SELECT ON \"SRC\".\"ORDERS\" TO PUBLIC",
             "GRANT UPDATE ON \"SRC\".\"ORDERS\" TO \"APP_ROLE\" WITH GRANT OPTION"],
            [])
        {
            TriggerObjects = [on, off, off], // 꺼져 있던 트리거는 생성 문장 뒤 DISABLE 이 같은 트리거로 따른다
        };
        var p = new Fixture(Dbms.Oracle, Dbms.Oracle, Table("SRC", IdColumn(), NameColumn()), target: null, createMissing: true,
            companions: new CompanionAnswers { Captured = captured, TargetPrivileges = ["CREATE TRIGGER"], TargetUsers = ["APP_USER"] });

        var report = await p.RunAsync();

        Assert.False(report.HasErrors);
        Assert.Same(p.Source, Assert.Single(p.SourceRecreate.Captured)); // 원본 세션에서 원본 벌의 받아 두기로 읽는다

        var companions = Assert.Single(report.Order).Companions;
        Assert.Equal(
        [
            TargetTrigger,
            "CREATE OR REPLACE TRIGGER \"DST\".\"TRG_OFF\" AFTER UPDATE ON \"DST\".\"ORDERS\" FOR EACH ROW BEGIN NULL; END;",
            "ALTER TRIGGER \"DST\".\"TRG_OFF\" DISABLE",
        ], companions.Triggers);
        var onTarget = new DisabledObject(DisabledKind.Trigger, "DST", "ORDERS", "TRG_ORD");
        var offTarget = new DisabledObject(DisabledKind.Trigger, "DST", "ORDERS", "TRG_OFF");
        Assert.Equal([onTarget, offTarget, offTarget], companions.TriggerObjects);
        Assert.Equal(
        [
            "GRANT SELECT ON \"DST\".\"ORDERS\" TO \"APP_USER\"",
            "GRANT SELECT ON \"DST\".\"ORDERS\" TO PUBLIC",
            "GRANT UPDATE ON \"DST\".\"ORDERS\" TO \"APP_ROLE\" WITH GRANT OPTION",
        ], companions.Grants);

        // 사용자로 확인되지 않는 받는 쪽(롤일 수 있다)도 빼지 않고 붙여 본다: 통과 항목에 그 뜻을 적는다.
        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.CompanionObjects);
        Assert.Equal(CheckLevel.Pass, item.Level);
        Assert.Equal("ORDERS: 적재가 끝난 뒤 함께 만듭니다: 트리거 TRG_ORD, 트리거 TRG_OFF, 권한 → APP_USER, 권한 → PUBLIC, "
                     + "권한 → APP_ROLE(대상에서 사용자로 확인되지 않음: 롤이면 붙고, 없으면 실행 때 알림)." + CompanionTail, item.Message);

        // 미리 보기에 실행 순서대로 들어 있다(실행기가 같은 글을 보낸다).
        var statements = companions.Statements.ToList();
        Assert.Equal(statements, report.Preview.Select(x => x.Sql).Where(statements.Contains));
    }

    [Fact]
    public async Task Grant_to_the_target_owner_itself_is_skipped_with_a_confirm_item()
    {
        var p = new Fixture(Dbms.Oracle, Dbms.Oracle, Table("SRC", IdColumn(), NameColumn()), target: null, createMissing: true,
            companions: new CompanionAnswers
            {
                Captured = new PreservedDdl([], ["GRANT SELECT ON \"SRC\".\"ORDERS\" TO \"DST\"", "GRANT SELECT ON \"SRC\".\"ORDERS\" TO \"APP_USER\""], []),
                TargetUsers = ["APP_USER", "DST"],
            });

        var report = await p.RunAsync();

        Assert.Equal(["GRANT SELECT ON \"DST\".\"ORDERS\" TO \"APP_USER\""], Assert.Single(report.Order).Companions.Grants);
        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.CompanionObjects);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Equal("ORDERS: 적재가 끝난 뒤 함께 만듭니다: 권한 → APP_USER. 만들지 않는 것: 권한 GRANT SELECT ON \"SRC\".\"ORDERS\" TO \"DST\"(대상 소유자 자신)."
                     + CompanionTail, item.Message);
        Assert.DoesNotContain(report.Preview, x => x.Sql.Contains("TO \"DST\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recreate_takes_the_function_index_but_not_the_source_triggers_and_grants()
    {
        // 「다시 만들기」는 대상 쪽 트리거·권한을 받아 두었다 되살린다: 원본 것은 읽지도 않는다. 함수 기반 인덱스는 만든다.
        var answers = FunctionIndexAnswer() with
        {
            Captured = SourceTriggerAndGrants("GRANT SELECT ON \"SRC\".\"ORDERS\" TO \"APP_USER\""),
            TargetPrivileges = ["CREATE TRIGGER"],
            TargetUsers = ["APP_USER"],
        };
        var p = new Fixture(Dbms.Oracle, Dbms.Oracle, WithFunctionIndex(Table("SRC", IdColumn(), NameColumn())), target: Table("DST", IdColumn(), NameColumn()),
            action: TargetAction.Recreate, companions: answers);

        var report = await p.RunAsync();

        Assert.DoesNotContain(report.Items, i => i.Code == "RecreateBlocked");
        Assert.Empty(p.SourceRecreate.Captured);
        var plan = Assert.Single(report.Order);
        Assert.False(plan.CreateMissing);
        Assert.Equal([FunctionIndexDdl], plan.Companions.FunctionIndexes);
        Assert.Empty(plan.Companions.Triggers);
        Assert.Empty(plan.Companions.TriggerObjects);
        Assert.Empty(plan.Companions.Grants);
        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.CompanionObjects);
        Assert.Equal(CheckLevel.Pass, item.Level);
        Assert.DoesNotContain("트리거", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("권한", item.Message, StringComparison.Ordinal);
        Assert.Contains(report.Preview, x => x.Sql == FunctionIndexDdl);
    }

    [Fact]
    public async Task Outer_parent_foreign_key_is_planned_only_when_the_target_owner_has_the_parent_table()
    {
        var source = Table("SRC", IdColumn(), NameColumn(), new ColumnInfo("CUST_ID", 3, "NUMBER") { Precision = 10, Scale = 0 }) with
        {
            Constraints =
            [
                new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ID"]),
                new ConstraintInfo("FK_ORD_CUST", ConstraintKind.ForeignKey, ["CUST_ID"]) { ReferencedTable = new DbObjectRef("SRC", "CUSTOMERS", "TABLE") },
            ],
        };
        var answers = new CompanionAnswers { ForeignKeys = [("CUSTOMERS", OuterForeignKey, "FK_ORD_CUST")] };

        var has = new Fixture(Dbms.Oracle, Dbms.Oracle, source, target: null, createMissing: true, companions: answers with { TargetTables = ["CUSTOMERS"] });
        var hasReport = await has.RunAsync();
        var missing = new Fixture(Dbms.Oracle, Dbms.Oracle, source, target: null, createMissing: true, companions: answers);
        var missingReport = await missing.RunAsync();

        // 부모가 대상에 있다: 원본 FK 문장을 그 부모로 읽어 적재 뒤에 붙인다. CREATE TABLE 에는 넣지 않는다(작업 밖 부모).
        Assert.False(hasReport.HasErrors);
        var fk = Assert.Single(Assert.Single(hasReport.Order).Companions.OuterForeignKeys);
        Assert.Equal(("CUSTOMERS", OuterForeignKey, "FK_ORD_CUST"), (fk.Parent, fk.Ddl, fk.Name));
        Assert.Equal("CUSTOMERS", Assert.Single(Assert.Single(has.SourceRecreate.ForeignKeyParents)));
        var item = Assert.Single(hasReport.Items, i => i.Code == CopyCheckCodes.CompanionObjects);
        Assert.Equal(CheckLevel.Pass, item.Level);
        Assert.Equal("ORDERS: 적재가 끝난 뒤 함께 만듭니다: FK FK_ORD_CUST(→CUSTOMERS, 작업 밖 부모)." + CompanionTail, item.Message);
        Assert.DoesNotContain("FOREIGN KEY", CreateDdl(hasReport), StringComparison.Ordinal);
        Assert.Contains(hasReport.Preview, x => x.Sql == OuterForeignKey);

        // 부모가 대상에 없다: FK 문장을 읽지도 않고 뺀다(확인 필요).
        Assert.True(Assert.Single(missingReport.Order).Companions.IsEmpty);
        Assert.Empty(missing.SourceRecreate.ForeignKeyParents);
        var skipped = Assert.Single(missingReport.Items, i => i.Code == CopyCheckCodes.CompanionObjects);
        Assert.Equal(CheckLevel.Confirm, skipped.Level);
        Assert.Contains("만들지 않는 것: FK FK_ORD_CUST(→CUSTOMERS: 대상 DST 에 부모 테이블 없음)", skipped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(missingReport.Preview, x => x.Sql.Contains("FK_ORD_CUST", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("CREATE TRIGGER", true)]
    [InlineData("CREATE ANY TRIGGER", true)]
    public async Task Source_trigger_is_skipped_without_the_create_trigger_privilege_but_the_grant_is_still_planned(string? privilege, bool made)
    {
        var p = new Fixture(Dbms.Oracle, Dbms.Oracle, Table("SRC", IdColumn(), NameColumn()), target: null, createMissing: true,
            companions: new CompanionAnswers
            {
                Captured = SourceTriggerAndGrants("GRANT SELECT ON \"SRC\".\"ORDERS\" TO \"APP_USER\""),
                TargetPrivileges = privilege is null ? [] : new[] { privilege },
                TargetUsers = ["APP_USER"],
            });

        var report = await p.RunAsync();

        var companions = Assert.Single(report.Order).Companions;
        Assert.Equal(["GRANT SELECT ON \"DST\".\"ORDERS\" TO \"APP_USER\""], companions.Grants);
        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.CompanionObjects);
        if (made)
        {
            Assert.Equal([TargetTrigger], companions.Triggers);
            Assert.Equal(CheckLevel.Pass, item.Level);
            Assert.Contains("트리거 TRG_ORD, 권한 → APP_USER.", item.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(companions.Triggers);
            Assert.Empty(companions.TriggerObjects);
            Assert.Equal(CheckLevel.Confirm, item.Level);
            Assert.Equal("ORDERS: 적재가 끝난 뒤 함께 만듭니다: 권한 → APP_USER. 만들지 않는 것: 트리거 TRG_ORD(CREATE TRIGGER 권한 없음)." + CompanionTail, item.Message);
            Assert.DoesNotContain(report.Preview, x => x.Sql.Contains("TRIGGER", StringComparison.Ordinal));
        }
    }

    // ---- 도우미 ----

    private static TableInfo Table(string owner, params ColumnInfo[] columns) =>
        new(new DbObjectRef(owner, "ORDERS", "TABLE"), columns)
        {
            Constraints = [new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ID"])],
        };

    /// <summary>미리 보기에서 대상에 만드는 표의 CREATE TABLE 문장.</summary>
    private static string CreateDdl(PrecheckReport report)
        => Assert.Single(report.Preview, x => x.Side == PreviewSide.Target && x.Sql.StartsWith("CREATE TABLE", StringComparison.Ordinal)).Sql;

    private static CopyDbmsParts Parts(Dbms dbms, IBulkValueCodec codec, FakeRecreateStep? recreate = null) =>
        new(dbms, new FakeCatalog(), new FakeClassifier(dbms), new FakeDialect(), new FakePrecheckDialect(), codec, recreate ?? new FakeRecreateStep());

    private sealed class Fixture
    {
        public Fixture(Dbms sourceDbms, Dbms targetDbms, TableInfo source, TableInfo? target, string[]? unsupportedBySource = null,
                       string[]? unsupportedByTarget = null, string[]? crossWritableByTarget = null, bool createMissing = false, string[]? targetSequences = null, string? targetVersion = null,
                       SequenceAnswers? sequences = null, TargetAction action = TargetAction.DeleteAllInsert, CompanionAnswers? companions = null,
                       IReadOnlyList<SequenceRule>? sequenceRules = null)
        {
            Job = new CopyJob
            {
                Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
                SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==",
                Tables = [new TableRule("ORDERS", RuleKind.All, action) { CreateIfMissing = createMissing }],
                Sequences = sequenceRules ?? [],
            };
            Source = new FakeSession(TestData.Profile(readOnly: true), TestData.Server()) { DbKey = "SRCDB/PDB1/SRC" };
            Target = new FakeSession(TestData.Profile(ConnectionEnvironment.Development) with { Name = "dev" }, targetVersion is null ? TestData.Server() : TestData.Server() with { Version = targetVersion }) { DbKey = "DSTDB/PDB1/DST" };
            var metadata = new FakeMetadataProvider();
            metadata.Tables[new DbObjectRef("SRC", "ORDERS", "TABLE")] = source;
            if (target is not null)
            {
                metadata.Tables[new DbObjectRef("DST", "ORDERS", "TABLE")] = target;
            }

            SourceRecreate = new FakeRecreateStep(companions);
            Service = new CopyPrecheckService(new FakePrecheckBulk(Source, Target, target is not null, targetSequences ?? [], sequences, companions), metadata, new AllowPolicy(),
                Parts(sourceDbms, new FakeCodec(unsupportedBySource ?? []), SourceRecreate), Parts(targetDbms, new FakeCodec(unsupportedByTarget ?? [], crossWritableByTarget ?? [])));
        }

        public CopyJob Job { get; }

        public FakeSession Source { get; }

        public FakeSession Target { get; }

        /// <summary>원본 벌의 받아 두기(SQ2: 원본 트리거·권한·인덱스 식·작업 밖 FK 를 이것으로 읽는다).</summary>
        public FakeRecreateStep SourceRecreate { get; }

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
    /// <summary>SQ1 시퀀스 점검 답(없으면 지금까지처럼 CREATE TABLE 권한만, 원본 시퀀스 속성 없음).</summary>
    private sealed record SequenceAnswers
    {
        /// <summary>대상 세션 시스템 권한에 더할 것(예 CREATE SEQUENCE).</summary>
        public string[] TargetPrivileges { get; init; } = [];

        /// <summary>원본 시퀀스 이름 → copy.sequence_attrs 행.</summary>
        public Dictionary<string, object?[]> SourceAttributes { get; init; } = [];

        /// <summary>원본 트리거가 쓰는 시퀀스(copy.trigger_sequences 행: 소유자, 이름).</summary>
        public (string Owner, string Name)[] TriggerSequences { get; init; } = [];

        /// <summary>원본 SELECT MAX(…) 답.</summary>
        public long? SourceMax { get; init; }

        /// <summary>대상 copy.sequence_info 답(LAST_NUMBER).</summary>
        public long? TargetLastNumber { get; init; }
    }

    /// <summary>SQ2 딸린 객체 점검 답(없으면 원본에 트리거·권한·인덱스 식·작업 밖 FK 가 없다).</summary>
    private sealed record CompanionAnswers
    {
        /// <summary>대상 세션 시스템 권한에 더할 것(예 CREATE TRIGGER).</summary>
        public string[] TargetPrivileges { get; init; } = [];

        /// <summary>원본 세션 받아 두기(CaptureAsync) 답: 원본 트리거·권한.</summary>
        public PreservedDdl Captured { get; init; } = PreservedDdl.Empty;

        /// <summary>원본 인덱스 이름 → IndexColumnsAsync 답(없으면 읽지 못함 = null).</summary>
        public Dictionary<string, IReadOnlyList<IndexColumnDetail>> IndexColumns { get; init; } = [];

        /// <summary>원본 FK 문장(SourceForeignKeysAsync 답, 넘어온 부모 이름으로 거른다).</summary>
        public (string Parent, string Ddl, string? Name)[] ForeignKeys { get; init; } = [];

        /// <summary>copy.target_is_table 이 1 을 답하는 작업 밖 표(대상 DST 의 부모 후보). 작업 표 ORDERS 는 그대로 targetHasTable 로 답한다.</summary>
        public string[] TargetTables { get; init; } = [];

        /// <summary>copy.user_exists 가 1 을 답하는 대상 사용자.</summary>
        public string[] TargetUsers { get; init; } = [];
    }

    private sealed class FakePrecheckBulk(ISession source, ISession target, bool targetHasTable = true, string[]? targetSequences = null, SequenceAnswers? sequences = null,
                                          CompanionAnswers? companions = null) : IBulkExecutionService
    {
        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var isTarget = ReferenceEquals(s, target);
            object?[][] rows = query.Text switch
            {
                "copy.session_user" => [[isTarget ? "DST" : "SRC"]],
                "copy.target_is_table" => [[IsTable(binds) ? 1L : 0L]],
                "copy.user_exists" when isTarget => [[binds.TryGetValue("NAME", out var user) && (companions?.TargetUsers ?? []).Contains(user.Value as string) ? 1L : 0L]],
                "copy.session_privs" when isTarget => [["CREATE TABLE"], .. (sequences?.TargetPrivileges ?? []).Concat(companions?.TargetPrivileges ?? []).Select(p => new object?[] { p })],
                "copy.sequence_attrs" when !isTarget => binds.TryGetValue("SEQ", out var seq) && sequences?.SourceAttributes.GetValueOrDefault((string)seq.Value!) is { } attrs ? [attrs] : [],
                "copy.trigger_sequences" when !isTarget => [.. (sequences?.TriggerSequences ?? []).Select(x => new object?[] { x.Owner, x.Name })],
                "copy.sequence_info" when isTarget && sequences?.TargetLastNumber is { } last => [[last, 1L]],
                _ when !isTarget && query.Text.StartsWith("SELECT MAX(", StringComparison.Ordinal) => [[sequences?.SourceMax]],
                "copy.sequence_names" when isTarget => [.. (targetSequences ?? []).Select(n => new object?[] { n })],
                // 기본값이 가리키는 객체 확인(만들기 차단 검사): 대상 시퀀스만 있다. 표 이름 자체(이름 겹침 검사)는 없다.
                "copy.object_exists" when isTarget => [[binds.TryGetValue("NAME", out var name) && (targetSequences ?? []).Contains(name.Value as string) ? 1L : 0L]],
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

        /// <summary>작업 표(ORDERS)는 targetHasTable, 다른 표(작업 밖 부모)는 <see cref="CompanionAnswers.TargetTables"/> 에 있으면 표다.</summary>
        private bool IsTable(IReadOnlyDictionary<string, BindValue> binds)
            => binds.TryGetValue("TBL", out var table) && table.Value is string name && name != "ORDERS"
                ? (companions?.TargetTables ?? []).Contains(name)
                : targetHasTable;
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

    /// <summary>받아 두기. <paramref name="answers"/> 를 주면 원본 트리거·권한·인덱스 식·FK 를 그 답으로 돌려주고, 불린 것을 남긴다.</summary>
    private sealed class FakeRecreateStep(CompanionAnswers? answers = null) : ICopyRecreateStep
    {
        /// <summary>CaptureAsync 를 부른 세션(부른 순서).</summary>
        public List<ISession> Captured { get; } = [];

        /// <summary>SourceForeignKeysAsync 에 넘어온 부모 이름 집합(부른 순서).</summary>
        public List<IReadOnlySet<string>> ForeignKeyParents { get; } = [];

        public Task<PreservedDdl> CaptureAsync(ISession target, string owner, string table, CancellationToken ct)
        {
            Captured.Add(target);
            return Task.FromResult(answers?.Captured ?? PreservedDdl.Empty); // 원본 트리거·권한(SQ2)도 이것으로 읽는다
        }

        public Task<IReadOnlyList<(string Parent, string Ddl, string? Name)>> SourceForeignKeysAsync(ISession source, string sourceOwner, string table,
            IReadOnlySet<string> parents, string targetOwner, CancellationToken ct)
        {
            ForeignKeyParents.Add(parents);
            return Task.FromResult<IReadOnlyList<(string Parent, string Ddl, string? Name)>>([.. (answers?.ForeignKeys ?? []).Where(f => parents.Contains(f.Parent))]);
        }

        public Task<IReadOnlyList<IndexColumnDetail>?> IndexColumnsAsync(ISession source, DbObjectRef table, string indexName, CancellationToken ct)
            => Task.FromResult(answers?.IndexColumns.GetValueOrDefault(indexName));

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

        // 미리 보기가 시퀀스 보정 연결마다 NEXTVAL 문장을 보인다(SQ3 시험이 연결을 쓴다).
        public SqlStatement NextValues(string owner, string sequence) =>
            CopyCommonStatements.Query($"SELECT \"{owner}\".\"{sequence}\".NEXTVAL FROM DUAL CONNECT BY LEVEL <= :{CopyCommonStatements.NextValuesBind}");

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
