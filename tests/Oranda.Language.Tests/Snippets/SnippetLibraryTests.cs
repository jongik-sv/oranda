using Oranda.Language.Snippets;

namespace Oranda.Language.Tests.Snippets;

// 소유: TSK-04-07. 코드 조각·자리 표시자·사용자 조각 파일.
public class SnippetLibraryTests
{
    [Fact]
    public void Expand_places_stops_in_tab_order_and_zero_last()
    {
        var e = SnippetExpander.Expand("SELECT ${1:*} FROM ${2:dual}$0");
        Assert.Equal("SELECT * FROM dual", e.Text);
        var groups = e.Groups;
        Assert.Equal([1, 2, 0], groups.Select(g => g[0].Index).ToArray());
        Assert.Equal("*", e.Text.Substring(groups[0][0].Start, groups[0][0].Length));
        Assert.Equal("dual", e.Text.Substring(groups[1][0].Start, groups[1][0].Length));
        Assert.Equal(e.Text.Length, groups[2][0].Start);
    }

    [Fact]
    public void Expand_mirrors_use_first_default_and_group_together()
    {
        var e = SnippetExpander.Expand("${1:col}, COUNT(*) GROUP BY ${1}");
        Assert.Equal("col, COUNT(*) GROUP BY col", e.Text);
        var g = e.Groups.First(x => x[0].Index == 1);
        Assert.Equal(2, g.Count);
    }

    [Fact]
    public void Expand_without_zero_adds_end_stop()
    {
        var e = SnippetExpander.Expand("a $1 b");
        Assert.Equal("a  b", e.Text);
        Assert.Equal(0, e.Stops[^1].Index);
        Assert.Equal(e.Text.Length, e.Stops[^1].Start);
    }

    [Fact]
    public void Expand_reindents_following_lines_and_honors_newline()
    {
        var e = SnippetExpander.Expand("a\nb\nc", indent: "  ", newLine: "\r\n");
        Assert.Equal("a\r\n  b\r\n  c", e.Text);
    }

    [Fact]
    public void Expand_escapes_dollar_and_keeps_oracle_dollar_names()
    {
        Assert.Equal("$1 V$SESSION", SnippetExpander.Expand("\\$1 V$SESSION").Text);
    }

    [Fact]
    public void Library_covers_snippets_hints_functions_pseudocolumns()
    {
        Assert.NotEmpty(SnippetLibrary.Snippets);
        Assert.Contains(SnippetLibrary.Hints, h => h.Prefix == "FULL");
        Assert.Contains(SnippetLibrary.Functions, f => f.Prefix == "NVL");
        Assert.Contains(SnippetLibrary.PseudoColumns, p => p.Prefix == "ROWNUM");
        Assert.All(SnippetLibrary.All, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
            Assert.False(string.IsNullOrWhiteSpace(s.Example));
        });
    }

    [Fact]
    public void Hint_examples_show_text_as_is()
    {
        var hint = SnippetLibrary.Hints.First(h => h.Prefix == "FULL");
        Assert.Contains("/*+ FULL(e) */", hint.Example);
    }

    [Fact]
    public void Prefixes_are_unique_within_a_kind()
    {
        foreach (var group in SnippetLibrary.All.GroupBy(s => s.Kind))
        {
            Assert.Equal(group.Count(), group.Select(s => s.Prefix.ToUpperInvariant()).Distinct().Count());
        }
    }

    [Fact]
    public void Search_matches_words_and_ranks_prefix_first()
    {
        var r = SnippetLibrary.Search("sel");
        Assert.Equal("sel", r[0].Prefix);
        Assert.Contains(SnippetLibrary.Search("건수"), s => s.Prefix == "selc");
        Assert.Empty(SnippetLibrary.Search("zzzz-없는-것"));
        Assert.All(SnippetLibrary.Search(null, SnippetKind.Hint), s => Assert.Equal(SnippetKind.Hint, s.Kind));
    }

    [Fact]
    public void FindByPrefix_is_case_insensitive_and_user_overrides()
    {
        Assert.Equal("SELECT", SnippetLibrary.FindByPrefix("SEL")!.Name);
        var mine = new Snippet("sel", "내 SELECT", "", "select 1", IsUser: true);
        Assert.Equal("내 SELECT", SnippetLibrary.FindByPrefix("sel", [mine])!.Name);
        Assert.Null(SnippetLibrary.FindByPrefix("nope"));
    }

    [Fact]
    public void Every_builtin_has_an_english_description_and_a_name_without_korean()
    {
        var hangul = new System.Text.RegularExpressions.Regex("[\\uAC00-\\uD7A3]");
        Assert.All(SnippetLibrary.All, s =>
        {
            Assert.True(SnippetLibrary.HasEnglish(s), $"영어 글 없음: {s.Kind} {s.Prefix}");
            var name = SnippetLibrary.DisplayName(s, "en");
            var description = SnippetLibrary.DisplayDescription(s, "en");
            Assert.False(hangul.IsMatch(name), $"영어 이름에 한글: {s.Prefix} → {name}");
            Assert.False(string.IsNullOrWhiteSpace(description));
            Assert.False(hangul.IsMatch(description), $"영어 설명에 한글: {s.Prefix} → {description}");

            // 한국어는 원래 글 그대로다.
            Assert.Equal(s.Name, SnippetLibrary.DisplayName(s, "ko"));
            Assert.Equal(s.Description, SnippetLibrary.DisplayDescription(s, "ko"));
        });
    }

    [Fact]
    public void Summary_never_repeats_the_prefix()
    {
        // 의사 열·함수처럼 이름이 줄임말과 같아도 설명을 보인다(「ROWID ROWID」 가 아니다).
        var rowid = SnippetLibrary.PseudoColumns.First(p => p.Prefix == "ROWID");
        Assert.Equal(rowid.Description, SnippetLibrary.Summary(rowid, "ko"));
        Assert.StartsWith("Physical address", SnippetLibrary.Summary(rowid, "en"), StringComparison.Ordinal);
        Assert.All(SnippetLibrary.All, s => Assert.False(string.Equals(SnippetLibrary.Summary(s, "ko"), s.Prefix, StringComparison.OrdinalIgnoreCase), s.Prefix));

        // 설명이 없는 사용자 조각: 이름이 줄임말과 다르면 이름, 같으면 본문 첫 줄.
        Assert.Equal("내 조회", SnippetLibrary.Summary(new Snippet("my", "내 조회", "", "SELECT 1", IsUser: true), "ko"));
        Assert.Equal("SELECT x", SnippetLibrary.Summary(new Snippet("my", "my", " ", "\n  SELECT ${1:x}\nFROM dual", IsUser: true), "en"));

        // 사용자 조각은 번역하지 않는다.
        var mine = new Snippet("sel", "내 SELECT", "내 설명", "SELECT 1", IsUser: true);
        Assert.Equal("내 설명", SnippetLibrary.DisplayDescription(mine, "en"));
    }

    [Fact]
    public void Search_finds_by_english_text_in_either_language()
    {
        Assert.Contains(SnippetLibrary.Search("nested loops"), s => s.Prefix == "USE_NL");
        Assert.Contains(SnippetLibrary.Search("물리 주소"), s => s.Prefix == "ROWID");
    }

    [Fact]
    public void UserSnippetFile_round_trips()
    {
        var list = new[] { new Snippet("my", "내 조각", "설명", "SELECT ${1:x}\n  FROM dual$0", IsUser: true) };
        var back = UserSnippetFile.Parse(UserSnippetFile.Serialize(list));
        Assert.Single(back);
        Assert.Equal(list[0], back[0]);
    }

    [Fact]
    public void UserSnippetFile_skips_empty_and_rejects_bad_or_future()
    {
        var ok = UserSnippetFile.Parse("""{"schemaVersion":1,"snippets":[{"prefix":"","body":"x"},{"prefix":"a","body":"b"}]}""");
        Assert.Single(ok);
        Assert.Throws<InvalidDataException>(() => UserSnippetFile.Parse("not json"));
        Assert.Throws<InvalidDataException>(() => UserSnippetFile.Parse("""{"schemaVersion":9,"snippets":[]}"""));
    }
}
