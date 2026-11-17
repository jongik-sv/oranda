using System.Text;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.Core.Tests.Schema.TableEdit;

// 소유: te-m1 (T2). 설계 7.3 제약 이름 규칙.

public class ConstraintNamingTests
{
    [Fact]
    public void Primary_key_is_pk_prefix_plus_table()
    {
        Assert.Equal("PK_ORDERS", ConstraintNaming.Next(ConstraintNameKind.PrimaryKey, "ORDERS", [], 30));
    }

    [Theory]
    [InlineData(ConstraintNameKind.Unique, "UK_ORDERS_01")]
    [InlineData(ConstraintNameKind.ForeignKey, "FK_ORDERS_01")]
    [InlineData(ConstraintNameKind.Check, "CK_ORDERS_01")]
    [InlineData(ConstraintNameKind.Index, "IX_ORDERS_01")]
    public void First_name_of_each_kind_is_numbered_01(ConstraintNameKind kind, string expected)
    {
        Assert.Equal(expected, ConstraintNaming.Next(kind, "ORDERS", [], 30));
    }

    [Fact]
    public void Number_is_largest_existing_plus_one_not_count()
    {
        // 01 이 지워져 03 하나만 남아도 04.
        var name = ConstraintNaming.Next(ConstraintNameKind.ForeignKey, "ORDERS", ["FK_ORDERS_03", "FK_ORDERS_02x"], 30);
        Assert.Equal("FK_ORDERS_04", name);
    }

    [Fact]
    public void Other_kinds_and_other_tables_do_not_count()
    {
        var existing = new[] { "UK_ORDERS_07", "FK_ORDER_ITEMS_05", "FK_ORDERS_ITEMS_09", "CK_ORDERS_02" };
        Assert.Equal("FK_ORDERS_01", ConstraintNaming.Next(ConstraintNameKind.ForeignKey, "ORDERS", existing, 30));
    }

    [Fact]
    public void Server_made_sys_names_are_ignored()
    {
        var existing = new[] { "SYS_C0012345", "SYS_C00123", "SYS_IL0000012345C00003$$" };
        Assert.Equal("CK_ORDERS_01", ConstraintNaming.Next(ConstraintNameKind.Check, "ORDERS", existing, 30));
    }

    [Fact]
    public void Existing_names_compare_case_insensitively()
    {
        Assert.Equal("IX_ORDERS_03", ConstraintNaming.Next(ConstraintNameKind.Index, "ORDERS", ["ix_orders_02"], 30));
    }

    [Fact]
    public void Number_grows_to_three_digits_after_99()
    {
        Assert.Equal("UK_T_100", ConstraintNaming.Next(ConstraintNameKind.Unique, "T", ["UK_T_99"], 30));
    }

    [Fact]
    public void Primary_key_name_taken_falls_back_to_numbered()
    {
        Assert.Equal("PK_ORDERS_01", ConstraintNaming.Next(ConstraintNameKind.PrimaryKey, "ORDERS", ["PK_ORDERS"], 30));
    }

    [Fact]
    public void Long_table_name_is_shortened_to_fit_30_bytes()
    {
        var name = ConstraintNaming.Next(ConstraintNameKind.ForeignKey, "CUSTOMER_ORDER_ITEM_HISTORY_DETAIL", [], 30);
        Assert.Equal(30, Encoding.UTF8.GetByteCount(name));
        Assert.StartsWith("FK_CUSTOMER_ORDER_ITEM_HIST", name);
        Assert.EndsWith("_01", name);
    }

    [Fact]
    public void Shortened_name_numbers_continue_from_existing_shortened_names()
    {
        var table = "CUSTOMER_ORDER_ITEM_HISTORY_DETAIL";
        var first = ConstraintNaming.Next(ConstraintNameKind.ForeignKey, table, [], 30);
        var second = ConstraintNaming.Next(ConstraintNameKind.ForeignKey, table, [first], 30);
        Assert.EndsWith("_02", second);
        Assert.Equal(first[..^2], second[..^2]);
    }

    [Fact]
    public void Primary_key_long_name_is_shortened_without_suffix()
    {
        var name = ConstraintNaming.Next(ConstraintNameKind.PrimaryKey, "CUSTOMER_ORDER_ITEM_HISTORY_DETAIL", [], 30);
        Assert.Equal("PK_CUSTOMER_ORDER_ITEM_HISTORY", name);
    }

    [Fact]
    public void Server_128_byte_limit_keeps_full_table_name()
    {
        var table = "CUSTOMER_ORDER_ITEM_HISTORY_DETAIL";
        Assert.Equal("FK_" + table + "_01", ConstraintNaming.Next(ConstraintNameKind.ForeignKey, table, [], 128));
    }

    [Fact]
    public void Korean_table_name_is_cut_on_a_character_boundary()
    {
        // 한글 한 글자 = UTF-8 3 바이트. 30 바이트 한도에서 "FK_" 3 + "_01" 3 = 24 바이트 남음 = 8글자(주문상세내역이력).
        var name = ConstraintNaming.Next(ConstraintNameKind.ForeignKey, "주문상세내역이력관리테이블", [], 30);
        Assert.Equal("FK_주문상세내역이력_01", name);
        Assert.Equal(30, Encoding.UTF8.GetByteCount(name));
    }

    [Fact]
    public void Korean_cut_never_splits_a_character_when_bytes_do_not_divide_evenly()
    {
        // 한도 29: 남는 바이트 23 → 7글자(21바이트, 주문상세내역이), 반 글자를 만들지 않는다.
        var name = ConstraintNaming.Next(ConstraintNameKind.ForeignKey, "주문상세내역이력관리테이블", [], 29);
        Assert.Equal("FK_주문상세내역이_01", name);
        Assert.DoesNotContain('�', name);
        Assert.True(Encoding.UTF8.GetByteCount(name) <= 29);
    }

    [Fact]
    public void Surrogate_pair_is_not_split()
    {
        // 😀 = UTF-8 4 바이트(서로게이트 쌍). 한도 12: "FK_"+"_01"=6, 남는 6 → 😀 한 개(4) + A(1).
        var name = ConstraintNaming.Next(ConstraintNameKind.ForeignKey, "😀A😀A😀A", [], 12);
        Assert.Equal("FK_😀A_01", name);
    }

    [Fact]
    public void Limit_too_small_for_any_table_part_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ConstraintNaming.Next(ConstraintNameKind.ForeignKey, "ORDERS", [], 6));
    }

    [Fact]
    public void Blank_table_throws()
    {
        Assert.Throws<ArgumentException>(() => ConstraintNaming.Next(ConstraintNameKind.Check, " ", [], 30));
    }

    [Fact]
    public void Result_is_not_in_existing_names_even_for_odd_matches()
    {
        // 번호 규칙에 안 맞는 같은 이름이 있으면(예: 인덱스와 제약이 이름 공간을 같이 씀) 피한다.
        var name = ConstraintNaming.Next(ConstraintNameKind.Index, "T", ["IX_T_01", "IX_T_02"], 30);
        Assert.Equal("IX_T_03", name);
    }
}
