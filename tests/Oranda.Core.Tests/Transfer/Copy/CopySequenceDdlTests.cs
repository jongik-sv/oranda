using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>복사가 표보다 먼저 만드는 시퀀스의 문장과 적재 뒤 맞추기 계산(SQ1, 서버 없음).</summary>
public sealed class CopySequenceDdlTests
{
    private static SequenceSpec Spec(string? min = "1", string? max = "9999999999999999999999999999", string inc = "1", string? cache = "20",
                                     bool cycle = false, bool order = false, string start = "21") => new(min, max, inc, cache, cycle, order, start);

    [Fact]
    public void Create_writes_the_source_attributes_in_the_oracle_clause_order()
    {
        Assert.Equal("CREATE SEQUENCE \"DST\".\"SEQ_ORD\" MINVALUE 1 MAXVALUE 999999 INCREMENT BY 5 START WITH 100 CACHE 50 ORDER CYCLE",
            CopySequenceDdl.Create("DST", "SEQ_ORD", Spec(max: "999999", inc: "5", cache: "50", cycle: true, order: true, start: "100")));
    }

    [Fact]
    public void Default_attributes_leave_out_the_cache_clause_so_the_target_uses_its_own_default()
    {
        // SQ3(결정 84): 원본에도 없는 시퀀스는 기본 속성으로 만든다(최소·최대·캐시 절 없음, 증가 1).
        var spec = SequenceSpec.Defaults("41");

        Assert.True(spec.DefaultCache);
        Assert.Equal("CREATE SEQUENCE \"DST\".\"SEQ_ORD\" INCREMENT BY 1 START WITH 41 NOORDER NOCYCLE", CopySequenceDdl.Create("DST", "SEQ_ORD", spec));
    }

    [Fact]
    public void Default_oracle_limits_become_nomaxvalue_and_nominvalue_so_the_target_uses_its_own_default()
    {
        Assert.Contains(" NOMAXVALUE ", CopySequenceDdl.Create("DST", "S", Spec()), StringComparison.Ordinal);
        Assert.Contains(" NOMINVALUE ", CopySequenceDdl.Create("DST", "S", Spec(min: "-999999999999999999999999999", max: "-1", inc: "-1", start: "-1")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData(null)]
    public void Cache_of_zero_or_one_is_nocache_because_oracle_refuses_cache_1(string? cache)
    {
        Assert.EndsWith("START WITH 21 NOCACHE NOORDER NOCYCLE", CopySequenceDdl.Create("DST", "S", Spec(cache: cache)), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_limits_are_left_out()
    {
        Assert.Equal("CREATE SEQUENCE \"DST\".\"S\" INCREMENT BY 1 START WITH 21 CACHE 20 NOORDER NOCYCLE",
            CopySequenceDdl.Create("DST", "S", Spec(min: null, max: " ")));
    }

    [Fact]
    public void From_row_reads_the_catalog_columns_and_needs_increment_and_last_number()
    {
        var spec = CopySequenceDdl.FromRow(["1", "100", "2", "Y", "N", "0", "41"], v => v?.ToString());

        Assert.Equal(new SequenceSpec("1", "100", "2", "0", true, false, "41"), spec);
        Assert.Null(CopySequenceDdl.FromRow(["1", "100", null, "N", "N", "0", "41"], v => v?.ToString()));
        Assert.Null(CopySequenceDdl.FromRow(["1", "100", "2", "N", "N", "0", null], v => v?.ToString()));
        Assert.Null(CopySequenceDdl.FromRow(["1"], v => v?.ToString()));
    }

    [Fact]
    public void Plan_create_ddl_is_the_same_text_as_create()
    {
        var plan = new CopySequencePlan("DST", "SEQ_ORD", Spec());

        Assert.Equal(CopySequenceDdl.Create("DST", "SEQ_ORD", Spec()), plan.CreateDdl);
    }

    [Theory]
    [InlineData(20, null, null)]          // 키 최댓값이 첫 값보다 작다: 맞출 것이 없다
    [InlineData(21, 0, 22)]               // 첫 값과 같다: NEXTVAL 한 번만, 다음 값 = 22
    [InlineData(500, 479, 501)]           // 차이 479 로 한 번 더 NEXTVAL, 다음 값 = 501
    public void Adjustment_moves_the_next_value_past_the_loaded_maximum(long max, int? gap, int? next)
    {
        var adjust = CopySequenceDdl.Adjustment(Spec(start: "21"), max);

        if (gap is null)
        {
            Assert.Null(adjust);
            return;
        }

        Assert.Equal((gap.Value, next!.Value), (adjust!.Value.Gap, adjust.Value.Next));
    }

    [Fact]
    public void Descending_or_non_numeric_sequences_are_not_adjusted()
    {
        Assert.Null(CopySequenceDdl.Adjustment(Spec(inc: "-1", start: "-1"), 10));
        Assert.Null(CopySequenceDdl.Adjustment(Spec(start: "x"), 10));
    }

    [Fact]
    public void Exceeds_compares_with_the_source_maxvalue_when_known()
    {
        Assert.True(CopySequenceDdl.Exceeds(Spec(max: "100"), 101));
        Assert.False(CopySequenceDdl.Exceeds(Spec(max: "100"), 100));
        Assert.False(CopySequenceDdl.Exceeds(Spec(max: null), decimal.MaxValue));
    }

    [Fact]
    public void Alter_increment_and_drop_statements()
    {
        Assert.Equal("ALTER SEQUENCE \"DST\".\"S\" INCREMENT BY 479", CopySequenceDdl.AlterIncrement("DST", "S", 479));
        var drop = CopyCommonStatements.DropCreatedSequence("DST", "S");
        Assert.Equal("DROP SEQUENCE \"DST\".\"S\"", drop.Text);
        Assert.Equal("S", drop.DangerousTarget); // 위험 명령: 미리 보기에 같은 글이 있어야 실행 중 확인 없이 지나간다
        Assert.Equal("DROP", drop.Verb);
    }
}
