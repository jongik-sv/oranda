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
        var sent = CrossTableMap.Build(sourceDbms, targetDbms, plan.Source, "DST", new HashSet<string>(StringComparer.Ordinal)).Statements;
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

    [Fact]
    public async Task Same_dbms_create_with_a_missing_sequence_is_unchanged_and_still_blocked()
    {
        var source = Table("SRC", new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false, DefaultValue = "\"SRC\".\"SEQ_ORD\".\"NEXTVAL\"" });

        var report = await new Fixture(Dbms.Oracle, Dbms.Oracle, source, target: null, createMissing: true, targetSequences: ["OTHER_SEQ"]).RunAsync();

        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.SequenceDefaultDropped);
        Assert.Contains(report.Items, i => i.Code == CopyCheckCodes.CreateBlocked && i.Level == CheckLevel.Error);
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

    // ---- 도우미 ----

    private static TableInfo Table(string owner, params ColumnInfo[] columns) =>
        new(new DbObjectRef(owner, "ORDERS", "TABLE"), columns)
        {
            Constraints = [new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ID"])],
        };

    /// <summary>미리 보기에서 대상에 만드는 표의 CREATE TABLE 문장.</summary>
    private static string CreateDdl(PrecheckReport report)
        => Assert.Single(report.Preview, x => x.Side == PreviewSide.Target && x.Sql.StartsWith("CREATE TABLE", StringComparison.Ordinal)).Sql;

    private static CopyDbmsParts Parts(Dbms dbms, IBulkValueCodec codec) =>
        new(dbms, new FakeCatalog(), new FakeClassifier(dbms), new FakeDialect(), new FakePrecheckDialect(), codec, new FakeRecreateStep());

    private sealed class Fixture
    {
        public Fixture(Dbms sourceDbms, Dbms targetDbms, TableInfo source, TableInfo? target, string[]? unsupportedBySource = null,
                       string[]? unsupportedByTarget = null, string[]? crossWritableByTarget = null, bool createMissing = false, string[]? targetSequences = null, string? targetVersion = null)
        {
            Job = new CopyJob
            {
                Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
                SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==",
                Tables = [new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert) { CreateIfMissing = createMissing }],
            };
            Source = new FakeSession(TestData.Profile(readOnly: true), TestData.Server()) { DbKey = "SRCDB/PDB1/SRC" };
            Target = new FakeSession(TestData.Profile(ConnectionEnvironment.Development) with { Name = "dev" }, targetVersion is null ? TestData.Server() : TestData.Server() with { Version = targetVersion }) { DbKey = "DSTDB/PDB1/DST" };
            var metadata = new FakeMetadataProvider();
            metadata.Tables[new DbObjectRef("SRC", "ORDERS", "TABLE")] = source;
            if (target is not null)
            {
                metadata.Tables[new DbObjectRef("DST", "ORDERS", "TABLE")] = target;
            }

            Service = new CopyPrecheckService(new FakePrecheckBulk(Source, Target, target is not null, targetSequences ?? []), metadata, new AllowPolicy(),
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
    private sealed class FakePrecheckBulk(ISession source, ISession target, bool targetHasTable = true, string[]? targetSequences = null) : IBulkExecutionService
    {
        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var isTarget = ReferenceEquals(s, target);
            object?[][] rows = query.Text switch
            {
                "copy.session_user" => [[isTarget ? "DST" : "SRC"]],
                "copy.target_is_table" => [[targetHasTable ? 1L : 0L]],
                "copy.session_privs" when isTarget => [["CREATE TABLE"]],
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
