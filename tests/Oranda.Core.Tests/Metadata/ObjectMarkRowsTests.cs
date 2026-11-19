using Oranda.Core.Metadata;

namespace Oranda.Core.Tests.Metadata;

/// <summary>표식 보조 질의 행(<c>meta.marks_*</c>) → <see cref="ObjectMark"/>. Oracle·Tibero 가 같은 열 순서로 읽는다(Tibero 에 없는 열은 NULL).</summary>
public class ObjectMarkRowsTests
{
    private static object?[] TableRow(string name, string temporary = "N", string? duration = null, string? iot = null, string? cluster = null,
        string? part = null, string? sub = null, string? interval = null, string external = "NO", string obj = "NO")
        => [name, temporary, duration, iot, cluster, part is null ? null : $"{part}|{sub}|{interval}", external, obj];

    [Theory]
    [InlineData("Y", "SYS$SESSION", null, null, "GTT_SESSION")]
    [InlineData("Y", "SYS$TRANSACTION", null, null, "GTT_TRANSACTION")]
    [InlineData("YES", "SESSION", null, null, "GTT_SESSION")] // Tibero 표기
    [InlineData("YES", "TRANSACTION", null, null, "GTT_TRANSACTION")]
    [InlineData("N", null, "IOT", null, "IOT")]
    [InlineData("N", null, null, "C_EMP", "CLUSTER")]
    public void Table_kind_tokens(string temporary, string? duration, string? iot, string? cluster, string expected)
    {
        var mark = ObjectMarkRows.Table(TableRow("T", temporary, duration, iot, cluster));

        Assert.Equal(expected, mark.SubKind);
        Assert.Null(mark.State);
    }

    [Fact]
    public void Object_and_external_tables_win_over_other_kinds_and_partitions_are_a_separate_token()
    {
        Assert.Equal("OBJECT", ObjectMarkRows.Table(TableRow("T", "Y", "SYS$SESSION", obj: "YES")).SubKind);
        Assert.Equal("EXTERNAL", ObjectMarkRows.Table(TableRow("T", external: "YES")).SubKind);

        var iotPart = ObjectMarkRows.Table(TableRow("T", iot: "IOT", part: "HASH", sub: "NONE"));
        Assert.Equal("IOT PART:HASH", iotPart.SubKind);
        Assert.True(iotPart.Has("IOT"));
        Assert.Equal("HASH", iotPart.ValueOf("PART"));

        Assert.Equal("PART:RANGE-LIST+INTERVAL", ObjectMarkRows.Table(TableRow("T", part: "RANGE", sub: "LIST", interval: "YES")).SubKind);
    }

    [Fact]
    public void Rows_of_the_same_table_from_several_branches_are_merged()
    {
        // 외부 테이블 갈래와 일반 사전 갈래(파티션)에 같은 테이블이 나온다.
        var read = ObjectMarkRows.Read(ObjectMarkRows.Tables, "HR", [TableRow("EXT", part: "LIST", sub: "NONE"), TableRow("EXT", external: "YES")]);

        var only = Assert.Single(read);
        Assert.True(only.Mark.Has("EXTERNAL"));
        Assert.Equal("LIST", only.Mark.ValueOf("PART"));
    }

    [Fact]
    public void Heap_table_row_has_no_mark_and_is_dropped()
    {
        var read = ObjectMarkRows.Read(ObjectMarkRows.Tables, "HR", [TableRow("HEAP"), TableRow("GTT", "Y", "SYS$SESSION")]);

        var only = Assert.Single(read);
        Assert.Equal(new DbObjectRef("HR", "GTT", "TABLE"), only.Ref);
    }

    [Theory]
    [InlineData("BEFORE EACH ROW", "TABLE", "INSERT", "ENABLED", "BEFORE ROW", null)]
    [InlineData("AFTER STATEMENT", "TABLE", "UPDATE", "DISABLED", "AFTER STATEMENT", "DISABLED")]
    [InlineData("INSTEAD OF", "VIEW", "INSERT", "ENABLED", "INSTEAD ROW", null)]
    [InlineData("COMPOUND", "TABLE", "INSERT OR UPDATE", "ENABLED", "COMPOUND", null)]
    [InlineData("BEFORE EVENT", "SCHEMA", "DROP", "ENABLED", "BEFORE DDL", null)]
    [InlineData("AFTER EVENT", "DATABASE", "LOGON ", "ENABLED", "AFTER DB", null)]
    [InlineData("BEFORE DDL", null, "DROP OR TRUNCATE", "ENABLED", "BEFORE DDL", null)] // Tibero: base_object_type 없음
    [InlineData("AFTER EVENT", null, "STARTUP", "ENABLED", "AFTER DB", null)]
    public void Trigger_tokens(string type, string? baseType, string events, string status, string expected, string? state)
    {
        var mark = ObjectMarkRows.Trigger(["TRG", type, baseType, events, status]);

        Assert.Equal(expected, mark.SubKind);
        Assert.Equal(state, mark.State);
    }

    [Theory]
    [InlineData("FALSE", "DISABLED", "DISABLED")]
    [InlineData("TRUE", "RUNNING", "RUNNING")]
    [InlineData("TRUE", "BROKEN", "BROKEN")]
    [InlineData("TRUE", "FAILED", "FAILED")]
    [InlineData("TRUE", "RETRY SCHEDULED", "FAILED:RETRY SCHEDULED")]
    [InlineData("FALSE", "COMPLETED", "DISABLED")]
    [InlineData("TRUE", "SUCCEEDED", null)]
    public void Job_states(string enabled, string state, string? expected)
    {
        var mark = ObjectMarkRows.Job(["J", enabled, state]);

        Assert.Equal(expected, mark.State);
        Assert.Null(mark.SubKind);
    }

    [Fact]
    public void Failed_job_keeps_the_original_state_as_detail()
    {
        var mark = ObjectMarkRows.Job(["J", "TRUE", "CHAIN_STALLED"]);

        Assert.Equal("FAILED", mark.StateCode);
        Assert.Equal("CHAIN_STALLED", mark.StateDetail);
    }

    [Fact]
    public void Synonym_target_remote_and_broken()
    {
        Assert.Equal("TARGET:MATERIALIZED_VIEW", ObjectMarkRows.Synonym(["S", null, "MATERIALIZED VIEW"]).SubKind);
        Assert.Equal("REMOTE", ObjectMarkRows.Synonym(["S", "PROD.LINK", null]).SubKind);
        var broken = ObjectMarkRows.Synonym(["S", null, null]);
        Assert.Null(broken.SubKind);
        Assert.Equal("BROKEN", broken.State);
    }

    [Fact]
    public void Mview_method_mode_and_staleness()
    {
        var fast = ObjectMarkRows.MView(["MV", "FAST", "COMMIT", "FRESH"]);
        Assert.Equal("FAST COMMIT", fast.SubKind);
        Assert.Null(fast.State);

        Assert.Equal("STALE", ObjectMarkRows.MView(["MV", "COMPLETE", "DEMAND", "STALE"]).State);
        Assert.Equal("STALE:NEEDS_COMPILE", ObjectMarkRows.MView(["MV", "FORCE", "DEMAND", "NEEDS_COMPILE"]).State);
    }

    [Fact]
    public void Collection_types()
    {
        Assert.Equal("VARRAY", ObjectMarkRows.CollectionType(["T", "VARYING ARRAY"]).SubKind);
        Assert.Equal("NESTED_TABLE", ObjectMarkRows.CollectionType(["T", "TABLE"]).SubKind);
        Assert.True(ObjectMarkRows.CollectionType(["T", "SOMETHING"]).IsEmpty);
    }

    [Fact]
    public void Read_maps_query_ids_to_object_types_and_skips_rows_without_a_name()
    {
        Assert.Equal("MATERIALIZED VIEW", ObjectMarkRows.ObjectTypeOf(ObjectMarkRows.MViews));
        Assert.Equal("JOB", ObjectMarkRows.ObjectTypeOf(ObjectMarkRows.Jobs));
        var read = ObjectMarkRows.Read(ObjectMarkRows.Synonyms, "APP", [[null, null, null], ["S1", null, "TABLE"]]);

        Assert.Equal([new DbObjectRef("APP", "S1", "SYNONYM")], read.Select(r => r.Ref));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectMarkRows.ObjectTypeOf("meta.objects"));
    }

    [Fact]
    public void Mark_of_a_cache_row_is_null_when_both_columns_are_empty()
    {
        var row = new MetaObject { DbKey = "K", Owner = "O", Name = "N", Type = "TABLE" };

        Assert.Null(ObjectMark.Of(row));
        Assert.Equal(new ObjectMark("IOT", null), ObjectMark.Of(row with { SubKind = "IOT" }));
    }
}
