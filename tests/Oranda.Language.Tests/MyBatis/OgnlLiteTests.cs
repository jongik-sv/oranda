using Oranda.Core.Text;
using Oranda.Language.MyBatis;

namespace Oranda.Language.Tests.MyBatis;

public class OgnlLiteTests
{
    // 골든 값 사전(Ognl.golden.json 의 values). _parameter 는 사전 전체, _databaseId 는 null 로 이 시험의 lookup 이 처리한다.
    private static readonly Dictionary<string, object?> Values = LoadValues();

    private static readonly OgnlLookup Lookup = From(Values);

    /// <summary>
    /// p2-ognl-subset.md 7절 「계산 못 함」: MyBatis 실측은 true 여도 Oranda 는 Failure 를 돌려준다(부분집합 밖).
    /// 산술 + % · in/not in · 정적 호출 · 목록에 없는 메서드 · instanceof · 삼항 · 색인 · #this · 대입/=~ · 선택 {? } ·
    /// MyBatis 가 BigInteger 로 계산하는 값(10H 리터럴, 글자·null 의 부호) · 목록에 없는 Java 빈 속성(iterator·bytes·class). 그 밖은 골든과 값까지 같아야 한다.
    /// </summary>
    private static readonly string[] Unevaluable =
    [
        "n1 + 1 == 2",
        "ename + 'X' == 'KIMX'",
        "n1 in {1, 2}",
        "n1 not in {1, 2}",
        "@java.lang.Math@max(1, 2) == 2",
        "ename.toUpperCase() == 'KIM'",
        "ename.startsWith('K')",
        "ename instanceof java.lang.String",
        "n1 == 1 ? true : false",
        "list2[0] == 10",
        "#this != null",
        "list2.contains(10)",
        "n1 % 2 == 1",
        "ename.equalsIgnoreCase('kim')",
        "ename = 'KIM'",
        "ename =~ 'K'",
        "list2.{? #this > 10}.size() == 1",
        "n1 == 1H",
        "-nul == 0",
        "list2.iterator != null",
        "ename.bytes != null",
        "n1.class != null",
    ];

    /// <summary>bind value 표(Bind.golden.json) 중 Oranda 가 계산하지 않는 식: 빼기(부분집합 밖).</summary>
    private static readonly string[] UnevaluableBind = ["-(-2147483647 - 1)"];

    private static OgnlLookup From(Dictionary<string, object?> map) =>
        (string name, out object? value) =>
        {
            if (name == "_parameter")
            {
                value = map;
                return true;
            }

            if (name == "_databaseId")
            {
                value = null;
                return true;
            }

            return map.TryGetValue(name, out value);
        };

    private static Dictionary<string, object?> LoadValues()
    {
        using var doc = GoldenData.Load("Ognl.golden.json");
        return (Dictionary<string, object?>)GoldenData.Decode(doc.RootElement.GetProperty("values"))!;
    }

    private static OgnlValue Run(string expression) => OgnlLite.Test(expression, Lookup);

    private static void AssertResult(bool expected, string expression)
    {
        var r = Run(expression);
        Assert.True(r.Ok, $"{expression} => {r.Failure?.Reason}");
        Assert.Equal(expected, (bool)r.Value!);
    }

    private static OgnlFailure AssertFails(string expression)
    {
        var r = Run(expression);
        Assert.False(r.Ok, $"{expression} 는 계산 못 함이어야 한다");
        return r.Failure!;
    }

    [Fact]
    public void Golden_table_matches_MyBatis_3_5_19()
    {
        using var doc = GoldenData.Load("Ognl.golden.json");
        var cases = doc.RootElement.GetProperty("cases");
        Assert.Equal(262, cases.GetArrayLength());

        var mismatches = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases.EnumerateArray())
        {
            var expression = c.GetProperty("expression").GetString()!;
            var golden = c.GetProperty("result").GetString();
            var r = Run(expression);
            if (Array.IndexOf(Unevaluable, expression) >= 0)
            {
                seen.Add(expression);
                if (r.Ok)
                {
                    mismatches.Add($"[{expression}] 계산 못 함이어야 하는데 {r.Value} 를 돌려줬다");
                }

                continue;
            }

            string actual = !r.Ok ? "error" : ((bool)r.Value!) ? "true" : "false";
            if (actual != golden)
            {
                mismatches.Add($"[{expression}] 골든 {golden}, 계산 {actual}{(r.Ok ? string.Empty : " (" + r.Failure!.Reason + ")")}");
            }
        }

        Assert.Equal(Unevaluable.Length, seen.Count); // 계산 못 함 목록의 식이 모두 골든에 있다
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void Bind_value_table_matches_MyBatis_3_5_19_value_type_and_java_string()
    {
        // MyBatis 3.5.19 실측: "a" + null = "anull", '%' + null = NPE, '%' + 1 = "%1", 'a' + 'b' = 195(int), int 넘침은 돈다, double 은 Java 표기.
        using var doc = GoldenData.Load("Bind.golden.json");
        var cases = doc.RootElement.GetProperty("cases");
        Assert.Equal(110, cases.GetArrayLength());

        var mismatches = new List<string>();
        foreach (var c in cases.EnumerateArray())
        {
            var expression = c.GetProperty("expression").GetString()!;
            var r = OgnlLite.Value(expression, Lookup);
            var result = c.GetProperty("result");
            bool mustFail = c.GetProperty("error").GetString() is not null
                || Array.IndexOf(UnevaluableBind, expression) >= 0
                || (result.ValueKind == System.Text.Json.JsonValueKind.Object && result.GetProperty("t").GetString() == "other");
            if (mustFail)
            {
                if (r.Ok)
                {
                    mismatches.Add($"[{expression}] 실패해야 하는데 {OgnlLite.JavaString(r.Value)} 를 돌려줬다");
                }

                continue;
            }

            if (!r.Ok)
            {
                mismatches.Add($"[{expression}] 계산 못 함({r.Failure!.Reason})");
                continue;
            }

            var expected = GoldenData.Decode(result);
            var javaString = c.GetProperty("javaString").GetString();
            if (!Equals(expected, r.Value) || expected?.GetType() != r.Value?.GetType()
                || (javaString is not null && javaString != OgnlLite.JavaString(r.Value)))
            {
                mismatches.Add($"[{expression}] 골든 {expected?.GetType().Name}:{javaString}, 계산 {r.Value?.GetType().Name}:{OgnlLite.JavaString(r.Value)}");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void Equality_operators_accept_symbols_and_words()
    {
        AssertResult(true, "n1 == 1");
        AssertResult(true, "n1 eq 1");
        AssertResult(false, "n1 != 1");
        AssertResult(false, "n1 neq 1");
        AssertResult(true, "ename neq 'LEE'");
        AssertResult(true, "nul == null");
        AssertResult(false, "n0 == null");
    }

    [Fact]
    public void Relational_operators_accept_symbols_and_words()
    {
        AssertResult(true, "n1 > 0 and n1 gt 0");
        AssertResult(true, "n1 >= 1 and n1 gte 1");
        AssertResult(true, "n1 < 2 and n1 lt 2");
        AssertResult(true, "n1 <= 1 and n1 lte 1");
        AssertResult(false, "n1 < 1");
        AssertResult(true, "nul < 1");
        AssertResult(false, "nul > 1");
    }

    [Fact]
    public void Numbers_compare_by_value_across_types()
    {
        AssertResult(true, "lng == 7369"); // long 대 int 리터럴
        AssertResult(true, "n1 == 1.0");
        AssertResult(true, "dec == 1.50");
        AssertResult(true, "dbl > dec");
        AssertResult(true, "s2 > 1"); // 글자는 수로 바꾼다
        AssertResult(true, "n1 == \"1\"");
        AssertResult(false, "n1 == '1'"); // char 는 코드 49
        AssertResult(true, "n1 == true");
    }

    [Fact]
    public void Single_quoted_one_char_is_char_as_MyBatis_3_5_19_measured()
    {
        // MyBatis 3.5.19 실측: 'Y' 는 char 라 글자 "Y" 와 비교하면 NumberFormatException.
        var f = AssertFails("flagY == 'Y'");
        Assert.Contains("NumberFormatException", f.Reason, StringComparison.Ordinal);
        Assert.Contains("char", f.Reason, StringComparison.Ordinal);
        Assert.Equal(new TextRange(0, 12), f.Range);
        AssertResult(true, "flagY == \"Y\"");
        AssertResult(true, "ename == 'KIM'"); // 두 글자 이상은 string
        AssertResult(false, "empty == 'Y'"); // 빈 글자는 0
        AssertResult(false, "flagY.equals('Y')"); // string 과 char 는 equals 에서 다름
    }

    [Fact]
    public void Logical_operators_follow_precedence()
    {
        AssertResult(true, "n1 == 1 and n0 == 0");
        AssertResult(true, "n1 == 1 && n0 == 0");
        AssertResult(true, "n1 == 1 or n0 == 1");
        AssertResult(true, "n0 == 1 || n1 == 1");
        AssertResult(false, "boolF or boolT and boolF"); // and 가 먼저
        AssertResult(true, "not boolF and boolT");
        AssertResult(false, "not boolT");
        AssertResult(false, "!boolT");
        AssertResult(true, "! boolF");
    }

    [Fact]
    public void Not_binds_tighter_than_equality()
    {
        AssertResult(false, "not nul == null"); // (not nul) == null
        AssertResult(false, "!nul == null");
    }

    [Fact]
    public void And_short_circuits_so_later_failure_is_not_reached()
    {
        AssertResult(false, "nul != null and nul.size() > 0");
        AssertResult(true, "nul == null or nul.size() > 0");
    }

    [Fact]
    public void Parentheses_group_operands()
    {
        AssertResult(true, "(n1 == 1 or n0 == 1) and boolT");
        AssertResult(false, "not (n1 == 1)");
        AssertResult(false, "!(n1 == 1)");
    }

    [Fact]
    public void Truthiness_follows_evaluateBoolean()
    {
        AssertResult(false, "n0");
        AssertResult(true, "n1");
        AssertResult(true, "dec");
        AssertResult(false, "nul");
        AssertResult(false, "missing");
        AssertResult(true, "empty"); // 빈 글자도 참
        AssertResult(true, "flagFalse"); // "false" 글자도 참
        AssertResult(true, "list0"); // 빈 목록도 참
        AssertResult(true, "list2");
    }

    [Fact]
    public void Dotted_path_prefers_flat_key_over_nested_map()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["user.name"] = "FLAT",
            ["user"] = new Dictionary<string, object?> { ["name"] = "NESTED", ["dept"] = new Dictionary<string, object?> { ["id"] = 10 } },
        };
        var lookup = From(values);

        Assert.Equal("FLAT", OgnlLite.Resolve("user.name", lookup).Value);
        Assert.Equal(10, OgnlLite.Resolve("user.dept.id", lookup).Value);
        Assert.Equal("FLAT", OgnlLite.ParameterValue("user.name", lookup).Value);
        Assert.True((bool)OgnlLite.Test("user.name == 'FLAT'", lookup).Value!);
        Assert.True((bool)OgnlLite.Test("user.dept.id.equals(10)", lookup).Value!);
    }

    [Fact]
    public void Resolve_uses_longest_flat_prefix_then_walks_the_rest()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["a.b"] = new Dictionary<string, object?> { ["c"] = 5 },
            ["a"] = new Dictionary<string, object?> { ["b"] = new Dictionary<string, object?> { ["c"] = 1 } },
        };

        Assert.Equal(5, OgnlLite.Resolve("a.b.c", From(values)).Value);
        Assert.Equal(5, OgnlLite.ParameterValue("a.b.c", From(values)).Value);
    }

    [Fact]
    public void Resolve_reads_dictionaries_and_list_size_and_gives_null_for_gaps()
    {
        Assert.Equal("KIM", OgnlLite.Resolve("user.name", Lookup).Value);
        Assert.Equal(2, OgnlLite.Resolve("list2.size", Lookup).Value);
        Assert.Equal(0, OgnlLite.Resolve("list0.size", Lookup).Value);
        Assert.Null(OgnlLite.Resolve("user.missing", Lookup).Value);
        Assert.Null(OgnlLite.Resolve("nul.name", Lookup).Value);
        Assert.Null(OgnlLite.Resolve("missing.name.x", Lookup).Value);
        Assert.False(OgnlLite.Resolve("ename.name", Lookup).Ok); // MyBatis 3.5.19 실측: 글자의 없는 속성은 NoSuchPropertyException

        // OGNL 사전 접근자: size 는 열쇠 "size"(99)보다 먼저 개수(MyBatis 3.5.19 실측 sz.size == 2).
        Assert.Equal(2, OgnlLite.Resolve("sz.size", Lookup).Value);
    }

    [Fact]
    public void Resolve_reads_non_generic_and_read_only_dictionaries()
    {
        var old = new System.Collections.Hashtable { ["name"] = "OLD" };
        var values = new Dictionary<string, object?> { ["h"] = old, ["r"] = new Dictionary<string, string> { ["k"] = "V" } };

        Assert.Equal("OLD", OgnlLite.Resolve("h.name", From(values)).Value);
        Assert.Equal("V", OgnlLite.Resolve("r.k", From(values)).Value);
    }

    [Fact]
    public void ParameterValue_follows_MetaObject_index_and_plain_map_keys()
    {
        // #{…} 값은 MyBatis MetaObject 규칙: 사전은 열쇠로만(size 도 열쇠), [n] 은 목록 색인, 목록·글자의 속성은 오류.
        Assert.Equal(99, OgnlLite.ParameterValue("sz.size", Lookup).Value);
        Assert.Equal(20, OgnlLite.ParameterValue("list2[1]", Lookup).Value);
        Assert.Equal(10, OgnlLite.ParameterValue("user[dept].id", Lookup).Value);
        Assert.Null(OgnlLite.ParameterValue("nul.x", Lookup).Value);
        Assert.Contains("IndexOutOfBounds", OgnlLite.ParameterValue("list2[5]", Lookup).Failure!.Reason, StringComparison.Ordinal);
        Assert.False(OgnlLite.ParameterValue("list2.size", Lookup).Ok);
        Assert.False(OgnlLite.ParameterValue("ename.length", Lookup).Ok);
    }

    [Fact]
    public void Methods_size_isEmpty_length_trim_work_on_their_receivers()
    {
        AssertResult(true, "list2.size() == 2");
        AssertResult(true, "list0.size() == 0");
        AssertResult(true, "list0.isEmpty()");
        AssertResult(true, "!list2.isEmpty()");
        AssertResult(true, "empty.isEmpty()");
        AssertResult(true, "ename.length() == 3");
        AssertResult(true, "blank.trim() == ''");
        AssertResult(true, "blank.trim().length() == 0");
        AssertResult(true, "ename.trim().equals('KIM')");
        AssertResult(true, "user.size() == 2");
        AssertResult(true, "list2.size");
    }

    [Fact]
    public void Equals_follows_Java_equals_including_the_type()
    {
        AssertResult(true, "n1.equals(1)");
        AssertResult(false, "lng.equals(7369)"); // long 대 int
        AssertResult(true, "lng.equals(n7369) == false");
        AssertResult(false, "flagY.equals('Y')"); // string 대 char
        AssertResult(true, "flagY.equals(\"Y\")");
        AssertResult(false, "n1.equals(1.0)"); // int 대 double
        AssertResult(false, "ename.equals(null)");
    }

    [Fact]
    public void Method_on_null_fails_at_the_call()
    {
        var f = AssertFails("nul.size() == 0");
        Assert.Contains("NullPointerException", f.Reason, StringComparison.Ordinal);
        Assert.Equal(new TextRange(0, 10), f.Range); // nul.size()
    }

    [Fact]
    public void Unknown_method_fails_at_the_method_name()
    {
        var f = AssertFails("ename.toUpperCase() == 'KIM'");
        Assert.Contains("toUpperCase", f.Reason, StringComparison.Ordinal);
        Assert.Equal(new TextRange(6, 11), f.Range);
    }

    [Fact]
    public void Unevaluable_operators_report_their_own_position()
    {
        var plus = AssertFails("n1 + 1 == 2");
        Assert.Equal("부분집합 밖의 연산자 +", plus.Reason);
        Assert.Equal(new TextRange(3, 1), plus.Range);

        var mod = AssertFails("n1 % 2 == 1");
        Assert.Equal(new TextRange(3, 1), mod.Range);

        var inOp = AssertFails("n1 in {1, 2}");
        Assert.Equal(new TextRange(3, 2), inOp.Range);

        var notIn = AssertFails("n1 not in {1, 2}");
        Assert.Equal(new TextRange(3, 6), notIn.Range);

        var bigInteger = AssertFails("n1 == 1H");
        Assert.Equal(new TextRange(6, 2), bigInteger.Range);

        var assign = AssertFails("ename = 'KIM'");
        Assert.Equal(new TextRange(6, 1), assign.Range);
    }

    [Fact]
    public void Uppercase_AND_is_a_syntax_error_as_MyBatis_3_5_19_measured()
    {
        var f = AssertFails("n1 == 1 AND n0 == 0");
        Assert.Equal(new TextRange(8, 3), f.Range);
        Assert.Contains("AND", f.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncated_or_empty_expression_fails()
    {
        var cut = AssertFails("ename == 'KIM' and");
        Assert.Equal(new TextRange(15, 3), cut.Range); // 끝난 자리 앞의 마지막 토큰

        Assert.Equal(new TextRange(0, 3), AssertFails("   ").Range);
        AssertFails("(n1 == 1");
        AssertFails("'abc == 1");
        AssertFails("n1 == 1 )");
    }

    [Fact]
    public void Negation_and_literals_cover_ints_longs_and_doubles()
    {
        AssertResult(true, "-n1 < 0");
        AssertResult(true, "n1 > -1");
        AssertResult(true, "n1 == -(-1)");
        AssertResult(true, "lng == 7369");
        Assert.Contains("NumberFormatException", AssertFails("n1 < 3000000000").Reason, StringComparison.Ordinal); // MyBatis 3.5.19 실측: L 없는 리터럴은 int
        AssertResult(true, "n1 < 3000000000L");
        AssertResult(true, "n1 == 0x1");
        AssertResult(false, "n1 == 010"); // 0 으로 시작하면 8진수(8)
        AssertResult(true, "dbl == 2.5e0");
        AssertResult(true, "ename == 'K\\u0049M'"); // 유니코드 이스케이프
        AssertResult(true, "ename != 'it\\'s'");
    }

    [Fact]
    public void Value_joins_strings_for_like_patterns()
    {
        var r = OgnlLite.Value("'%' + ename + '%'", Lookup);
        Assert.True(r.Ok, r.Failure?.Reason);
        Assert.Equal("%KIM%", r.Value);

        Assert.Equal("KIM%", OgnlLite.Value("ename + '%'", Lookup).Value);
        Assert.Equal("%1", OgnlLite.Value("\"%\" + n1", Lookup).Value);
        Assert.Equal("%1", OgnlLite.Value("'%' + n1", Lookup).Value); // MyBatis 3.5.19 실측: char 와 수는 글자로 잇는다
        Assert.Equal(195, OgnlLite.Value("'a' + 'b'", Lookup).Value); // char 와 char 는 int 로 더한다
        Assert.Equal("id=7369", OgnlLite.Value("\"id=\" + lng", Lookup).Value);
    }

    [Fact]
    public void Value_adds_numbers_and_keeps_the_type()
    {
        Assert.Equal(3, OgnlLite.Value("n1 + 2", Lookup).Value);
        Assert.Equal(7370L, OgnlLite.Value("lng + 1", Lookup).Value);
        Assert.Equal(2.5m, OgnlLite.Value("dec + n1", Lookup).Value);
        Assert.Equal(3.5d, OgnlLite.Value("dbl + n1", Lookup).Value);
        Assert.Equal(2, OgnlLite.Value("n1 + n1", Lookup).Value);
        Assert.Equal(int.MinValue, OgnlLite.Value("n1 + 2147483647", Lookup).Value); // int 넘침은 Java 처럼 돈다
        Assert.Equal("x1.0E-4", OgnlLite.Value("\"x\" + 0.0001", Lookup).Value); // Double.toString
    }

    [Fact]
    public void Value_returns_the_expression_value_as_is()
    {
        Assert.Equal("KIM", OgnlLite.Value("ename", Lookup).Value);
        Assert.Equal(10, OgnlLite.Value("user.dept.id", Lookup).Value);
        Assert.Null(OgnlLite.Value("nul", Lookup).Value);
        Assert.Same(Values["list2"], OgnlLite.Value("list2", Lookup).Value);
    }

    [Fact]
    public void Value_with_null_operand_fails_like_MyBatis_3_5_19()
    {
        // MyBatis 3.5.19 실측: NullPointerException: Can't add values % , null (bindLike/null)
        var r = OgnlLite.Value("'%' + nul + '%'", Lookup);
        Assert.False(r.Ok);
        Assert.Contains("Can't add values % , null", r.Failure!.Reason, StringComparison.Ordinal);
        Assert.Equal(new TextRange(0, 9), r.Failure.Range); // '%' + nul

        // 글자와 null 은 MyBatis 처럼 "null" 을 잇는다(사용자 지시 2026-10-23: 완전히 같은 처리).
        Assert.Equal("anull", OgnlLite.Value("\"a\" + nul", Lookup).Value);
        Assert.Equal("nullnull", OgnlLite.Value("nul + nul", Lookup).Value);
        var missing = OgnlLite.Value("missing + 1", Lookup);
        Assert.False(missing.Ok);
    }

    [Fact]
    public void Plus_is_unevaluable_in_test_expressions_but_other_operators_are_not_added_to_value()
    {
        Assert.False(Run("ename + 'X' == 'KIMX'").Ok);
        Assert.True(OgnlLite.Value("ename + 'X' == 'KIMX'", Lookup).Ok); // Value 에서는 + 가 비교보다 먼저
        Assert.False(OgnlLite.Value("n1 - 1", Lookup).Ok);
        Assert.False(OgnlLite.Value("n1 * 2", Lookup).Ok);
    }

    [Fact]
    public void References_lists_paths_in_order_without_method_names()
    {
        var refs = OgnlLite.References("ename != null and ename != ''");
        Assert.Equal(
            [new OgnlReference("ename", new TextRange(0, 5)), new OgnlReference("ename", new TextRange(18, 5))],
            refs);

        var expression = "user.dept.id > 5 and list2.size() > 0";
        Assert.Equal(
            [new OgnlReference("user.dept.id", new TextRange(0, 12)), new OgnlReference("list2", new TextRange(expression.IndexOf("list2", StringComparison.Ordinal), 5))],
            OgnlLite.References(expression));
    }

    [Fact]
    public void References_skips_keywords_literals_and_chained_call_parts()
    {
        Assert.Equal(["a", "b", "c"], OgnlLite.References("a eq b or not c").Select(r => r.Path));
        Assert.Equal(["ename", "x"], OgnlLite.References("ename == 'a.b' and x == \"y z\" and true and null").Select(r => r.Path));
        Assert.Equal(["blank"], OgnlLite.References("blank.trim().length() == 0").Select(r => r.Path));
        Assert.Equal(["list2.size"], OgnlLite.References("list2.size == 2").Select(r => r.Path));
        Assert.Equal(["_parameter.ename"], OgnlLite.References("_parameter.ename == 'KIM'").Select(r => r.Path));
    }

    [Fact]
    public void References_reads_as_far_as_tokens_go_for_unevaluable_expressions()
    {
        Assert.Equal(["n1", "ename"], OgnlLite.References("n1 + 1 == 2 and ename.toUpperCase() == 'K'").Select(r => r.Path));
        Assert.Equal(["n1", "x"], OgnlLite.References("@java.lang.Math@max(1, 2) == n1 and #this != x").Select(r => r.Path));
        Assert.Equal(["ename"], OgnlLite.References("ename == 'KIM' and").Select(r => r.Path));
        Assert.Empty(OgnlLite.References(string.Empty));
    }

    [Fact]
    public void Repeated_evaluation_uses_the_cached_parse_and_stays_correct()
    {
        for (int i = 0; i < 3; i++)
        {
            AssertResult(true, "ename != null and ename != ''");
            Assert.False(Run("n1 + 1 == 2").Ok);
        }
    }
}
