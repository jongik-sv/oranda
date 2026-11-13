using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Compare;
using Oranda.Testing.Rollback;

namespace Oranda.Core.Tests.Rollback.Compare;

// 소유: RB-31. 시점 비교 분류와 되돌리는 DML(설계 8.3).
public class PointComparePlannerTests
{
    private static readonly RollbackTable Products = new(
        new DbObjectRef("SAMPLE", "PRODUCTS", "TABLE"),
        [
            new RollbackColumn("PRODUCT_ID", "NUMBER", RollbackValueKind.Number),
            new RollbackColumn("PRODUCT_NAME", "VARCHAR2", RollbackValueKind.Text),
            new RollbackColumn("UNIT_PRICE", "NUMBER", RollbackValueKind.Number),
        ],
        ["PRODUCT_ID"]);

    private static CompareSnapshotRow R(decimal id, string? name, decimal? price) => new([id, name, price]);

    [Fact]
    public void Three_points_classify_revert_restore_remove_conflict_same()
    {
        var b = new[] { R(1001, "A", 25900), R(1007, "B", 19000), R(1040, "C", 4300), R(1050, "D", 100) };
        var c = new[] { R(1001, "A", 28490), R(1007, "B", 20900), R(2001, "N", 1), R(1050, "D", 100) };
        var n = new[] { R(1001, "A", 28490), R(1007, "B", 21500), R(2001, "N", 1), R(1050, "D", 999) };

        var plan = PointComparePlanner.Classify(Products, b, c, n);

        CompareKind KindOf(decimal id) => plan.Rows.Single(r => (decimal)r.Key[0]! == id).Kind;
        Assert.Equal(CompareKind.Revert, KindOf(1001));
        Assert.Equal(CompareKind.Conflict, KindOf(1007));
        Assert.Equal(CompareKind.Restore, KindOf(1040));
        Assert.Equal(CompareKind.Remove, KindOf(2001));
        Assert.Equal(CompareKind.Same, KindOf(1050)); // B = C 이면 지금 값이 달라도 이 실행과 무관
        Assert.Equal([2], plan.Rows.Single(r => r.Kind == CompareKind.Revert).ChangedColumns);
        Assert.True(plan.HasCommitPoint);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Dml_order_is_restore_revert_remove_with_now_value_conditions_and_conflicts_opt_in()
    {
        var b = new[] { R(1001, "A", 25900), R(1007, "B", 19000), R(1040, "보리차", 4300) };
        var c = new[] { R(1001, "A", 28490), R(1007, "B", 20900), R(2001, "N", null) };
        var n = new[] { R(1001, "A", 28490), R(1007, "B", 21500), R(2001, "N", null) };
        var plan = PointComparePlanner.Classify(Products, b, c, n);
        var dialect = new FakeRollbackDialect();

        var off = PointComparePlanner.BuildDml(plan, dialect);
        Assert.Equal(3, off.Statements.Count);
        Assert.StartsWith("INSERT INTO SAMPLE.PRODUCTS (PRODUCT_ID, PRODUCT_NAME, UNIT_PRICE) VALUES (", off.Statements[0].Sql);
        Assert.StartsWith("UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = ", off.Statements[1].Sql);
        Assert.Contains(" WHERE PRODUCT_ID = ", off.Statements[1].Sql);
        Assert.Contains(" AND UNIT_PRICE = ", off.Statements[1].Sql);
        Assert.StartsWith("DELETE FROM SAMPLE.PRODUCTS WHERE PRODUCT_ID = ", off.Statements[2].Sql);
        Assert.Contains("UNIT_PRICE IS NULL", off.Statements[2].Sql);

        var on = PointComparePlanner.BuildDml(plan, dialect, includeConflicts: true);
        var conflict = Assert.Single(on.Statements, s => s.Note == "충돌");
        Assert.Contains("21500", conflict.Sql); // 충돌 행은 지금 값(N)을 조건에 넣는다
    }

    [Fact]
    public void Without_commit_point_compares_base_and_now_and_warns()
    {
        var plan = PointComparePlanner.Classify(Products, [R(1, "A", 1), R(2, "B", 2)], null, [R(1, "A", 5), R(3, "C", 3)]);
        Assert.False(plan.HasCommitPoint);
        Assert.Contains(PointComparePlanner.NoCommitPointWarning, plan.Warnings);
        Assert.Equal(1, plan.Count(CompareKind.Revert));
        Assert.Equal(1, plan.Count(CompareKind.Restore));
        Assert.Equal(1, plan.Count(CompareKind.Remove));
        Assert.Equal(0, plan.Count(CompareKind.Conflict));
    }

    [Fact]
    public void Restore_skips_row_with_non_null_large_column_and_notes_identity()
    {
        var t = Products with
        {
            Columns = [.. Products.Columns, new RollbackColumn("NOTE", "CLOB", RollbackValueKind.Large), new RollbackColumn("SEQ", "NUMBER", RollbackValueKind.Number) { IsIdentity = true }],
        };
        // 큰 열은 NULL 여부 표지로 읽는다(1 = NULL, 0 = 값 있음)
        var plan = PointComparePlanner.Classify(t, [new([1m, "A", 1m, 1m, 7m]), new([2m, "B", 2m, 0m, 8m])], null, []);
        var script = PointComparePlanner.BuildDml(plan, new FakeRollbackDialect());
        var ok = Assert.Single(script.Statements);
        Assert.DoesNotContain("NOTE", ok.Sql);
        Assert.Contains("ORA-32795", ok.Note);
        Assert.Contains("NOTE", Assert.Single(script.Skipped).Reason);
    }

    [Fact]
    public void Table_without_pk_pairs_by_rowid_and_warns()
    {
        var t = Products with { KeyColumns = [] };
        var plan = PointComparePlanner.Classify(t, [R(1, "A", 1) with { RowId = "AAA" }], null, [R(1, "A", 2) with { RowId = "AAA" }]);
        Assert.Contains(PointComparePlanner.NoKeyWarning, plan.Warnings);
        var sql = Assert.Single(PointComparePlanner.BuildDml(plan, new FakeRollbackDialect()).Statements).Sql;
        Assert.Contains("ROWID = ", sql);
    }

    [Fact]
    public void Queries_read_rows_by_key_at_point_and_rewrite_where()
    {
        var d = new FakeRollbackDialect();
        var keys = Enumerable.Range(1, 1001).Select(i => (IReadOnlyList<object?>)[(decimal)i]).ToList();
        var reads = PointCompareQueries.ReadRows(Products, d, 48220114, keys, out var skipped);
        Assert.Equal(2, reads.Count); // IN 목록 1000개씩
        Assert.Equal(0, skipped);
        Assert.Contains("FROM SAMPLE.PRODUCTS AS OF SCN 48220114 t WHERE t.PRODUCT_ID IN (", reads[0].Sql);
        Assert.Contains("ROWIDTOCHAR(t.ROWID) AS " + PointCompareQueries.RowIdAlias, reads[0].Sql);

        var w = PointCompareQueries.WhereKeys(Products, d, 48220231, "p.CATEGORY_ID = :c", "P", new Dictionary<string, Oranda.Core.Execution.BindValue>());
        Assert.StartsWith("SELECT " + d.ReadExpression(Products.Columns[0], "P.PRODUCT_ID") + " AS K0 FROM SAMPLE.PRODUCTS AS OF SCN 48220231 P WHERE p.CATEGORY_ID = :c", w.Sql);

        var v = PointCompareQueries.VersionKeys(Products, d, 1, 2, "0A001B00");
        Assert.EndsWith(" AS K0 FROM SAMPLE.PRODUCTS VERSIONS BETWEEN SCN :b AND :c WHERE VERSIONS_XID = HEXTORAW(:xid)", v.Sql); // 키는 읽기 식(고정 형식 글)
    }
}
