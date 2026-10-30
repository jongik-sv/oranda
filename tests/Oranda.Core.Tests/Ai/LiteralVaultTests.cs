using Oranda.Core.Ai.Gate;

namespace Oranda.Core.Tests.Ai;

public class LiteralVaultTests
{
    [Fact]
    public void Same_raw_token_gets_the_same_number_and_different_tokens_get_new_ones()
    {
        var vault = new LiteralVault();

        Assert.Equal(":v1", vault.Add("'ABC'"));
        Assert.Equal(":v2", vault.Add("100"));
        Assert.Equal(":v1", vault.Add("'ABC'"));
        Assert.Equal(":v3", vault.Add("'abc'"));
        Assert.Equal(3, vault.Count);
    }

    [Fact]
    public void Placeholder_is_restored_with_or_without_colon_and_ignoring_case()
    {
        var vault = new LiteralVault();
        vault.Add("'ABC'");

        Assert.True(vault.TryGetRaw(":v1", out var raw));
        Assert.Equal("'ABC'", raw);
        Assert.True(vault.TryGetRaw("V1", out raw));
        Assert.Equal("'ABC'", raw);
    }

    [Theory]
    [InlineData(":v2", false)]
    [InlineData(":v0", false)]
    [InlineData(":v01", false)]
    [InlineData(":v1x", false)]
    [InlineData(":x1", false)]
    [InlineData(":v", false)]
    public void Unknown_or_malformed_placeholders_are_not_restored(string text, bool expected)
    {
        var vault = new LiteralVault();
        vault.Add("'ABC'");

        Assert.Equal(expected, vault.TryGetRaw(text, out _));
    }

    [Fact]
    public void Own_placeholder_shape_is_detected_even_when_the_vault_does_not_hold_it()
    {
        var vault = new LiteralVault("lit");

        Assert.True(vault.IsOwnPlaceholder(":lit9"));
        Assert.False(vault.IsOwnPlaceholder(":v9"));
        Assert.False(vault.IsOwnPlaceholder(":lit"));
        Assert.Equal(":lit1", vault.Add("1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(":v")]
    [InlineData("1v")]
    [InlineData("v 1")]
    public void Invalid_prefix_is_rejected(string prefix) =>
        Assert.Throws<ArgumentException>(() => new LiteralVault(prefix));

    [Fact]
    public void Statement_pairs_are_remembered_by_exact_masked_text()
    {
        var vault = new LiteralVault();
        vault.RememberStatement("select * from t where a = :v1", "select /* c */ * from t where a = 'x'");

        Assert.True(vault.TryGetStatement("select * from t where a = :v1", out var original));
        Assert.Equal("select /* c */ * from t where a = 'x'", original);
        Assert.False(vault.TryGetStatement("select * from t where a = :v1 ", out _));
    }

    [Fact]
    public void To_string_never_prints_the_values()
    {
        var vault = new LiteralVault();
        vault.Add("'secret-value'");

        Assert.DoesNotContain("secret", vault.ToString(), StringComparison.Ordinal);
        Assert.Equal("LiteralVault(1)", vault.ToString());
    }

    [Fact]
    public void Redact_also_finds_q_quote_inner_text_doubled_quotes_and_suffixed_numbers_but_not_error_numbers()
    {
        var vault = new LiteralVault();
        vault.Add("q'[SECRET]'");
        vault.Add("'it''s fine'");
        vault.Add("01234");
        vault.Add("1234f");

        var redacted = vault.Redact("a SECRET b; it's fine c; ORA-01234: x; 1234f");

        Assert.DoesNotContain("SECRET", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("it's fine", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("1234f", redacted, StringComparison.Ordinal);
        Assert.Contains("ORA-01234", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(":v01")]
    [InlineData(":v99999999999")]
    public void Placeholder_shape_is_ours_even_when_the_number_is_not_canonical(string text)
    {
        var vault = new LiteralVault();
        vault.Add("1");

        Assert.True(vault.IsOwnPlaceholder(text));
        Assert.False(vault.TryGetRaw(text, out _));
    }

    [Fact]
    public void Redact_replaces_known_values_in_text_but_leaves_short_values_and_error_numbers_alone()
    {
        var vault = new LiteralVault();
        vault.Add("'secret-value'");
        vault.Add("1");
        vault.Add("123456");

        var redacted = vault.Redact("ORA-00001: bad 'secret-value' near secret-value and 123456 (row 1)");

        Assert.DoesNotContain("secret-value", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", redacted, StringComparison.Ordinal);
        Assert.Contains("ORA-00001", redacted, StringComparison.Ordinal);
        Assert.Contains("row 1", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Comments_get_their_own_numbers_and_come_back_only_into_a_placeholder_of_the_same_kind()
    {
        var vault = new LiteralVault();
        vault.Add("'KIM'");

        Assert.Equal("--c1", vault.AddComment("-- 월말 정산"));
        Assert.Equal("/*c2*/", vault.AddComment("/* why */"));
        Assert.Equal("--c1", vault.AddComment("-- 월말 정산"));
        Assert.Equal(2, vault.CommentCount);
        Assert.Equal(1, vault.Count);

        Assert.True(vault.TryGetComment("-- C1 ", out var raw));
        Assert.Equal("-- 월말 정산", raw);
        Assert.True(vault.TryGetComment("/* c2 */", out raw));
        Assert.Equal("/* why */", raw);
        Assert.False(vault.TryGetComment("/*c1*/", out _)); // 줄 주석을 블록 자리에 넣지 않는다
        Assert.False(vault.TryGetComment("--c3", out _));
        Assert.False(vault.TryGetComment("--c01", out _));
        Assert.False(vault.TryGetComment("-- c1 note", out _));
        Assert.Equal(["--c1", "/*c2*/", "--c1"], LiteralVault.FindCommentPlaceholders("a --c1\nb /*c2*/ c --c1\n d /*c12x*/ :v1"));
    }

    [Fact]
    public void Redact_replaces_remembered_comments_but_not_empty_ones()
    {
        var vault = new LiteralVault();
        vault.AddComment("/* 고객 홍길동 요청 */");
        vault.AddComment("--");

        var redacted = vault.Redact("ORA-00933: near /* 고객 홍길동 요청 */ -- x");

        Assert.Equal("ORA-00933: near /*c1*/ -- x", redacted);
        Assert.Equal("ORA-00933: /*c1*/ 근처", vault.Redact("ORA-00933: 고객 홍길동 요청 근처"));
    }
}
