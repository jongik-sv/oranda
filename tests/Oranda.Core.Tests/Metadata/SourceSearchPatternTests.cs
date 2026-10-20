using Oranda.Core.Metadata;

namespace Oranda.Core.Tests.Metadata;

// 소유: TSK-07-04. 소스 본문 검색 바인드 값(Oracle 정규식) 만들기.
public class SourceSearchPatternTests
{
    private const string NonWord = SourceSearchPattern.NonWord;

    [Fact]
    public void Plain_text_is_escaped_and_prefiltered_case_insensitively_by_default()
    {
        var binds = SourceSearchPattern.Build(new SourceSearchQuery { Text = "a.b(c)*" });

        Assert.Equal("a.b(c)*", binds.Needle);
        Assert.Equal(@"a\.b\(c\)\*", binds.Pattern);
        Assert.Equal("i", binds.Flags);
    }

    [Fact]
    public void Match_case_uses_the_case_sensitive_flag()
        => Assert.Equal("c", SourceSearchPattern.Build(new SourceSearchQuery { Text = "x", MatchCase = true }).Flags);

    [Theory]
    [InlineData(@"\", @"\\")]
    [InlineData("^$|?+[{", @"\^\$\|\?\+\[\{")]
    [InlineData("]}", "]}")]
    [InlineData("v_total#1", "v_total#1")]
    [InlineData("한글 이름", "한글 이름")]
    public void Escape_quotes_only_regex_metacharacters(string text, string expected)
        => Assert.Equal(expected, SourceSearchPattern.Escape(text));

    [Fact]
    public void Whole_word_wraps_the_escaped_text_with_identifier_boundaries()
    {
        var binds = SourceSearchPattern.Build(new SourceSearchQuery { Text = "v$total", WholeWord = true });

        Assert.Equal("v$total", binds.Needle);
        Assert.Equal($@"(^|{NonWord})(v\$total)($|{NonWord})", binds.Pattern);
        Assert.Equal("[^[:alnum:]_$#]", NonWord);
    }

    [Fact]
    public void Regex_is_sent_as_is_without_a_prefilter()
    {
        var binds = SourceSearchPattern.Build(new SourceSearchQuery { Text = @"^\s*v_total(x)?\s*:=", UseRegex = true, MatchCase = true });

        Assert.Null(binds.Needle);
        Assert.Equal(@"^\s*v_total(x)?\s*:=", binds.Pattern);
        Assert.Equal("c", binds.Flags);
    }

    [Fact]
    public void Whole_word_regex_shifts_back_references_past_the_added_groups()
    {
        var binds = SourceSearchPattern.Build(new SourceSearchQuery { Text = @"(a)\1\.\d", UseRegex = true, WholeWord = true });

        Assert.Equal($@"(^|{NonWord})((a)\3\.\d)($|{NonWord})", binds.Pattern);
        Assert.Equal(@"\9\\", SourceSearchPattern.ShiftBackReferences(@"\7\\", 2));
        Assert.Equal(@"ab\", SourceSearchPattern.ShiftBackReferences(@"ab\", 2));
        Assert.Throws<SearchPatternException>(() =>
            SourceSearchPattern.Build(new SourceSearchQuery { Text = @"(a)\8", UseRegex = true, WholeWord = true }));
    }

    [Fact]
    public void Empty_text_and_too_long_patterns_are_rejected_before_reaching_the_server()
    {
        Assert.Throws<SearchPatternException>(() => SourceSearchPattern.Build(new SourceSearchQuery()));
        Assert.Equal(512, SourceSearchPattern.Build(new SourceSearchQuery { Text = new string('x', 512) }).Pattern.Length);

        var tooLong = Assert.Throws<SearchPatternException>(() => SourceSearchPattern.Build(new SourceSearchQuery { Text = new string('x', 500), WholeWord = true }));
        Assert.Contains("512바이트", tooLong.Message);

        // 바이트로 센다: 한글은 UTF-8 로 3바이트.
        Assert.Throws<SearchPatternException>(() => SourceSearchPattern.Build(new SourceSearchQuery { Text = new string('가', 171) }));
    }

    [Theory]
    [InlineData(12721, false)]
    [InlineData(12722, true)]
    [InlineData(12725, true)]
    [InlineData(12733, true)]
    [InlineData(12734, false)]
    [InlineData(942, false)]
    public void Regex_errors_are_recognized_by_number(int number, bool expected)
        => Assert.Equal(expected, SourceSearchPattern.IsRegexError(number));

    [Fact]
    public void Regex_errors_are_described_in_words_with_the_error_code()
    {
        Assert.Equal("정규식이 올바르지 않다 — 괄호 ( ) 짝이 맞지 않는다 (ORA-12725)", SourceSearchPattern.Describe(12725));
        Assert.Contains("대괄호", SourceSearchPattern.Describe(12726));
        Assert.Contains("ORA-12722", SourceSearchPattern.Describe(12722));
    }
}
