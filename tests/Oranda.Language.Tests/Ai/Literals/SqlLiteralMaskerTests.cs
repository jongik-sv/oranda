using Oranda.Core.Ai.Gate;
using Oranda.Language.Ai.Literals;

namespace Oranda.Language.Tests.Ai.Literals;

public class SqlLiteralMaskerTests
{
    private static readonly SqlLiteralMasker Masker = new();

    private static (string Masked, LiteralVault Vault) Mask(string sql)
    {
        var vault = Masker.CreateVault(sql);
        var result = Masker.Mask(sql, vault);
        Assert.True(result.Succeeded);
        return (result.Text, vault);
    }

    [Fact]
    public void Strings_and_numbers_become_numbered_placeholders_and_equal_tokens_share_a_number()
    {
        var (masked, vault) = Mask("select * from t where a = 'KIM' and b in (1, 2.5, 1) and c = N'x' and d = q'[it's]' and e = 'KIM'");

        Assert.Equal("select * from t where a = :v1 and b in (:v2, :v3, :v2) and c = :v4 and d = :v5 and e = :v1", masked);
        Assert.Equal(5, vault.Count);
        Assert.True(vault.TryGetRaw(":v5", out var raw));
        Assert.Equal("q'[it's]'", raw);
    }

    [Fact]
    public void Date_and_timestamp_keywords_are_masked_together_with_their_string()
    {
        var (masked, vault) = Mask("select 1 from t where d >= DATE '2024-01-01' and ts < timestamp\n  '2024-01-01 00:00:00' and x = date'2025-01-01'");

        Assert.Equal("select :v1 from t where d >= :v2 and ts < :v3 and x = :v4", masked);
        Assert.True(vault.TryGetRaw(":v2", out var raw));
        Assert.Equal("DATE '2024-01-01'", raw);
    }

    [Fact]
    public void A_string_that_spans_lines_is_one_literal_but_adjacent_strings_on_different_lines_are_not_merged()
    {
        var (masked, vault) = Mask("select 'first\nsecond' as a,\n'x'\n'y' as b from dual");

        Assert.Equal("select :v1 as a,\n:v2\n:v3 as b from dual", masked);
        Assert.True(vault.TryGetRaw(":v1", out var raw));
        Assert.Equal("'first\nsecond'", raw);
    }

    [Fact]
    public void Comment_bodies_are_emptied_but_hints_are_kept()
    {
        var (masked, _) = Mask("select /*+ INDEX(t ix_a) */ a -- secret note 'x'\nfrom t /* multi\nline 'y' */ where a = 1");

        Assert.Equal("select /*+ INDEX(t ix_a) */ a --\nfrom t /**/ where a = :v1", masked);
    }

    [Theory]
    [InlineData("select 'abc from dual")]
    [InlineData("select q'[abc from dual")]
    [InlineData("select \"abc from dual")]
    [InlineData("select /*+ INDEX(t) from dual where a = 'x'")]
    [InlineData("select a ! b from dual")]
    public void Unreadable_text_fails_closed_without_returning_any_text(string sql)
    {
        var vault = Masker.CreateVault(sql);
        var result = Masker.Mask(sql, vault);

        Assert.False(result.Succeeded);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void A_bind_shaped_like_our_placeholder_but_not_in_the_vault_fails_the_mask()
    {
        var vault = new LiteralVault();
        Assert.False(Masker.Mask("select * from t where a = :v7", vault).Succeeded);
    }

    [Fact]
    public void Existing_binds_are_kept_and_a_clashing_statement_gets_another_prefix()
    {
        var plain = Masker.CreateVault("select * from t where a = :1 and b = :name");
        Assert.Equal("v", plain.Prefix);

        var sql = "select * from t where a = :v1 and b = 'KIM' and c = :LIT1";
        var vault = Masker.CreateVault(sql);
        Assert.Equal("lit2", vault.Prefix);

        var (masked, _) = (Masker.Mask(sql, vault).Text, vault);
        Assert.Equal("select * from t where a = :v1 and b = :lit21 and c = :LIT1", masked);
    }

    [Fact]
    public void Restore_puts_the_original_tokens_back_for_placeholders_the_model_wrote()
    {
        var (_, vault) = Mask("select * from t where name = 'KIM' and n > 10 and d = DATE '2024-01-01'");

        var restored = Masker.Restore("SELECT /* fix */ * FROM t WHERE name = :v1 AND n > ':v2' AND d >= :V3 AND x = :other", vault);

        Assert.True(restored.Succeeded);
        Assert.Equal("SELECT /* fix */ * FROM t WHERE name = 'KIM' AND n > 10 AND d >= DATE '2024-01-01' AND x = :other", restored.Text);
    }

    [Fact]
    public void Restore_reports_a_placeholder_that_looks_like_ours_but_is_not_in_the_vault()
    {
        var (_, vault) = Mask("select * from t where a = 'KIM'");

        var restored = Masker.Restore("select * from t where a = :v1 and b = :v9", vault);

        Assert.False(restored.Succeeded);
        Assert.Equal(":v9", restored.UnknownPlaceholder);
        Assert.Equal(string.Empty, restored.Text);

        var quoted = Masker.Restore("select * from t where b = ':v9'", vault);
        Assert.Equal(":v9", quoted.UnknownPlaceholder);
    }

    [Fact]
    public void Restore_of_the_masked_target_statement_returns_the_original_with_comments()
    {
        var original = "select /* why */ * from t where a = 'KIM' -- tail";
        var (masked, vault) = Mask(original);

        Assert.Equal(original, Masker.Restore(masked, vault).Text);
    }

    [Fact]
    public void Restore_leaves_placeholders_inside_other_strings_and_comments_alone()
    {
        var (_, vault) = Mask("select 'KIM' from dual");

        var restored = Masker.Restore("select 'see :v1 here' from dual -- :v1", vault);

        Assert.Equal("select 'see :v1 here' from dual -- :v1", restored.Text);
    }

    [Fact]
    public void Predicates_are_masked_and_an_unreadable_predicate_is_redacted_whole()
    {
        var (_, vault) = Mask("select * from t where a = 'KIM'");

        var masked = Masker.MaskPredicate("\"A\"='KIM' AND \"B\">100 AND \"C\"=:B1 AND TO_CHAR(\"D\",'YYYY')='2024'", vault);

        Assert.Equal("\"A\"=:v1 AND \"B\">:v2 AND \"C\"=:B1 AND TO_CHAR(\"D\",:v3)=:v4", masked);
        Assert.Equal("[가림]", Masker.MaskPredicate("\"A\"='unterminated", vault));
        Assert.Equal("[가림]", Masker.MaskPredicate("\"A\"=:v99", vault));
        Assert.Equal(string.Empty, Masker.MaskPredicate(string.Empty, vault));
    }

    [Fact]
    public void Only_the_first_hint_right_after_a_dml_keyword_is_kept_and_its_values_are_masked()
    {
        // 힌트 자리가 아닌 /*+ 는 평범한 주석이다(본문 비움). 올바른 자리의 힌트도 안의 문자열·숫자는 가린다.
        Assert.Equal(
            "select a from t where a = :v1 /**/",
            Mask("select a from t where a = 1 /*+ 홍길동 010-1234-5678 'KIM' */").Masked);
        Assert.Equal(
            "select /*+ INDEX(t ix) OPT_PARAM(:v1 :v2) :v3 */ a from t",
            Mask("select /*+ INDEX(t ix) OPT_PARAM('x' 'y') 'SECRET' */ a from t").Masked);
        Assert.Equal(
            "select /*+ INDEX(t ix)\n :v1 :v2 */ a from t",
            Mask("select /*+ INDEX(t ix)\n 'SECRET' 12345 */ a from t").Masked);

        // 힌트 안에 주석(--)이 있거나 읽을 수 없으면 힌트 전체를 비우고, 두 번째 힌트도 비운다.
        Assert.Equal("select /**/ a from t", Mask("select /*+ INDEX(t ix) -- 900101 */ a from t").Masked);
        Assert.Equal("select /*+ FULL(t) */ /**/ a from t", Mask("select /*+ FULL(t) */ /*+ 'SECRET' */ a from t").Masked);
        Assert.Equal("select /**/ a from t", Mask("select /*+ FULL(t) $ */ a from t").Masked);
    }

    [Fact]
    public void Free_text_inside_a_hint_position_comment_is_dropped_but_real_hints_survive()
    {
        Assert.Equal("select /**/ a from t", Mask("select /*+ 담당 홍길동 고객 김철수 kim@corp.com */ a from t").Masked);
        Assert.Equal("select /**/ a from t", Mask("select /*+ written by kim for the test */ a from t").Masked);
        Assert.Equal(
            "select /*+ FULL(t) NO_MERGE LEADING(a b) PARALLEL(:v1) GATHER_PLAN_STATISTICS */ a from t",
            Mask("select /*+ FULL(t) NO_MERGE LEADING(a b) PARALLEL(8) GATHER_PLAN_STATISTICS */ a from t").Masked);
    }

    [Fact]
    public void Placeholders_inside_a_hint_are_restored_and_unknown_ones_are_reported()
    {
        var (_, vault) = Mask("select /*+ CARDINALITY(t 500) */ * from t where a = 'KIM'");

        Assert.Equal("select /*+ CARDINALITY(t 500) */ 1 from t", Masker.Restore("select /*+ CARDINALITY(t :v1) */ 1 from t", vault).Text);
        Assert.Equal(":v9", Masker.Restore("select /*+ CARDINALITY(t :v9) */ 1 from t", vault).UnknownPlaceholder);
    }

    [Fact]
    public void Restore_also_separates_a_following_quote_or_dot()
    {
        var (_, vault) = Mask("select * from t where a = 'KIM' and n = 12345");

        Assert.Equal("select 'KIM' 'x'", Masker.Restore("select :v1'x'", vault).Text);
        Assert.Equal("select 12345 .5", Masker.Restore("select :v2.5", vault).Text);
    }

    [Fact]
    public void A_placeholder_is_separated_from_a_following_identifier_character()
    {
        var (masked, _) = Mask("select case when a=1then 'x' else 'y'end from t where b=10AND c='z'or d=2");

        Assert.Equal("select case when a=:v1 then :v2 else :v3 end from t where b=:v4 AND c=:v5 or d=:v6", masked);
    }

    [Fact]
    public void Restore_separates_the_original_token_from_glued_neighbours()
    {
        var (_, vault) = Mask("select * from t where a = 'KIM' and n = 12345");

        Assert.Equal("select * from t where x 12345 and 'abc' 'KIM'", Masker.Restore("select * from t where x:v2 and 'abc':v1", vault).Text);
        Assert.Equal("select q 'KIM' from t", Masker.Restore("select q:v1 from t", vault).Text);
        Assert.Equal("select :v1b from t", Masker.Restore("select :v1b from t", vault).Text); // 다른 바인드 이름은 건드리지 않는다
    }

    [Theory]
    [InlineData(":v01")]
    [InlineData(":v99999999999")]
    [InlineData(":v0")]
    public void Restore_treats_non_canonical_numbers_of_our_prefix_as_unknown(string placeholder)
    {
        var (_, vault) = Mask("select * from t where a = 'KIM'");

        Assert.Equal(placeholder, Masker.Restore("select * from t where a = " + placeholder, vault).UnknownPlaceholder);
    }

    [Fact]
    public void Masked_text_never_contains_a_raw_literal()
    {
        var sql = "select * from t where a = 'S3CR3T' and b = 987654 and c = q'{key=\"pw\"}' and d = TIMESTAMP '2024-05-05 10:00:00'";
        var (masked, _) = Mask(sql);

        foreach (var secret in new[] { "S3CR3T", "987654", "key=", "pw", "2024-05-05" })
        {
            Assert.DoesNotContain(secret, masked, StringComparison.Ordinal);
        }
    }
}
