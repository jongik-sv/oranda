#pragma warning disable CA1861 // 시험 입력용 상수 배열
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions;

/// <summary>파티션 차이·문장(설계 18.4, TE-40). 예시 테이블 = SAMPLE.ORDERS.</summary>
public class PartitionScriptBuilderTests
{
    private static readonly OracleLike Oracle = new();

    private static readonly TiberoLike Tibero = new();

    private static PartitionScheme RangeScheme(string? interval = null) => new(PartitionMethod.Range, ["ORDER_DATE"]) { Interval = interval };

    private static PartitionDraft Row(string key, string name, string? bound = null) => new(key, name) { Bound = bound };

    /// <summary>원본: P_2026_08..10(월 범위) — 시안 ⑥ 의 꼴.</summary>
    private static PartitionTableDraft Monthly() => new(RangeScheme("NUMTOYMINTERVAL(1, 'MONTH')"),
    [
        Row("p1", "P_2026_08", "DATE '2026-09-01'"),
        Row("p2", "P_2026_09", "DATE '2026-10-01'"),
        Row("p3", "P_2026_10", "DATE '2026-11-01'"),
    ]);

    private static PartitionTableDraft With(PartitionTableDraft d, params PartitionDraft[] rows) => d with { Partitions = rows };

    private static List<string> Texts(PartitionTableDraft original, PartitionTableDraft edited, ITableEditDialect? dialect = null, IReadOnlyList<string>? global = null) =>
        [.. PartitionScriptBuilder.Build("SAMPLE", "ORDERS", original, edited, dialect ?? Oracle, null, global).Select(s => s.Statement.Text)];

    [Fact]
    public void Untouched_partitions_produce_no_statements()
    {
        var original = Monthly();

        Assert.True(PartitionDiff.Compute(original, original, Oracle).IsEmpty);
        Assert.Empty(Texts(original, original));
        Assert.Empty(Texts(PartitionTableDraft.None, PartitionTableDraft.None));
    }

    /// <summary>서버 표기와 공백·대소문자만 다른 경계·INTERVAL 은 같다(되돌리기 = 0문장).</summary>
    [Fact]
    public void Bounds_and_interval_compare_ignoring_case_and_spacing_outside_literals()
    {
        var original = Monthly();
        var retyped = With(original with { Scheme = RangeScheme("numtoyminterval(1,'MONTH')") },
            Row("p1", "P_2026_08", "date  '2026-09-01'"), Row("p2", "P_2026_09", "DATE '2026-10-01'"), Row("p3", "P_2026_10", " DATE '2026-11-01' "));

        Assert.True(PartitionDiff.Compute(original, retyped, Oracle).IsEmpty);
        var literal = With(original, Row("p1", "P_2026_08", "DATE '2026-09-01'"), Row("p2", "P_2026_09", "DATE '2026-10-01'"), Row("p3", "P_2026_10", "date '2026-11-02'"));
        Assert.Contains(PartitionDiff.Compute(original, literal, Oracle).Problems, p => p.ReasonKey == PartitionDiff.BoundChangeKey);
    }

    /// <summary>시안 ⑥: 마지막 뒤에 줄 추가 = ADD, 줄 지우기 = DROP … UPDATE INDEXES(⚠ 되돌릴 수 없음). INTERVAL 이 없는 테이블.</summary>
    [Fact]
    public void Add_after_the_last_bound_and_drop_follow_the_mockup()
    {
        var original = Monthly() with { Scheme = RangeScheme() };
        var edited = With(original,
            Row("p1", "P_2026_08", "DATE '2026-09-01'") with { IsDropped = true },
            Row("p2", "P_2026_09", "DATE '2026-10-01'"),
            Row("p3", "P_2026_10", "DATE '2026-11-01'"),
            Row("n1", "P_2026_11", "DATE '2026-12-01'"));

        var script = PartitionScriptBuilder.Build("SAMPLE", "ORDERS", original, edited, Oracle);

        Assert.Equal(
        [
            "ALTER TABLE SAMPLE.ORDERS DROP PARTITION P_2026_08 UPDATE INDEXES",
            "ALTER TABLE SAMPLE.ORDERS ADD PARTITION P_2026_11 VALUES LESS THAN (DATE '2026-12-01')",
        ], script.Select(s => s.Statement.Text));
        Assert.All(script, s => Assert.Equal((StatementKind.Ddl, TableEditPage.Partitions), (s.Statement.Kind, s.Page)));
        Assert.Equal([TableEditMarks.Irreversible, TableEditMarks.None], script.Select(s => s.Marks));
        Assert.Equal(["p1", "n1"], script.Select(s => s.RowKey));
    }

    [Fact]
    public void Rename_and_truncate_use_the_original_name()
    {
        var original = Monthly();
        var edited = With(original,
            Row("p1", "P_2026_08", "DATE '2026-09-01'") with { IsTruncated = true },
            Row("p2", "P_SEP", "DATE '2026-10-01'"),
            Row("p3", "P_2026_10", "DATE '2026-11-01'"));

        Assert.Equal(
        [
            "ALTER TABLE SAMPLE.ORDERS TRUNCATE PARTITION P_2026_08 UPDATE INDEXES",
            "ALTER TABLE SAMPLE.ORDERS RENAME PARTITION P_2026_09 TO P_SEP",
        ], Texts(original, edited));
    }

    /// <summary>범위 줄을 원본 사이에 넣으면 바로 다음 파티션을 나눈다. 둘 넣으면 낮은 경계부터, 남는 쪽 이름 바꾸기는 마지막 INTO 로 흡수.</summary>
    [Fact]
    public void Range_rows_inserted_between_bounds_split_the_next_partition()
    {
        var original = Monthly();
        var edited = With(original,
            Row("p1", "P_2026_08", "DATE '2026-09-01'"),
            Row("n1", "P_2026_09_A", "DATE '2026-09-11'"),
            Row("n2", "P_2026_09_B", "DATE '2026-09-21'") with { Tablespace = "TS_ARCHIVE" },
            Row("p2", "P_2026_09_C", "DATE '2026-10-01'"),
            Row("p3", "P_2026_10", "DATE '2026-11-01'"));

        Assert.Equal(
        [
            "ALTER TABLE SAMPLE.ORDERS SPLIT PARTITION P_2026_09 AT (DATE '2026-09-11') INTO (PARTITION P_2026_09_A, PARTITION P_2026_09) UPDATE INDEXES",
            "ALTER TABLE SAMPLE.ORDERS SPLIT PARTITION P_2026_09 AT (DATE '2026-09-21') INTO (PARTITION P_2026_09_B TABLESPACE TS_ARCHIVE, PARTITION P_2026_09_C) UPDATE INDEXES",
        ], Texts(original, edited));
        var diff = PartitionDiff.Compute(original, edited, Oracle);
        Assert.Equal(DraftRowState.Modified, diff.Rows.Single(r => r.RowKey == "p2").State); // 이름이 바뀌어도 경계는 그대로라 문제 없음
        Assert.Empty(diff.Problems);
    }

    [Fact]
    public void Merge_two_adjacent_partitions_into_the_surviving_row_name()
    {
        var original = Monthly();
        var edited = With(original,
            Row("p1", "P_2026_08", "DATE '2026-09-01'") with { MergedInto = "p2" },
            Row("p2", "P_2026_Q3", "DATE '2026-10-01'"),
            Row("p3", "P_2026_10", "DATE '2026-11-01'"));

        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS MERGE PARTITIONS P_2026_08, P_2026_09 INTO PARTITION P_2026_Q3 UPDATE INDEXES"], Texts(original, edited));
    }

    /// <summary>셋 합치기: 대상과 가까운 줄부터 두 개씩(붙은 파티션끼리만 — 떨어진 둘을 먼저 합치면 ORA-14274).</summary>
    [Fact]
    public void Merging_three_goes_nearest_first_and_keeps_the_original_name_until_the_last()
    {
        var original = Monthly();
        var edited = With(original,
            Row("p1", "P_2026_08", "DATE '2026-09-01'") with { MergedInto = "p3" },
            Row("p2", "P_2026_09", "DATE '2026-10-01'") with { MergedInto = "p3" },
            Row("p3", "P_2026_Q", "DATE '2026-11-01'"));

        Assert.Equal(
        [
            "ALTER TABLE SAMPLE.ORDERS MERGE PARTITIONS P_2026_09, P_2026_10 INTO PARTITION P_2026_10 UPDATE INDEXES",
            "ALTER TABLE SAMPLE.ORDERS MERGE PARTITIONS P_2026_08, P_2026_10 INTO PARTITION P_2026_Q UPDATE INDEXES",
        ], Texts(original, edited));
    }

    [Fact]
    public void Range_merge_of_non_adjacent_partitions_is_a_problem()
    {
        var original = Monthly();
        var edited = With(original,
            Row("p1", "P_2026_08", "DATE '2026-09-01'") with { MergedInto = "p3" },
            Row("p2", "P_2026_09", "DATE '2026-10-01'"),
            Row("p3", "P_2026_10", "DATE '2026-11-01'"));

        Assert.Equal([("p1", PartitionDiff.BadMergeKey)], PartitionDiff.Compute(original, edited, Oracle).Problems.Select(p => (p.RowKey, p.ReasonKey)));
        Assert.Empty(Texts(original, edited));
    }

    /// <summary>목록: 합칠 대상이 나누기 원본이기도 하고 이름도 바꾸면, 합치기는 원래 이름으로 두고 나누기 끝에 새 이름(없는 이름을 SPLIT 하지 않게).</summary>
    [Fact]
    public void Merge_target_that_is_also_split_and_renamed_tracks_the_current_name()
    {
        var original = new PartitionTableDraft(new PartitionScheme(PartitionMethod.List, ["REGION"]),
            [Row("p1", "P_A", "'A'"), Row("p2", "P_B", "'B', 'C'")]);
        var edited = original with
        {
            Partitions =
            [
                Row("p1", "P_A", "'A'") with { MergedInto = "p2" },
                Row("n1", "P_C", "'C'") with { SplitFrom = "p2" },
                Row("p2", "P_AB", "'A', 'B'"),
            ],
        };

        Assert.Equal(
        [
            "ALTER TABLE SAMPLE.ORDERS MERGE PARTITIONS P_A, P_B INTO PARTITION P_B UPDATE INDEXES",
            "ALTER TABLE SAMPLE.ORDERS SPLIT PARTITION P_B VALUES ('C') INTO (PARTITION P_C, PARTITION P_AB) UPDATE INDEXES",
        ], Texts(original, edited));
    }

    /// <summary>Tibero: 나누기·합치기 뒤 전역 인덱스가 UNUSABLE 로 남아(실측 19) 끝에 REBUILD 를 붙인다. 지우기·추가만이면 붙이지 않는다.</summary>
    [Fact]
    public void Tibero_like_dialect_rebuilds_global_indexes_after_split_or_merge_only()
    {
        var original = Monthly();
        var merged = With(original,
            Row("p1", "P_2026_08", "DATE '2026-09-01'") with { MergedInto = "p2" },
            Row("p2", "P_2026_09", "DATE '2026-10-01'"),
            Row("p3", "P_2026_10", "DATE '2026-11-01'"));
        var dropped = With(original, Row("p1", "P_2026_08", "DATE '2026-09-01'") with { IsDropped = true }, Row("p2", "P_2026_09", "DATE '2026-10-01'"), Row("p3", "P_2026_10", "DATE '2026-11-01'"));

        Assert.Equal(
        [
            "ALTER TABLE SAMPLE.ORDERS MERGE PARTITIONS P_2026_08, P_2026_09 INTO PARTITION P_2026_09 UPDATE INDEXES",
            "ALTER INDEX SAMPLE.ORDERS_IX1 REBUILD",
            "ALTER INDEX SAMPLE.\"Ix 2\" REBUILD",
        ], Texts(original, merged, Tibero, ["ORDERS_IX1", "Ix 2"]));
        Assert.Single(Texts(original, dropped, Tibero, ["ORDERS_IX1"]));
        Assert.Single(Texts(original, merged, Oracle, ["ORDERS_IX1"])); // Oracle 은 UPDATE INDEXES 로 충분
    }

    /// <summary>INTERVAL 이 켜진 채 ADD 는 문제(Oracle ORA-14760). 끄면서 하면 SET INTERVAL () 이 맨 앞, 켜면서 하면 맨 뒤.</summary>
    [Fact]
    public void Interval_off_goes_first_on_goes_last_and_add_with_interval_on_is_a_problem()
    {
        var original = Monthly();
        PartitionDraft[] withNew = [.. original.Partitions, Row("n1", "P_2026_11", "DATE '2026-12-01'")];

        Assert.Equal([("n1", PartitionDiff.AddWithIntervalKey)], PartitionDiff.Compute(original, original with { Partitions = withNew }, Oracle).Problems.Select(p => (p.RowKey, p.ReasonKey)));

        var off = original with { Scheme = RangeScheme(), Partitions = [original.Partitions[0], original.Partitions[1], original.Partitions[2] with { IsDropped = true }, withNew[3]] };
        Assert.Equal(
        [
            "ALTER TABLE SAMPLE.ORDERS SET INTERVAL ()",
            "ALTER TABLE SAMPLE.ORDERS DROP PARTITION P_2026_10 UPDATE INDEXES",
            "ALTER TABLE SAMPLE.ORDERS ADD PARTITION P_2026_11 VALUES LESS THAN (DATE '2026-12-01')",
        ], Texts(original, off));

        var none = original with { Scheme = RangeScheme() };
        var on = original with { Partitions = withNew };
        Assert.Equal(
        [
            "ALTER TABLE SAMPLE.ORDERS ADD PARTITION P_2026_11 VALUES LESS THAN (DATE '2026-12-01')",
            "ALTER TABLE SAMPLE.ORDERS SET INTERVAL (NUMTOYMINTERVAL(1, 'MONTH'))",
        ], Texts(none, on));
    }

    [Fact]
    public void Interval_on_off_and_change()
    {
        var original = Monthly();

        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS SET INTERVAL ()"], Texts(original, original with { Scheme = RangeScheme() }));
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS SET INTERVAL (NUMTODSINTERVAL(7, 'DAY'))"],
            Texts(original, original with { Scheme = RangeScheme("NUMTODSINTERVAL(7, 'DAY')") }));
        var none = original with { Scheme = RangeScheme() };
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS SET INTERVAL (NUMTOYMINTERVAL(1, 'MONTH'))"], Texts(none, original));
    }

    /// <summary>DEFAULT 파티션이 있는 목록: DEFAULT 에서 나누기는 되고, ADD 는 문제(Oracle ORA-14323).</summary>
    [Fact]
    public void List_split_from_default_works_and_add_with_default_is_a_problem()
    {
        var original = new PartitionTableDraft(new PartitionScheme(PartitionMethod.List, ["REGION"]),
            [Row("p1", "P_EAST", "'E'"), Row("p2", "P_OTHER", "DEFAULT")]);
        var edited = original with
        {
            Partitions =
            [
                Row("p1", "P_EAST", "'E'"),
                Row("n1", "P_WEST", "'W', 'NW'") with { SplitFrom = "p2" },
                Row("p2", "P_OTHER", "DEFAULT"),
                Row("n2", "P_SOUTH", "'S'"),
            ],
        };

        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS SPLIT PARTITION P_OTHER VALUES ('W', 'NW') INTO (PARTITION P_WEST, PARTITION P_OTHER) UPDATE INDEXES"], Texts(original, edited));
        Assert.Equal([("n2", PartitionDiff.AddWithDefaultKey)], PartitionDiff.Compute(original, edited, Oracle).Problems.Select(p => (p.RowKey, p.ReasonKey)));

        var noDefault = new PartitionTableDraft(new PartitionScheme(PartitionMethod.List, ["REGION"]), [Row("p1", "P_EAST", "'E'")]);
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS ADD PARTITION P_SOUTH VALUES ('S')"],
            Texts(noDefault, noDefault with { Partitions = [.. noDefault.Partitions, Row("n1", "P_SOUTH", "'S'")] }));
    }

    /// <summary>해시 줄이기는 끝에서부터만(서버가 마지막 파티션을 없앰), 해시 합치기는 없음.</summary>
    [Fact]
    public void Hash_coalesce_only_from_the_end_and_no_hash_merge()
    {
        var original = new PartitionTableDraft(new PartitionScheme(PartitionMethod.Hash, ["ID"]),
            [Row("p1", "SAMPLE_P1"), Row("p2", "SAMPLE_P2"), Row("p3", "SAMPLE_P3")]);

        var middle = original with { Partitions = [original.Partitions[0], original.Partitions[2]] };
        Assert.Equal([("p2", PartitionDiff.HashCoalesceLastKey)], PartitionDiff.Compute(original, middle, Oracle).Problems.Select(p => (p.RowKey, p.ReasonKey)));
        Assert.Empty(Texts(original, middle));

        var merged = original with { Partitions = [original.Partitions[0] with { MergedInto = "p2" }, original.Partitions[1], original.Partitions[2]] };
        Assert.Equal([("p1", PartitionDiff.HashMergeKey)], PartitionDiff.Compute(original, merged, Oracle).Problems.Select(p => (p.RowKey, p.ReasonKey)));
    }

    [Fact]
    public void Hash_add_and_remove_use_add_and_coalesce()
    {
        var original = new PartitionTableDraft(new PartitionScheme(PartitionMethod.Hash, ["ID"]),
            [Row("p1", "SAMPLE_P1"), Row("p2", "SAMPLE_P2"), Row("p3", "SAMPLE_P3")]);
        var added = original with { Partitions = [.. original.Partitions, Row("n1", "P4") with { Tablespace = "TS_DATA" }] };
        var removed = original with { Partitions = [original.Partitions[0], original.Partitions[1]] };

        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS ADD PARTITION P4 TABLESPACE TS_DATA UPDATE INDEXES"], Texts(original, added));
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS COALESCE PARTITION UPDATE INDEXES"], Texts(original, removed));
    }

    /// <summary>방언이 못 하는 문장은 문제로 나오고 문장이 없다(Tibero 실측 11: 해시 ADD·COALESCE).</summary>
    [Fact]
    public void Dialect_unsupported_operations_become_problems_without_statements()
    {
        var original = new PartitionTableDraft(new PartitionScheme(PartitionMethod.Hash, ["ID"]), [Row("p1", "SAMPLE_P1"), Row("p2", "SAMPLE_P2")]);
        var added = original with { Partitions = [.. original.Partitions, Row("n1", "P3")] };
        var removed = original with { Partitions = [original.Partitions[0]] };

        Assert.Empty(Texts(original, added, Tibero));
        Assert.Equal([("n1", TiberoLike.HashKey)], PartitionDiff.Compute(original, added, Tibero).Problems.Select(p => (p.RowKey, p.ReasonKey)));
        Assert.Empty(Texts(original, removed, Tibero));
        Assert.Equal([("p2", TiberoLike.HashKey)], PartitionDiff.Compute(original, removed, Tibero).Problems.Select(p => (p.RowKey, p.ReasonKey)));
    }

    [Fact]
    public void Edits_without_a_statement_are_problems()
    {
        var original = Monthly() with
        {
            Partitions = [Row("p1", "P_2026_08", "DATE '2026-09-01'"), Row("p2", "P_MAX", "MAXVALUE")],
        };

        var afterMax = original with { Partitions = [.. original.Partitions, Row("n1", "P_LATE", "DATE '2027-01-01'")] };
        Assert.Contains(PartitionDiff.Compute(original, afterMax, Oracle).Problems, p => p is { RowKey: "n1", ReasonKey: PartitionDiff.AddAfterMaxValueKey });
        Assert.Empty(Texts(original, afterMax));

        var tablespace = original with { Partitions = [original.Partitions[0] with { Tablespace = "TS_X" }, original.Partitions[1]] };
        Assert.Contains(PartitionDiff.Compute(original, tablespace, Oracle).Problems, p => p.ReasonKey == PartitionDiff.TablespaceChangeKey);

        // 경계를 바꾸면서 이름도 바꾼 줄은 이름 바꾸기도 하지 않는다(반쯤만 반영하지 않음).
        var renamedAndMoved = original with { Partitions = [Row("p1", "P_RENAMED", "DATE '2026-09-15'"), original.Partitions[1]] };
        Assert.Empty(Texts(original, renamedAndMoved));

        var rescheme = original with { Scheme = new PartitionScheme(PartitionMethod.Range, ["ID"]) };
        Assert.Equal([PartitionDiff.SchemeChangeKey], PartitionDiff.Compute(original, rescheme, Oracle).Problems.Select(p => p.ReasonKey));
        Assert.Equal([PartitionDiff.UnpartitionKey], PartitionDiff.Compute(original, PartitionTableDraft.None, Oracle).Problems.Select(p => p.ReasonKey));
    }

    /// <summary>떼어 낼 원본 줄이 문장을 못 내면 새 줄도 문제로(말없이 사라지지 않게).</summary>
    [Fact]
    public void Split_off_row_whose_source_has_a_problem_is_a_problem_too()
    {
        var original = Monthly();
        var edited = With(original,
            Row("p1", "P_2026_08", "DATE '2026-09-01'"),
            Row("n1", "P_2026_09_A", "DATE '2026-09-15'"),
            Row("p2", "P_2026_09", "DATE '2026-10-01'") with { Tablespace = "TS_X" },
            Row("p3", "P_2026_10", "DATE '2026-11-01'"));

        var problems = PartitionDiff.Compute(original, edited, Oracle).Problems.Select(p => (p.RowKey, p.ReasonKey)).ToList();
        Assert.Contains(("p2", PartitionDiff.TablespaceChangeKey), problems);
        Assert.Contains(("n1", PartitionDiff.SplitSourceUnavailableKey), problems);
        Assert.Empty(Texts(original, edited));
    }

    /// <summary>범위 새 줄 뒤의 원본 줄을 지우면 그 줄은 건너뛰고 다음 원본 줄을 나눈다.</summary>
    [Fact]
    public void Split_source_skips_dropped_rows()
    {
        var original = Monthly() with { Scheme = RangeScheme() };
        var edited = With(original,
            Row("p1", "P_2026_08", "DATE '2026-09-01'"),
            Row("n1", "P_2026_09_A", "DATE '2026-09-15'"),
            Row("p2", "P_2026_09", "DATE '2026-10-01'") with { IsDropped = true },
            Row("p3", "P_2026_10", "DATE '2026-11-01'"));

        Assert.Equal(
        [
            "ALTER TABLE SAMPLE.ORDERS DROP PARTITION P_2026_09 UPDATE INDEXES",
            "ALTER TABLE SAMPLE.ORDERS SPLIT PARTITION P_2026_10 AT (DATE '2026-09-15') INTO (PARTITION P_2026_09_A, PARTITION P_2026_10) UPDATE INDEXES",
        ], Texts(original, edited));
    }

    /// <summary>이름 겹침: 맞바꾸기, 남는 이름 둘, 새 줄이 아직 남은 원본 이름을 씀 → 문제, 문장 없음. 지운 줄·먼저 이름을 바꾼 줄의 이름은 다시 써도 된다.</summary>
    [Fact]
    public void Name_collisions_are_problems()
    {
        var original = Monthly() with { Scheme = RangeScheme() };
        var rows = original.Partitions;

        var swap = original with { Partitions = [rows[0] with { Name = "P_2026_09" }, rows[1] with { Name = "P_2026_08" }, rows[2]] };
        Assert.Equal(["p1", "p2"], PartitionDiff.Compute(original, swap, Oracle).Problems.Where(p => p.ReasonKey == PartitionDiff.NameInUseKey).Select(p => p.RowKey));
        Assert.Empty(Texts(original, swap));

        var twice = original with { Partitions = [rows[0], rows[1], rows[2], Row("n1", "P_2026_10", "DATE '2026-12-01'")] };
        Assert.Contains(PartitionDiff.Compute(original, twice, Oracle).Problems, p => p is { RowKey: "n1", ReasonKey: PartitionDiff.DuplicateNameKey });
        Assert.Empty(Texts(original, twice));

        var reuseDropped = original with { Partitions = [rows[0] with { IsDropped = true }, rows[1], rows[2], Row("n1", "P_2026_08", "DATE '2026-12-01'")] };
        Assert.Equal(2, Texts(original, reuseDropped).Count);

        var renameFreed = original with { Partitions = [rows[0] with { Name = "P_OLD" }, rows[1], rows[2], Row("n1", "P_2026_08", "DATE '2026-12-01'")] };
        Assert.Equal(
        [
            "ALTER TABLE SAMPLE.ORDERS RENAME PARTITION P_2026_08 TO P_OLD",
            "ALTER TABLE SAMPLE.ORDERS ADD PARTITION P_2026_08 VALUES LESS THAN (DATE '2026-12-01')",
        ], Texts(original, renameFreed));
    }

    [Fact]
    public void New_rows_marked_dropped_or_merged_are_ignored()
    {
        var original = Monthly() with { Scheme = RangeScheme() };
        var edited = original with { Partitions = [.. original.Partitions, Row("n1", "P_X", "DATE '2027-01-01'") with { IsDropped = true }] };

        Assert.True(PartitionDiff.Compute(original, edited, Oracle).IsEmpty);
    }

    /// <summary>파티션 테이블인데 정보가 없으면(못 읽음·옛 캐시) 편집 모델이 없다 — 「파티션 아님」으로 보고 바꾸기 문장을 내지 않게.</summary>
    [Fact]
    public void From_table_refuses_a_partitioned_table_without_partition_info()
    {
        var table = new TableInfo(new DbObjectRef("SAMPLE", "ORDERS", "TABLE"), []);

        Assert.Same(PartitionTableDraft.None, PartitionTableDraft.FromTable(table));
        Assert.Null(PartitionTableDraft.FromTable(table with { IsPartitioned = true }));
        Assert.NotNull(PartitionTableDraft.FromTable(table with { IsPartitioned = true, Partitioning = new PartitioningInfo("RANGE", ["ID"], []) }));
    }

    [Fact]
    public void Merge_into_a_new_or_dropped_row_is_a_problem()
    {
        var original = Monthly();
        var intoDropped = With(original,
            Row("p1", "P_2026_08", "DATE '2026-09-01'") with { MergedInto = "p2" },
            Row("p2", "P_2026_09", "DATE '2026-10-01'") with { IsDropped = true },
            Row("p3", "P_2026_10", "DATE '2026-11-01'"));

        Assert.Contains(PartitionDiff.Compute(original, intoDropped, Oracle).Problems, p => p is { RowKey: "p1", ReasonKey: PartitionDiff.BadMergeKey });
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS DROP PARTITION P_2026_09 UPDATE INDEXES"], Texts(original, intoDropped));
    }

    /// <summary>일반 → 파티션: Oracle 12.2+ 는 한 문장, 그 아래·Tibero 는 문제(다시 만들기 방식으로, TE-41).</summary>
    [Fact]
    public void Conversion_is_one_online_statement_on_oracle_12_2_and_a_rebuild_problem_otherwise()
    {
        var edited = new PartitionTableDraft(new PartitionScheme(PartitionMethod.Range, ["ID"]),
            [Row("n1", "P_LOW", "1000"), Row("n2", "P_MAX", "MAXVALUE")]);

        var on19 = PartitionScriptBuilder.Build("SAMPLE", "ORDERS", PartitionTableDraft.None, edited, Oracle, Server(Dbms.Oracle, "19.0.0.0.0"));
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS MODIFY PARTITION BY RANGE (ID) (PARTITION P_LOW VALUES LESS THAN (1000), PARTITION P_MAX VALUES LESS THAN (MAXVALUE)) ONLINE"],
            on19.Select(s => s.Statement.Text));
        Assert.Empty(PartitionScriptBuilder.Build("SAMPLE", "ORDERS", PartitionTableDraft.None, edited, Oracle, Server(Dbms.Oracle, "12.1.0.2.0")));
        Assert.Equal([PartitionDiff.ConvertNeedsRebuildKey],
            PartitionDiff.Compute(PartitionTableDraft.None, edited, Oracle, Server(Dbms.Oracle, "11.2.0.4.0")).Problems.Select(p => p.ReasonKey));
        Assert.Equal([TiberoLike.ConvertKey], PartitionDiff.Compute(PartitionTableDraft.None, edited, Tibero, Server(Dbms.Tibero, "6")).Problems.Select(p => p.ReasonKey));
    }

    /// <summary>다시 만들기 스크립트에 붙일 절: 바꾸기일 때만, 새 표 머리의 PARTITION BY 와 같은 꼴.</summary>
    [Fact]
    public void Conversion_clause_is_the_partition_by_part_only_for_a_conversion()
    {
        var edited = new PartitionTableDraft(new PartitionScheme(PartitionMethod.Range, ["ORDER_DATE"]) { Interval = "NUMTOYMINTERVAL(1, 'MONTH')" },
            [Row("n1", "P_BEFORE_2026_11", "DATE '2026-11-01'")]);

        Assert.Equal("PARTITION BY RANGE (ORDER_DATE) INTERVAL (NUMTOYMINTERVAL(1, 'MONTH')) (PARTITION P_BEFORE_2026_11 VALUES LESS THAN (DATE '2026-11-01'))",
            PartitionScriptBuilder.ConversionClause(PartitionTableDraft.None, edited, Oracle));
        Assert.Null(PartitionScriptBuilder.ConversionClause(edited, edited, Oracle)); // 이미 파티션 테이블
        Assert.Null(PartitionScriptBuilder.ConversionClause(PartitionTableDraft.None, new PartitionTableDraft(new PartitionScheme(PartitionMethod.Range, []), []), Oracle));
    }

    [Theory]
    [InlineData("12.2.0.1.0", true)]
    [InlineData("12.1.0.2.0", false)]
    [InlineData("18.0.0.0.0", true)]
    [InlineData("11.2.0.4.0", false)]
    public void Online_conversion_needs_oracle_12_2(string version, bool expected) =>
        Assert.Equal(expected, PartitionDefaults.SupportsOnlinePartitionConversion(Server(Dbms.Oracle, version)));

    [Fact]
    public void Create_clause_for_a_new_table()
    {
        var range = PartitionScriptBuilder.CreateClause(RangeScheme("NUMTOYMINTERVAL(1, 'MONTH')"),
            [Row("n1", "P_2026_01", "DATE '2026-02-01'") with { Tablespace = "TS_DATA" }, Row("n2", "P_2026_02", "DATE '2026-03-01'")], Oracle);
        Assert.Equal("PARTITION BY RANGE (ORDER_DATE) INTERVAL (NUMTOYMINTERVAL(1, 'MONTH')) (PARTITION P_2026_01 VALUES LESS THAN (DATE '2026-02-01') TABLESPACE TS_DATA, " +
                     "PARTITION P_2026_02 VALUES LESS THAN (DATE '2026-03-01'))", range);

        Assert.Equal("PARTITION BY LIST (REGION, \"group\") (PARTITION P_E VALUES ('E'), PARTITION P_X VALUES (DEFAULT))",
            PartitionScriptBuilder.CreateClause(new PartitionScheme(PartitionMethod.List, ["REGION", "group"]), [Row("n1", "P_E", "'E'"), Row("n2", "P_X", "DEFAULT")], Oracle));
        Assert.Equal("PARTITION BY HASH (ID) (PARTITION H1, PARTITION H2)",
            PartitionScriptBuilder.CreateClause(new PartitionScheme(PartitionMethod.Hash, ["ID"]), [Row("n1", "H1"), Row("n2", "H2")], Oracle));
        Assert.Equal("PARTITION BY HASH (ID)", PartitionScriptBuilder.CreateClause(new PartitionScheme(PartitionMethod.Hash, ["ID"]), [], Oracle));
    }

    [Fact]
    public void Reserved_and_mixed_case_names_are_quoted()
    {
        var original = new PartitionTableDraft(new PartitionScheme(PartitionMethod.Range, ["ID"]), [Row("p1", "P1", "10")]);
        var edited = original with { Partitions = [Row("p1", "DATE", "10"), Row("n1", "p two", "20")] };

        Assert.Equal(
        [
            "ALTER TABLE SAMPLE.\"ORDER\" RENAME PARTITION P1 TO \"DATE\"",
            "ALTER TABLE SAMPLE.\"ORDER\" ADD PARTITION \"p two\" VALUES LESS THAN (20)",
        ], PartitionScriptBuilder.Build("SAMPLE", "ORDER", original, edited, Oracle).Select(s => s.Statement.Text));
    }

    [Fact]
    public void From_info_keys_rows_by_position_and_leaves_unknown_methods_alone()
    {
        var info = new PartitioningInfo("RANGE", ["ORDER_DATE"],
            [new PartitionInfo("P_B", 2, "DATE '2026-10-01'") { NumRows = 5 }, new PartitionInfo("P_A", 1, "DATE '2026-09-01'") { Tablespace = "TS" }])
        {
            Interval = "NUMTOYMINTERVAL(1, 'MONTH')",
            SubpartitionMethod = "NONE",
        };

        var draft = PartitionTableDraft.FromInfo(info)!;

        Assert.Equal((PartitionMethod.Range, "NUMTOYMINTERVAL(1, 'MONTH')", (string?)null), (draft.Scheme!.Method, draft.Scheme.Interval, draft.Scheme.SubpartitionMethod));
        Assert.Equal([("p1", "P_A", "TS"), ("p2", "P_B", null)], draft.Partitions.Select(p => (p.RowKey, p.Name, p.Tablespace)));
        Assert.Equal(5, draft.Partitions[1].RowCount);
        Assert.Same(PartitionTableDraft.None, PartitionTableDraft.FromInfo(null));
        Assert.Null(PartitionTableDraft.FromInfo(info with { Method = "SYSTEM" }));
    }

    private static ServerInfo Server(Dbms dbms, string version) =>
        new(version, "i", "h", null, "D", null, false, 1, new ServerCapabilities(false, false, true, false, false, dbms), Dbms: dbms);

    /// <summary>기본 규칙(Oracle)만 쓰는 방언.</summary>
    private class OracleLike : ITableEditDialect
    {
        public virtual Dbms Dbms => Dbms.Oracle;

        public IReadOnlyList<string> TypeNames => ["NUMBER"];

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => null;
    }

    /// <summary>Tibero 실측(11·19)대로 해시 ADD·COALESCE·일반 → 파티션 막음, 전역 인덱스는 나누기·합치기 뒤 다시 만듦.</summary>
    private sealed class TiberoLike : OracleLike, ITableEditDialect
    {
        public const string HashKey = "TableEdit.unsupported.tibero.hashPartitionChange";

        public const string ConvertKey = "TableEdit.unsupported.tibero.partitionConversion";

        public override Dbms Dbms => Dbms.Tibero;

        bool ITableEditDialect.KeepsGlobalIndexesOnSplitMerge => false;

        string? ITableEditDialect.UnsupportedPartitionReasonKey(PartitionOperation operation, PartitionMethod method, ServerInfo? server) => operation switch
        {
            PartitionOperation.Convert => ConvertKey,
            PartitionOperation.Add or PartitionOperation.Coalesce when method == PartitionMethod.Hash => HashKey,
            _ => null,
        };
    }
}
