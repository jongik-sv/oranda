using Oranda.App.Features.Flashback;

namespace Oranda.App.Tests.Features.Flashback;

/// <summary>VERSIONS 결과의 바뀐 칸 계산: 같은 키 행끼리 시간 순 앞 버전과 견주고 의사열·첫 버전·삭제 버전은 칠하지 않는다.</summary>
public sealed class VersionsDiffTests
{
    // 「이 행 변경 이력」 이 만드는 열 순서와 같다(최신순 결과).
    private static readonly string[] Names = ["VERSIONS_STARTTIME", "VERSIONS_ENDTIME", "VERSIONS_OPERATION", "VERSIONS_XID", "ID", "NAME", "SAL"];

    private static readonly DateTime T0 = new(2026, 11, 16, 9, 0, 0);

    private static object?[] Row(DateTime? start, string? op, object? id, object? name, object? sal) =>
        [start, null, op, "0A00", id, name, sal];

    [Fact]
    public void Layout_excludes_pseudo_columns_and_keys()
    {
        var layout = VersionsDiff.Layout(Names, null, [4]);
        Assert.Equal(0, layout.StartTime);
        Assert.Equal(-1, layout.StartScn);
        Assert.Equal(2, layout.Operation);
        Assert.Equal([5, 6], layout.Data);

        // 별칭을 붙여도 분석기가 원래 열을 알면 의사열로 본다
        var aliased = VersionsDiff.Layout(["ST", "OP", "ID", "NAME", "RID", "ORA_ROWSCN"], ["VERSIONS_STARTSCN", "VERSIONS_OPERATION", "ID", "NAME", "ROWID", null], [2]);
        Assert.Equal(0, aliased.StartScn);
        Assert.Equal(1, aliased.Operation);
        Assert.Equal([3], aliased.Data);
    }

    [Fact]
    public void Newest_first_result_compares_with_the_older_version()
    {
        var layout = VersionsDiff.Layout(Names, null, [4]);
        object?[]?[] rows =
        [
            Row(T0.AddMinutes(2), "D", 1m, "B", 200m),   // 0: 삭제 — 칠하지 않음
            Row(T0.AddMinutes(1), "U", 1m, "B", 200m),   // 1: NAME·SAL 바뀜
            Row(null, null, 1m, "A", null),              // 2: 기간 앞부터 있던 첫 버전 — 칠하지 않음
        ];

        var changed = VersionsDiff.Changed(layout, rows);

        Assert.Equal([1], changed.Keys);
        Assert.Equal([5, 6], changed[1]); // NULL→값도 다름
    }

    [Fact]
    public void Keys_are_compared_separately_and_unread_rows_are_skipped()
    {
        var layout = VersionsDiff.Layout(Names, null, [4]);
        object?[]?[] rows =
        [
            Row(T0, "I", 1m, "A", 10m),
            Row(T0, "I", 2m, "X", 10m),
            null,                                         // 아직 읽지 못한 행
            Row(T0.AddMinutes(1), "U", 2m, "X", 20m),
            Row(T0.AddMinutes(2), "U", 1m, "A", 10m),     // 값 그대로: 칠하지 않음
        ];

        var changed = VersionsDiff.Changed(layout, rows);

        Assert.Equal([3], changed.Keys);
        Assert.Equal([6], changed[3]);
    }

    [Fact]
    public void Same_time_follows_result_order_direction_and_no_clock_uses_result_order()
    {
        var layout = VersionsDiff.Layout(Names, null, [4]);
        object?[]?[] newestFirst =
        [
            Row(T0.AddMinutes(5), "U", 1m, "C", 1m),
            Row(T0, "U", 1m, "B", 1m),                    // 같은 초에 두 번 바뀜: 최신순 결과라 아래 행이 앞 버전
            Row(T0, "I", 1m, "A", 1m),
        ];
        var changed = VersionsDiff.Changed(layout, newestFirst);
        Assert.Equal([0, 1], changed.Keys.Order());

        var noClock = VersionsDiff.Layout(["ID", "NAME"], null, [0]);
        var plain = VersionsDiff.Changed(noClock, [[1m, "A"], [1m, "A"], [1m, "B"]]);
        Assert.Equal([2], plain.Keys);
    }

    [Fact]
    public void Same_time_only_uses_the_statement_order_or_where_the_oldest_null_sits()
    {
        var layout = VersionsDiff.Layout(Names, null, [4]);

        // 기간 앞부터 있던 행을 같은 초에 두 번 UPDATE: 최신순이면 NULL(첫 버전)이 끝. 실제 순서 = 2 → 1 → 0
        object?[]?[] twice =
        [
            Row(T0, "U", 1m, "C", 1m),
            Row(T0, "U", 1m, "B", 1m),
            Row(null, null, 1m, "A", 1m),
        ];
        var changed = VersionsDiff.Changed(layout, twice);
        Assert.Equal([0, 1], changed.Keys.Order());
        Assert.Equal([5], changed[1]); // A→B 는 1행

        // INSERT 직후 UPDATE(같은 초, NULL 없음): 문장의 ORDER BY … DESC 로 최신순을 안다
        const string sql = "SELECT VERSIONS_STARTTIME, ID FROM T VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE WHERE ID = 1 ORDER BY VERSIONS_STARTSCN DESC NULLS LAST";
        Assert.True(VersionsDiff.OrderNewestFirst(sql));
        Assert.False(VersionsDiff.OrderNewestFirst("SELECT * FROM T VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE ORDER BY \"VERSIONS_STARTTIME\""));
        Assert.Null(VersionsDiff.OrderNewestFirst("SELECT * FROM T VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE ORDER BY ID"));
        object?[]?[] insertThenUpdate =
        [
            Row(T0, "U", 1m, "B", 1m),
            Row(T0, "I", 1m, "A", 1m),
        ];
        var fromSql = VersionsDiff.Changed(layout, insertThenUpdate, VersionsDiff.OrderNewestFirst(sql));
        Assert.Equal([0], fromSql.Keys);
    }
}
