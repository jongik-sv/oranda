using System.Diagnostics;
using System.Reflection;
using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Tests.Formatting;

// 소유: TSK-05-02. 쉼표·줄바꿈·정렬·제외 구간(FMT-04·05·06·10).
public class FormatterLayoutTests
{
    static ISqlFormatter Formatter()
    {
        var type = typeof(LanguageAssembly).Assembly.GetType("Oranda.Language.Formatting.SqlFormatter")!;
        return (ISqlFormatter)Activator.CreateInstance(type, nonPublic: true)!;
    }

    static string Fmt(string sql, FormatterOptions o)
    {
        var r = Formatter().Format(sql, o);
        Assert.True(r.Succeeded, $"{r.Failure}: {r.Message}");
        return r.Text;
    }

    static FormatterOptions Plain(AlignOptions a) => new() { Align = a };

    static readonly AlignOptions NoAlign = new() { Alias = false, Assignment = false, ColumnDefinition = false, EqualsSign = false };

    static readonly LineBreakOptions Fill = new() { SelectList = SelectListLayout.FillToWidth };

    // ---- 쉼표(FMT-04) ----

    [Fact]
    public void Select_list_one_per_line_with_trailing_or_leading_comma()
    {
        const string sql = "select a, b, f(x, y) from t";
        var trailing = new FormatterOptions { Align = NoAlign };
        Assert.Equal("SELECT a,\n    b,\n    f(x, y)\nFROM t", Fmt(sql, trailing));
        var leading = new FormatterOptions { Align = NoAlign, CommaPosition = CommaPosition.Leading };
        Assert.Equal("SELECT a\n    , b\n    , f(x, y)\nFROM t", Fmt(sql, leading));
    }

    [Fact]
    public void Select_list_fill_to_width_wraps_at_max_width()
    {
        const string sql = "select aaaaaaaa, bbbbbbbb, cccccccc, dddddddd, eeeeeeee from t";
        var lb = new LineBreakOptions { SelectList = SelectListLayout.FillToWidth, MaxLineWidth = 40 };
        var o = new FormatterOptions { Align = NoAlign, LineBreaks = lb };
        Assert.Equal("SELECT aaaaaaaa, bbbbbbbb, cccccccc,\n    dddddddd, eeeeeeee\nFROM t", Fmt(sql, o));
        var leading = new FormatterOptions { Align = NoAlign, CommaPosition = CommaPosition.Leading, LineBreaks = lb };
        Assert.Equal("SELECT aaaaaaaa, bbbbbbbb, cccccccc\n    , dddddddd, eeeeeeee\nFROM t", Fmt(sql, leading));
    }

    [Fact]
    public void Function_arguments_wrap_at_max_width()
    {
        const string sql = "select concat(aaaaaaaaaaaa, bbbbbbbbbbbb, cccccccccccc, dddddddddddd) from t";
        var o = new FormatterOptions { Align = NoAlign, LineBreaks = new LineBreakOptions { MaxLineWidth = 40 } };
        Assert.Equal("SELECT CONCAT(aaaaaaaaaaaa,\n    bbbbbbbbbbbb, cccccccccccc,\n    dddddddddddd)\nFROM t", Fmt(sql, o));
    }

    [Fact]
    public void Operator_spacing_and_paren_inner_space()
    {
        const string sql = "select a+b*2, f(x) from t where a>=1 and b<>-1 and c=:p";
        var tight = new FormatterOptions { Align = NoAlign, OperatorSpacing = false, LineBreaks = Fill };
        Assert.Equal("SELECT a+b*2, f(x)\nFROM t\nWHERE a>=1\n    AND b<>-1\n    AND c=:p", Fmt(sql, tight));
        var spaced = new FormatterOptions { Align = NoAlign, ParenInnerSpace = true, LineBreaks = Fill };
        Assert.Equal("SELECT a + b * 2, f( x )\nFROM t\nWHERE a >= 1\n    AND b <> -1\n    AND c = :p", Fmt(sql, spaced));
        Assert.Equal("SELECT COUNT( * )\nFROM t\nWHERE a(+) = b", Fmt("select count(*) from t where a(+)=b", spaced));
    }

    [Fact]
    public void Operator_spacing_off_keeps_tokens_that_would_merge_apart()
    {
        var o = new FormatterOptions { Align = NoAlign, OperatorSpacing = false, LineBreaks = Fill };
        Assert.Equal("SELECT 3- -4, a-b\nFROM t", Fmt("select 3 - -4, a - b from t", o));
    }

    // ---- 줄바꿈(FMT-05) ----

    [Fact]
    public void Join_clauses_break_per_join()
    {
        const string sql = "select 1 from a inner join b on a.x = b.x left outer join c on c.y = a.y cross join d, e";
        var on = new FormatterOptions { Align = NoAlign };
        Assert.Equal("SELECT 1\nFROM a\nINNER JOIN b ON a.x = b.x\nLEFT OUTER JOIN c ON c.y = a.y\nCROSS JOIN d, e", Fmt(sql, on));
        var off = new FormatterOptions { Align = NoAlign, LineBreaks = new LineBreakOptions { JoinClause = false } };
        Assert.Equal("SELECT 1\nFROM a INNER JOIN b ON a.x = b.x LEFT OUTER JOIN c ON c.y = a.y CROSS JOIN d, e", Fmt(sql, off));
    }

    [Fact]
    public void And_or_break_before_after_or_never()
    {
        const string sql = "select 1 from t where a = 1 and b between 1 and 2 or (c = 1 and d = 2) and e = 3";
        Assert.Equal(
            "SELECT 1\nFROM t\nWHERE a = 1\n    AND b BETWEEN 1 AND 2\n    OR (c = 1 AND d = 2)\n    AND e = 3",
            Fmt(sql, new FormatterOptions { Align = NoAlign }));
        Assert.Equal(
            "SELECT 1\nFROM t\nWHERE a = 1 AND\n    b BETWEEN 1 AND 2 OR\n    (c = 1 AND d = 2) AND\n    e = 3",
            Fmt(sql, new FormatterOptions { Align = NoAlign, LineBreaks = new LineBreakOptions { AndOr = AndOrBreak.After } }));
        Assert.Equal(
            "SELECT 1\nFROM t\nWHERE a = 1 AND b BETWEEN 1 AND 2 OR (c = 1 AND d = 2) AND e = 3",
            Fmt(sql, new FormatterOptions { Align = NoAlign, LineBreaks = new LineBreakOptions { AndOr = AndOrBreak.None } }));
    }

    [Fact]
    public void And_in_join_condition_and_nested_subquery_indent_follow_the_line()
    {
        const string sql = "select 1 from a join b on a.x = b.x and a.y = b.y where a.z in (select z from c where c.k = 1 and c.j = 2) and a.w = 1";
        Assert.Equal(
            "SELECT 1\nFROM a\nJOIN b ON a.x = b.x\n    AND a.y = b.y\nWHERE a.z IN (\n    SELECT z\n    FROM c\n    WHERE c.k = 1\n        AND c.j = 2\n)\n    AND a.w = 1",
            Fmt(sql, new FormatterOptions { Align = NoAlign }));
    }

    [Fact]
    public void In_list_wraps_above_threshold()
    {
        const string sql = "select 1 from t where x in (1, 2, 3, 4) and y not in (1, 2, 3, 4, 5, 6)";
        var five = new FormatterOptions { Align = NoAlign };
        Assert.Equal(
            "SELECT 1\nFROM t\nWHERE x IN (1, 2, 3, 4)\n    AND y NOT IN (\n        1,\n        2,\n        3,\n        4,\n        5,\n        6\n    )",
            Fmt(sql, five));
        var three = new FormatterOptions { Align = NoAlign, LineBreaks = new LineBreakOptions { InListThreshold = 3 }, CommaPosition = CommaPosition.Leading };
        Assert.Equal(
            "SELECT 1\nFROM t\nWHERE x IN (\n    1\n    , 2\n    , 3\n    , 4\n)\n    AND y NOT IN (\n        1\n        , 2\n        , 3\n        , 4\n        , 5\n        , 6\n    )",
            Fmt(sql, three));
        var off = new FormatterOptions { Align = NoAlign, LineBreaks = new LineBreakOptions { InListThreshold = 0 } };
        Assert.Equal("SELECT 1\nFROM t\nWHERE x IN (1, 2, 3, 4)\n    AND y NOT IN (1, 2, 3, 4, 5, 6)", Fmt(sql, off));
    }

    [Fact]
    public void Breaks_around_comments_are_left_to_the_base_layout()
    {
        const string sql = "select a, -- first\n  b /* mid */, c from t where x = 1 -- end\n and y = 2";
        var text = Fmt(sql, new FormatterOptions { Align = NoAlign });
        Assert.Equal("SELECT a, -- first\n    b /* mid */ , c\nFROM t\nWHERE x = 1 -- end\n    AND y = 2", text);
    }

    // ---- 정렬(FMT-06) ----

    [Fact]
    public void Alias_alignment()
    {
        const string sql = "select a, bbbb as b2, c cc, sum(x) total from t";
        Assert.Equal(
            "SELECT a,\n    bbbb   AS b2,\n    c      cc,\n    SUM(x) total\nFROM t",
            Fmt(sql, Plain(new AlignOptions { Alias = true, Assignment = false, ColumnDefinition = false })));
        Assert.Equal(
            "SELECT a,\n    bbbb AS b2,\n    c cc,\n    SUM(x) total\nFROM t",
            Fmt(sql, Plain(NoAlign)));
    }

    [Fact]
    public void Assignment_alignment_in_plsql()
    {
        const string sql = "declare v_a number := 1; v_long_name varchar2(10) := 'x'; begin v_a := 2; v_long_name := 'abc'; end;";
        Assert.Equal(
            "DECLARE\n    v_a NUMBER               := 1;\n    v_long_name VARCHAR2(10) := 'x';\nBEGIN\n    v_a         := 2;\n    v_long_name := 'abc';\nEND;",
            Fmt(sql, Plain(new AlignOptions { Alias = false, Assignment = true, ColumnDefinition = false })));
        Assert.Equal(
            "DECLARE\n    v_a NUMBER := 1;\n    v_long_name VARCHAR2(10) := 'x';\nBEGIN\n    v_a := 2;\n    v_long_name := 'abc';\nEND;",
            Fmt(sql, Plain(NoAlign)));
    }

    [Fact]
    public void Column_definitions_break_and_align()
    {
        const string sql = "create table x (a number(10,2) primary key, bcdef varchar2(20 char) not null, c date default sysdate, constraint pk check (a > 0))";
        Assert.Equal(
            "CREATE TABLE x (\n    a     NUMBER(10, 2) PRIMARY KEY,\n    bcdef VARCHAR2(20 CHAR) NOT NULL,\n    c     DATE DEFAULT SYSDATE,\n    CONSTRAINT pk CHECK (a > 0)\n)",
            Fmt(sql, Plain(new AlignOptions { Alias = false, Assignment = false, ColumnDefinition = true })));
        Assert.Equal(
            "CREATE TABLE x (a NUMBER(10, 2) PRIMARY KEY, bcdef VARCHAR2(20 CHAR) NOT NULL, c DATE DEFAULT SYSDATE,\n    CONSTRAINT pk CHECK (a > 0))",
            Fmt(sql, Plain(NoAlign)));
    }

    [Fact]
    public void Equals_alignment_in_set_and_where()
    {
        const string sql = "update t set a = 1, bbb = 2, cc = 3 where x = 1 and yyyy = 2";
        var o = Plain(new AlignOptions { Alias = false, Assignment = false, ColumnDefinition = false, EqualsSign = true });
        Assert.Equal("UPDATE t\nSET a   = 1,\n    bbb = 2,\n    cc  = 3\nWHERE x      = 1\n    AND yyyy = 2", Fmt(sql, o));
        Assert.Equal("UPDATE t\nSET a = 1, bbb = 2, cc = 3\nWHERE x = 1\n    AND yyyy = 2", Fmt(sql, Plain(NoAlign)));
    }

    // ---- 제외 구간(FMT-10) ----

    [Fact]
    public void Formatter_off_on_keeps_region_verbatim()
    {
        const string sql = "select   a  from t;\n-- @formatter:off\nselect   x,y\n    from   u  where  z=1;\n\n\n  insert   into v values(1,2);\n-- @formatter:on\nselect   b  from t";
        var o = new FormatterOptions { Align = NoAlign };
        Assert.Equal(
            "SELECT a\nFROM t;\n-- @formatter:off\nselect   x,y\n    from   u  where  z=1;\n\n\n  insert   into v values(1,2);\n-- @formatter:on\nSELECT b\nFROM t",
            Fmt(sql, o));
        string crlf = sql.Replace("\n", "\r\n", StringComparison.Ordinal);
        var r = Formatter().Format(crlf, o);
        Assert.True(r.Succeeded, $"{r.Failure}: {r.Message}");
        Assert.Contains("select   x,y\r\n    from   u  where  z=1;\r\n\r\n\r\n  insert   into v values(1,2);\r\n-- @formatter:on", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatter_off_without_on_runs_to_the_end_and_can_be_ignored()
    {
        const string sql = "select   a  from t;\n/* @formatter:off */\nselect   x   from   u";
        var o = new FormatterOptions { Align = NoAlign };
        Assert.Equal("SELECT a\nFROM t;\n/* @formatter:off */\nselect   x   from   u", Fmt(sql, o));
        var ignore = new FormatterOptions { Align = NoAlign, HonorFormatterOff = false };
        Assert.Equal("SELECT a\nFROM t;\n/* @formatter:off */\nSELECT x\nFROM u", Fmt(sql, ignore));
    }

    [Fact]
    public void Formatter_off_inside_a_statement_keeps_indent_and_blank_lines()
    {
        const string sql = "select a,\n-- @formatter:off\n       b,   c\n\n  ,d\n-- @formatter:on\n from t";
        var o = new FormatterOptions { Align = NoAlign };
        var r = Formatter().Format(sql, o);
        Assert.True(r.Succeeded, $"{r.Failure}: {r.Message}");
        Assert.Contains("-- @formatter:off\n       b,   c\n\n  ,d\n-- @formatter:on", r.Text, StringComparison.Ordinal);
    }

    // ---- 회귀 예제(옵션 x 원본) ----

    static readonly string[] ExtraSamples =
    [
        "select a, bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb as b2, c cc, case when a = 1 and b = 2 then 'x' else 'y' end as cs, sum(d) over (partition by a, b order by c) total from t1 a inner join t2 b on a.id = b.id and a.x = b.x left join t3 c on c.id = a.id cross join t4 where a.x in (1, 2, 3, 4, 5, 6, 7, 8) and b.y between 1 and 2 or (c.z = 1 and d.w = 2) group by a, b having count(*) > 1 order by a, b",
        "select 1 from t where x in (select y from u where a = 1 and b = 2 and c in (1,2,3,4,5,6,7)) and z = (select max(q) from v where a = 1 and b = 2)",
        "create table x (a number(10,2) primary key, bcdef varchar2(20 char) not null, c date default sysdate, constraint pk check (a > 0));",
        "create table y (a number, b varchar2(10)) tablespace users",
        "create table z as select a, b from t where a = 1 and b = 2",
        "declare\n  v_a number := 1;\n  v_long_name varchar2(10) := 'x';\n  c_k constant number := 5;\nbegin\n  v_a := 2;\n  v_long_name := 'abc';\n\n  v_a := v_a + 1;\n  for r in (select a, b from t where x = 1 and y = 2) loop\n    v_a := r.a;\n  end loop;\nend;",
        "update t set a = 1, bbb = 2, cc = (select max(x) from u where u.a = 1 and u.b = 2) where x = 1 and yyyy = 2 returning a into :a",
        "delete from t where a = 1 and b = 2 or c = 3",
        "merge into t using (select a, b from s where x = 1 and y = 2) s on (t.id = s.id and t.k = s.k) when matched then update set t.a = s.a, t.b = s.b where s.x = 1 and s.y = 2 when not matched then insert (a, b) values (s.a, s.b)",
        "insert into t (a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q, r, s, t2, u, v, w, x, y, z) values (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26)",
        "select a, -- first\n  b /* mid */, c from t where x = 1 -- end\n and y = 2 /* tail */ and z in (1,2,3,4,5,6) -- x\n",
        "select a,\n\n  b,\n\n\n  c from t\n\nwhere x = 1\n\n  and y = 2",
        "select a, b\nfrom t\n-- @formatter:off\nwhere   x=1\n    and  y = 2\n-- @formatter:on\norder by a, b;\n\n-- @formatter:off\nselect   1,2   from dual;\n  update t   set a=1;\n-- @formatter:on\nselect 3 from dual",
        "begin\n  -- @formatter:off\n  x  :=   1;\n  y := 2;\n  -- @formatter:on\n  z := 3;\n  w     := 4;\nend;",
        "select x /* @formatter:off */, y   ,   z /* @formatter:on */, w from t",
        "-- @formatter:off\nselect   1   from   dual",
        "select a || b, a-b, a - -b, - -1, 3*-2, x/y, count(*), t.*, a(+) from t1, t2 where t1.a(+) = t2.a and :p<>1",
        "select f(a,b,c)(1), g(), h( ) from t where (a) = (b) and ((a)) in ((1),(2))",
        "select * from t where a = 1\nand b = 2\n/\nselect * from t2 where x in (1,2,3,4,5,6)\n/",
        "set serveroutput on\nselect a, b from t where x = 1 and y = 2;\nprompt done",
        "select a, b from t1 natural join t2 full outer join t3 on t3.a = t1.a right join t4 using (a) outer apply f(1)",
        "with a as (select 1 x, 2 y from dual), b as (select x, y from a where x = 1 and y = 2) select x, y from b",
        "select a, b from t where exists (select 1 from u where u.a = t.a and u.b = t.b) and not exists (select 1 from v where v.a = t.a)",
        "select case a when 1 then 'x' when 2 then 'y' else 'z' end c1, case when b = 1 and c = 2 then 1 else 0 end c2 from t",
        "select aaaaaaaaaaaa, bbbbbbbbbbbb, cccccccccccc, dddddddddddd, eeeeeeeeeeee, ffffffffffff, gggggggggggg, hhhhhhhhhhhh from t where concat(aaaaaaaaaaaa, bbbbbbbbbbbb, cccccccccccc, dddddddddddd, eeeeeeeeeeee) = 'x'",
    ];

    static IEnumerable<FormatterOptions> Variants()
    {
        var rnd = new Random(20261124);
        var cases = new[] { LetterCase.Upper, LetterCase.Lower, LetterCase.AsIs };
        for (int n = 0; n < 144; n++)
        {
            bool flag = rnd.Next(2) == 1;
            yield return new FormatterOptions
            {
                Case = new CaseOptions { Keyword = cases[n % 3], Function = cases[(n / 3) % 3], Identifier = cases[(n / 9) % 3] },
                Indent = new IndentOptions { UseTabs = n % 11 == 0, Size = new[] { 2, 4, 8 }[n % 3], Subquery = n % 5 % 3, Case = n % 4, PlsqlBlock = n % 3 },
                CommaPosition = flag ? CommaPosition.Leading : CommaPosition.Trailing,
                OperatorSpacing = rnd.Next(2) == 1,
                ParenInnerSpace = rnd.Next(2) == 1,
                LineBreaks = new LineBreakOptions
                {
                    SelectList = rnd.Next(2) == 1 ? SelectListLayout.FillToWidth : SelectListLayout.OnePerLine,
                    JoinClause = rnd.Next(2) == 1,
                    AndOr = (AndOrBreak)rnd.Next(3),
                    InListThreshold = new[] { 0, 1, 3, 5, 9 }[rnd.Next(5)],
                    MaxLineWidth = new[] { 40, 60, 120, 400 }[rnd.Next(4)],
                },
                Align = new AlignOptions
                {
                    Alias = rnd.Next(2) == 1,
                    Assignment = rnd.Next(2) == 1,
                    ColumnDefinition = rnd.Next(2) == 1,
                    EqualsSign = rnd.Next(2) == 1,
                },
                Comments = new CommentOptions { Preserve = n % 7 != 0, MaxBlankLines = n % 4 },
                StatementTerminator = (StatementTerminator)(n % 3),
                HonorFormatterOff = n % 13 != 0,
            };
        }

        yield return new FormatterOptions { CommaPosition = CommaPosition.Leading };
        yield return new FormatterOptions { OperatorSpacing = false, ParenInnerSpace = true };
        yield return new FormatterOptions { LineBreaks = new LineBreakOptions { SelectList = SelectListLayout.FillToWidth, MaxLineWidth = 40, AndOr = AndOrBreak.After } };
        yield return new FormatterOptions { Align = new AlignOptions { EqualsSign = true } };
    }

    [Fact]
    public void Option_combinations_keep_tokens_comments_and_idempotency()
    {
        var f = Formatter();
        var failures = new List<string>();
        int cases = 0;
        string[] all = [.. FormatterBasicTests.Samples, .. ExtraSamples];
        foreach (var o in Variants())
        {
            foreach (var raw in all)
            {
                foreach (var sql in new[] { raw, raw.Replace("\n", "\r\n", StringComparison.Ordinal) })
                {
                    cases++;
                    var r = f.Format(sql, o);
                    if (!r.Succeeded)
                    {
                        failures.Add($"{r.Failure} {r.Message} :: {sql}");
                        continue;
                    }

                    if (o.StatementTerminator == StatementTerminator.Keep && !SameTokens(sql, r.Text, out var why)) failures.Add($"tokens {why} :: {sql}");
                    if (!SameComments(sql, r.Text)) failures.Add($"comments :: {sql}");
                    var again = f.Format(r.Text, o);
                    if (again.Text != r.Text) failures.Add($"idempotent :: {sql}\n--- 1\n{r.Text}\n--- 2\n{again.Text}");
                }
            }
        }

        Assert.True(cases >= 10000, $"회귀 예제 {cases}건");
        Assert.True(failures.Count == 0, $"{failures.Count}건\n" + string.Join("\n\n", failures.Take(6)));
    }

    [Fact]
    public void Statement_ranges_with_extended_options_keep_tokens()
    {
        var f = Formatter();
        var failures = new List<string>();
        var o = new FormatterOptions { CommaPosition = CommaPosition.Leading, Align = new AlignOptions { EqualsSign = true } };
        foreach (var sql in ExtraSamples)
        {
            foreach (var st in StatementSplitter.Split(sql, Core.Language.SplitOptions.Default))
            {
                var r = f.Format(sql, o, st.Range);
                if (!r.Succeeded) { failures.Add($"{r.Failure} {r.Message} :: {sql}"); continue; }
                if (!SameTokens(sql, r.Text, out var why)) failures.Add($"tokens {why} :: {sql}");
                if (f.Format(r.Text, o, r.Range).Text != r.Text) failures.Add($"idempotent :: {sql}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures.Take(5)));
    }

    static bool SameTokens(string a, string b, out string why)
    {
        var x = Code(a);
        var y = Code(b);
        why = "";
        if (x.Count != y.Count)
        {
            why = $"count {x.Count} != {y.Count}";
            return false;
        }

        for (int i = 0; i < x.Count; i++)
        {
            var cmp = x[i].Kind is TokenKind.Keyword or TokenKind.Identifier ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (x[i].Kind != y[i].Kind || !string.Equals(x[i].Text, y[i].Text, cmp))
            {
                why = $"'{x[i].Text}' != '{y[i].Text}'";
                return false;
            }
        }

        return true;
    }

    static List<(TokenKind Kind, string Text)> Code(string s) =>
        SqlLexer.Tokenize(s).Where(t => t.Kind != TokenKind.Comment)
            .Select(t => (t.Kind, s.Substring(t.Start, t.Length).TrimEnd('\r'))).ToList();

    static bool SameComments(string a, string b)
    {
        static List<string> C(string s) =>
            SqlLexer.Tokenize(s).Where(t => t.Kind == TokenKind.Comment).Select(t => s.Substring(t.Start, t.Length).Trim()).ToList();
        return C(a).SequenceEqual(C(b));
    }

    // ---- 성능 ----

    [Trait("Category", "Perf")]
    [Fact]
    public void Formats_2000_lines_with_extended_options_within_500ms()
    {
        var sb = new System.Text.StringBuilder();
        int lines = 0;
        while (lines < 2000)
        {
            foreach (var s in FormatterBasicTests.Samples.Concat(ExtraSamples))
            {
                sb.Append(s).Append(";\n");
                lines += s.Count(c => c == '\n') + 1;
            }
        }

        string sql = sb.ToString();
        var f = Formatter();
        var o = new FormatterOptions { Align = new AlignOptions { EqualsSign = true }, CommaPosition = CommaPosition.Leading };
        for (int i = 0; i < 3; i++) f.Format(sql, o);
        long best = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = f.Format(sql, o);
            sw.Stop();
            Assert.True(r.Succeeded, $"{r.Failure}: {r.Message}");
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }

        // 인수 기준 500 ms 는 최적화 빌드 기준이다. Debug(JIT 최적화 끔)·병렬 시험 부하에서는 3배 여유를 둔다.
        bool optimized = typeof(LanguageAssembly).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;
        long limit = optimized ? 500 : 1500;
        Assert.True(best <= limit, $"{lines}줄 {best} ms (한도 {limit} ms)");
    }
}
