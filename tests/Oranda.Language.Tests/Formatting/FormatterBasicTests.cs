using System.Diagnostics;
using System.Reflection;
using Oranda.Core.Formatting;
using Oranda.Core.Text;
using Oranda.Language.Lexing;

namespace Oranda.Language.Tests.Formatting;

// 소유: TSK-05-01. 대소문자·들여쓰기·주석 보존·토큰 동일성·멱등(FMT-01·02·03·07·11).
public class FormatterBasicTests
{
    static readonly FormatterOptions Default = new();

    static ISqlFormatter Formatter()
    {
        var type = typeof(LanguageAssembly).Assembly.GetType("Oranda.Language.Formatting.SqlFormatter")!;
        return (ISqlFormatter)Activator.CreateInstance(type, nonPublic: true)!;
    }

    static string Fmt(string sql, FormatterOptions? o = null)
    {
        var r = Formatter().Format(sql, o ?? Default);
        Assert.True(r.Succeeded, $"{r.Failure}: {r.Message}");
        return r.Text;
    }

    static string Lf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    // ---- 회귀 예제 ----

    /// <summary>회귀 예제 원본. 옵션 조합과 곱해 1,000건 이상을 만든다.</summary>
    public static readonly string[] Samples =
    [
        "select a, b,c from emp e where e.id = 1 and e.name like 'x%' order by a",
        "select count(*), nvl(x,0) from t1 join t2 on t1.id=t2.id where t1.x in (select y from t3 where z = -1) group by a having count(*)>1",
        "-- header comment\nselect a /* inline */ , b -- trailing\nfrom t;\n\n\nupdate t set a = 1, b = :b where c = &sub;\ndelete from t where x = 1;",
        "select case when a = 1 then 'x' when a = 2 then 'y' else 'z' end as c, decode(b, 1, 2) from t\nunion all\nselect 'q', 1 from dual",
        "insert into t (a, b) values (1, 'x');\nmerge into t using s on (t.id = s.id) when matched then update set t.a = s.a when not matched then insert (a) values (s.a);",
        "create table x (a number(10,2) primary key, b varchar2(20 char) not null, c date default sysdate);",
        "SELECT q'[it''s]', 'multi\nline string', \"Quoted Col\" FROM dual;",
        "with a as (select 1 x from dual), b as (select x from a) select * from b where x > 0",
        "select e.ename, d.dname from emp e, dept d where e.deptno = d.deptno(+) and e.sal between 1000 and 2000",
        "select level, sys_connect_by_path(ename, '/') from emp start with mgr is null connect by prior empno = mgr order siblings by ename",
        "select deptno, listagg(ename, ',') within group (order by ename) names from emp group by deptno",
        "select empno, row_number() over (partition by deptno order by sal desc) rn from emp fetch first 10 rows only",
        "select * from t for update nowait",
        "select a.* , b.x from a left outer join b on a.id = b.id where not exists (select 1 from c where c.id = a.id)",
        "select /*+ full(t) parallel(4) */ count(1) from big_table t",
        "select to_char(sysdate, 'YYYY-MM-DD'), systimestamp, current_date from dual",
        "select -1, +2, 3 - -4, a*b/c, x || y from dual",
        "select 1e10, .5, 3.14f, 0x from dual",
        "update emp set sal = sal * 1.1, comm = (select avg(comm) from emp) where deptno = 10 returning sal into :new_sal",
        "delete emp where empno in (1, 2, 3)",
        "insert all into t1 (a) values (1) into t2 (b) values (2) select * from dual",
        "insert into t select * from s where s.x = 1",
        "create view v as select a, b from t where c = 1",
        "create index ix_t on t (a, b)",
        "alter table t add (c number)",
        "grant select, insert on t to scott",
        "comment on table t is 'a table'",
        "truncate table t",
        "commit",
        "explain plan for select * from t",
        "select * from t1 natural join t2 cross join t3",
        "select x from t where a = &&var and b = &1 and c = :1 and d = &tab..col",
        "select &cols from &tab where &cond",
        "select 'a' || &suffix || 'b' from dual",
        "begin null; end;",
        "declare\n  v number;\nbegin\n  select count(*) into v from t;\n  if v > 0 then\n    dbms_output.put_line(v);\n  end if;\nexception when others then null;\nend;",
        "create or replace procedure p(a number, b out varchar2) is\n  v number := 0;\n  cursor c is select * from t where x = a;\nbegin\n  for r in c loop\n    if r.x > 0 then v := v + 1; elsif r.x < 0 then v := v - 1; else null; end if;\n  end loop;\n  case v when 1 then b := 'a'; else b := 'b'; end case;\nexception\n  when no_data_found then null;\n  when others then raise;\nend p;\n/",
        "create or replace function f(x in number) return number deterministic is\nbegin\n  return case when x > 0 then 1 else 0 end;\nend f;\n/",
        "create or replace package pk as\n  procedure a;\n  function b return number;\n  c constant number := 1;\nend pk;\n/",
        "create or replace package body pk as\n  procedure a is begin null; end a;\n  function b return number is\n    v number;\n  begin\n    select 1 into v from dual;\n    return v;\n  end b;\nend pk;\n/",
        "create or replace trigger trg before insert or update on t for each row\nbegin\n  :new.updated := sysdate;\n  if inserting then :new.created := sysdate; end if;\nend;\n/",
        "declare\n  type t_tab is table of emp%rowtype index by pls_integer;\n  l t_tab;\nbegin\n  select * bulk collect into l from emp;\n  forall i in 1..l.count insert into emp_copy values l(i);\n  for i in 1 .. l.count loop\n    exit when i > 10;\n    continue;\n  end loop;\n  while true loop exit; end loop;\n  loop null; exit; end loop;\nend;",
        "begin\n  <<outer>>\n  for i in 1..3 loop\n    null;\n  end loop outer;\n  execute immediate 'select 1 from dual' into x;\n  open c for select * from t;\n  fetch c into r;\n  close c;\n  x := sql%rowcount;\nend;",
        "begin\n  begin\n    raise_application_error(-20001, 'err');\n  exception\n    when others then\n      -- swallow\n      null;\n  end;\nend;",
        "select a from t where (a = 1",
        "select a from t where a = 1)",
        "begin if x then null; end;",
        "select 1 from dual;\nselect 2 from dual;\n\n\n\nselect 3 from dual;",
        "/* block\n   comment */\nselect 1 -- one\n  , 2 /* two */\nfrom dual\n-- tail\n;",
        "select a,\n\n  b\nfrom t",
        "SeLeCt Foo, \"Bar\", BAZ.Qux FrOm Some_Table St WhErE St.Col = 'MiXeD'",
        "select t.type, t.comment, t.date, x.count from t",
        "select json_value(doc, '$.a' returning number) from docs",
        "select * from t pivot (sum(x) for y in (1 as a, 2 as b))",
        "select interval '1' day, date '2020-01-01', timestamp '2020-01-01 00:00:00' from dual",
        "select cast(x as varchar2(10)), extract(year from sysdate), trim(leading '0' from s) from t",
        "select * from t where a in (select b from u union select c from v) and rownum <= 10",
        "lock table t in exclusive mode nowait",
        "call p(1, 2)",
        "savepoint s1",
        "rollback to savepoint s1",
    ];

    static IEnumerable<FormatterOptions> OptionVariants()
    {
        var cases = new[] { LetterCase.Upper, LetterCase.Lower, LetterCase.AsIs };
        foreach (var k in cases)
            foreach (var fn in cases)
                foreach (var id in cases)
                    yield return new FormatterOptions { Case = new CaseOptions { Keyword = k, Function = fn, Identifier = id } };

        yield return new FormatterOptions { Indent = new IndentOptions { UseTabs = true } };
        yield return new FormatterOptions { Indent = new IndentOptions { Size = 2, Subquery = 0, Case = 2, PlsqlBlock = 2 } };
        yield return new FormatterOptions { Indent = new IndentOptions { Size = 16, Subquery = 4, Case = 0, PlsqlBlock = 0 } };
        yield return new FormatterOptions { Comments = new CommentOptions { MaxBlankLines = 0 } };
        yield return new FormatterOptions { Comments = new CommentOptions { MaxBlankLines = 5, Preserve = false } };
        yield return new FormatterOptions { StatementTerminator = StatementTerminator.Add };
        yield return new FormatterOptions { StatementTerminator = StatementTerminator.Remove };
    }

    [Fact]
    public void Regression_corpus_keeps_tokens_and_is_idempotent()
    {
        var f = Formatter();
        var failures = new List<string>();
        int cases = 0;
        foreach (var o in OptionVariants())
        {
            foreach (var raw in Samples)
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

                    bool terminatorEdit = o.StatementTerminator != StatementTerminator.Keep;
                    if (!terminatorEdit && !SameTokens(sql, r.Text, out var why)) failures.Add($"tokens {why} :: {sql}");
                    if (!SameComments(sql, r.Text)) failures.Add($"comments :: {sql}");
                    var again = f.Format(r.Text, o);
                    if (again.Text != r.Text) failures.Add($"idempotent :: {sql}\n--- 1\n{r.Text}\n--- 2\n{again.Text}");
                }
            }
        }

        Assert.True(cases >= 1000, $"회귀 예제 {cases}건");
        Assert.True(failures.Count == 0, string.Join("\n\n", failures.Take(10)));
    }

    [Fact]
    public void Regression_corpus_statement_ranges_keep_tokens()
    {
        var f = Formatter();
        var failures = new List<string>();
        foreach (var sql in Samples)
        {
            foreach (var st in StatementSplitter.Split(sql, Core.Language.SplitOptions.Default))
            {
                var r = f.Format(sql, Default, st.Range);
                if (!r.Succeeded) { failures.Add($"{r.Failure} {r.Message} :: {sql}"); continue; }
                if (!SameTokens(sql, r.Text, out var why)) failures.Add($"tokens {why} :: {sql}");
                Assert.Equal(sql[..r.Range.Start], r.Text[..r.Range.Start]);
                Assert.EndsWith(sql[r.Range.End..], r.Text, StringComparison.Ordinal);
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures.Take(10)));
    }

    [Fact]
    public void Fuzzed_fragments_never_break_tokens_or_idempotency()
    {
        // 예제를 아무 곳에서나 자르거나 토큰을 섞은 깨진 입력도 검증을 통과해야 한다(원문 유지로 물러서더라도).
        var f = Formatter();
        var rnd = new Random(7);
        string all = string.Join(";\n", Samples);
        var toks = SqlLexer.Tokenize(all).Select(t => all.Substring(t.Start, t.Length)).ToList();
        string[] seps = ["\n", "", "\n\n", " ", " "];
        var failures = new List<string>();
        for (int n = 0; n < 3000; n++)
        {
            string s;
            if (n % 2 == 0)
            {
                int a = rnd.Next(all.Length);
                s = all[a..Math.Min(all.Length, a + rnd.Next(400))];
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                for (int i = rnd.Next(1, 40); i > 0; i--) sb.Append(toks[rnd.Next(toks.Count)]).Append(seps[rnd.Next(seps.Length)]);
                s = sb.ToString();
            }

            var o = new FormatterOptions { StatementTerminator = (StatementTerminator)(n % 3) };
            var r = f.Format(s, o);
            if (!r.Succeeded) failures.Add($"{r.Failure} {r.Message} :: {s}");
            else if (f.Format(r.Text, o).Text != r.Text) failures.Add($"idempotent :: {s}");
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures.Take(5)));
    }

    /// <summary>렉서로 독립 검사: 주석을 뺀 토큰 열이 같다(낱말은 대소문자 무시).</summary>
    static bool SameTokens(string a, string b, out string why)
    {
        var x = Code(a);
        var y = Code(b);
        why = "";
        if (x.Count != y.Count) { why = $"count {x.Count} != {y.Count}"; return false; }
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

    // ---- 대소문자(FMT-02) ----

    [Fact]
    public void Keyword_function_identifier_case_are_separate()
    {
        const string sql = "select Nvl(Col1, 0), Count(*), My_Func(x), sysdate from Emp where rownum < 2";
        var upperKw = new FormatterOptions { Case = new CaseOptions { Keyword = LetterCase.Upper, Function = LetterCase.Lower, Identifier = LetterCase.AsIs } };
        Assert.Equal("SELECT nvl(Col1, 0),\n    count(*),\n    My_Func(x),\n    sysdate\nFROM Emp\nWHERE ROWNUM < 2", Fmt(sql, upperKw));

        var lowerKw = new FormatterOptions { Case = new CaseOptions { Keyword = LetterCase.Lower, Function = LetterCase.Upper, Identifier = LetterCase.Upper } };
        Assert.Equal("select NVL(COL1, 0),\n    COUNT(*),\n    MY_FUNC(X),\n    SYSDATE\nfrom EMP\nwhere rownum < 2", Fmt(sql, lowerKw));
    }

    [Fact]
    public void Quoted_identifiers_strings_and_member_names_keep_case()
    {
        const string sql = "select \"MixedCase\", 'Text', t.Type, t.Comment from t";
        var o = new FormatterOptions { Case = new CaseOptions { Keyword = LetterCase.Upper, Identifier = LetterCase.Lower } };
        Assert.Equal("SELECT \"MixedCase\",\n    'Text',\n    t.type,\n    t.comment\nFROM t", Fmt(sql, o));
    }

    [Fact]
    public void Plsql_attributes_after_percent_use_keyword_case()
    {
        var text = Fmt("declare r emp%rowtype; begin x := sql%rowcount; end;");
        Assert.Contains("emp%ROWTYPE", text, StringComparison.Ordinal);
        Assert.Contains("SQL%ROWCOUNT", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 들여쓰기(FMT-03) ----

    [Fact]
    public void Clauses_break_and_subquery_indents()
    {
        Assert.Equal(
            "SELECT a\nFROM t\nWHERE x IN (\n    SELECT y\n    FROM u\n)\nORDER BY a",
            Fmt("select a from t where x in (select y from u) order by a"));
    }

    [Fact]
    public void Indent_size_tabs_and_depth_options()
    {
        var two = new FormatterOptions { Indent = new IndentOptions { Size = 2, Subquery = 2 } };
        Assert.Equal("SELECT a\nFROM (\n    SELECT b\n    FROM c\n)", Fmt("select a from (select b from c)", two));

        var tabs = new FormatterOptions { Indent = new IndentOptions { UseTabs = true } };
        Assert.Equal("BEGIN\n\tNULL;\nEND;", Fmt("begin null; end;", tabs));

        var flat = new FormatterOptions { Indent = new IndentOptions { Subquery = 0 } };
        Assert.Equal("SELECT a\nFROM (\nSELECT b\nFROM c\n)", Fmt("select a from (select b from c)", flat));
    }

    [Fact]
    public void Case_expression_uses_case_depth()
    {
        Assert.Equal(
            "SELECT CASE\n    WHEN a = 1 THEN 'x'\n    ELSE 'y'\nEND AS c\nFROM t",
            Fmt("select case when a = 1 then 'x' else 'y' end as c from t"));
        var deep = new FormatterOptions { Indent = new IndentOptions { Case = 2 } };
        Assert.Equal(
            "SELECT CASE\n        WHEN a = 1 THEN 'x'\nEND\nFROM t",
            Fmt("select case when a = 1 then 'x' end from t", deep));
    }

    [Fact]
    public void Plsql_blocks_indent_by_block_depth()
    {
        const string sql = "begin if a > 0 then x := 1; else x := 2; end if; for i in 1..3 loop null; end loop; exception when others then raise; end;";
        Assert.Equal(
            Lf("""
            BEGIN
                IF a > 0 THEN
                    x := 1;
                ELSE
                    x := 2;
                END IF;
                FOR i IN 1..3 LOOP
                    NULL;
                END LOOP;
            EXCEPTION
                WHEN OTHERS THEN
                    RAISE;
            END;
            """),
            Fmt(sql));
    }

    [Fact]
    public void Procedure_with_declarations_and_cursor()
    {
        const string sql = "create or replace procedure p(a number) is v number := 0; cursor c is select x from t where y = a; begin open c; close c; end p;\n/";
        Assert.Equal(
            Lf("""
            CREATE OR REPLACE PROCEDURE p(a NUMBER) IS
                v NUMBER := 0;
                CURSOR c IS
                    SELECT x
                    FROM t
                    WHERE y = a;
            BEGIN
                OPEN c;
                CLOSE c;
            END p;
            /
            """),
            Fmt(sql));
    }

    [Fact]
    public void Package_body_nests_subprograms()
    {
        const string sql = "create package body pk as procedure a is begin null; end a; end pk;";
        Assert.Equal(
            Lf("""
            CREATE PACKAGE BODY pk AS
                PROCEDURE a IS
                BEGIN
                    NULL;
                END a;
            END pk;
            """),
            Fmt(sql));
    }

    // ---- 주석·빈 줄(FMT-07) ----

    [Fact]
    public void Comments_keep_position_and_text()
    {
        const string sql = "-- head\nselect a, -- after a\n  b /* mid */ from t -- end\n;";
        Assert.Equal("-- head\nSELECT a, -- after a\n    b /* mid */\nFROM t -- end\n;", Fmt(sql));
    }

    [Fact]
    public void Blank_lines_are_capped_by_max()
    {
        const string sql = "select 1 from dual;\n\n\n\nselect 2 from dual;";
        Assert.Equal("SELECT 1\nFROM dual;\n\nSELECT 2\nFROM dual;", Fmt(sql));
        var none = new FormatterOptions { Comments = new CommentOptions { MaxBlankLines = 0 } };
        Assert.Equal("SELECT 1\nFROM dual;\nSELECT 2\nFROM dual;", Fmt(sql, none));
        var three = new FormatterOptions { Comments = new CommentOptions { MaxBlankLines = 3 } };
        Assert.Equal("SELECT 1\nFROM dual;\n\n\n\nSELECT 2\nFROM dual;", Fmt(sql, three));
    }

    [Fact]
    public void Statement_terminator_add_and_remove()
    {
        var add = new FormatterOptions { StatementTerminator = StatementTerminator.Add };
        Assert.Equal("SELECT 1\nFROM dual;", Fmt("select 1 from dual", add));
        Assert.Equal("BEGIN\n    NULL;\nEND; -- x\n/", Fmt("begin null; end; -- x", add));
        Assert.Equal("SELECT 1\nFROM dual;", Fmt("select 1 from dual;", add));

        var remove = new FormatterOptions { StatementTerminator = StatementTerminator.Remove };
        Assert.Equal("SELECT 1\nFROM dual", Fmt("select 1 from dual;", remove));
        Assert.Equal("BEGIN\n    NULL;\nEND;", Fmt("begin null; end;\n/", remove));
        // 문장이 여럿이면 지우지 않는다(스크립트가 깨진다).
        Assert.Equal("SELECT 1\nFROM dual;\nSELECT 2\nFROM dual;", Fmt("select 1 from dual; select 2 from dual;", remove));
    }

    [Fact]
    public void Sqlplus_lines_stay_verbatim()
    {
        const string sql = "set serveroutput on\nprompt it's here\nrem  note\nselect 1 from dual\n/\n@script.sql";
        Assert.Equal("set serveroutput on\nprompt it's here\nrem  note\nSELECT 1\nFROM dual\n/\n@script.sql", Fmt(sql));
    }

    // ---- 토큰 동일성·원문 유지(FMT-11) ----

    [Fact]
    public void Broken_statement_keeps_original_layout_and_applies_case()
    {
        const string sql = "select a,\n       b from t where (a = 1\n";
        Assert.Equal("SELECT a,\n       b FROM t WHERE (a = 1\n", Fmt(sql));
    }

    [Fact]
    public void Unterminated_string_leaves_text_unchanged()
    {
        const string sql = "select 'abc from dual";
        var r = Formatter().Format(sql, Default);
        Assert.True(r.Succeeded);
        Assert.Equal(sql, r.Text);
    }

    [Fact]
    public void Never_joins_tokens_that_would_lex_differently()
    {
        Assert.Equal("SELECT 3 - -4,\n    - -1\nFROM dual", Fmt("select 3 - -4, - -1 from dual"));
        Assert.Equal("SELECT &a&b,\n    &x.y\nFROM t", Fmt("select &a&b, &x.y from t"));
        Assert.Equal("SELECT n 'x'\nFROM dual", Fmt("select n 'x' from dual"));
    }

    [Fact]
    public void Verify_rejects_changed_tokens_and_comment_counts()
    {
        var engine = typeof(LanguageAssembly).Assembly.GetType("Oranda.Language.Formatting.FormatEngine")!;
        var run = engine.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
        var verify = engine.GetMethod("Verify", BindingFlags.Public | BindingFlags.Static)!;
        var output = run.Invoke(null, ["select a -- c\nfrom t", Default, "", "\n", null])!;
        var items = output.GetType().GetProperty("Items")!.GetValue(output)!;
        string text = (string)output.GetType().GetProperty("Text")!.GetValue(output)!;

        FormatFailure Check(string s) => (FormatFailure)verify.Invoke(null, [items, s, null])!;
        Assert.Equal(FormatFailure.None, Check(text));
        Assert.Equal(FormatFailure.TokenMismatch, Check(text.Replace("a", "b", StringComparison.Ordinal)));
        Assert.Equal(FormatFailure.CommentCountMismatch, Check(text.Replace("-- c", "", StringComparison.Ordinal) + "\n"));
        Assert.Equal(FormatFailure.TokenMismatch, Check(text + " x"));
    }

    // ---- 범위(FMT-01) ----

    [Fact]
    public void Range_formats_only_the_current_statement_and_keeps_base_indent()
    {
        const string sql = "select 1 from dual;\n  select a,b from t where c=1;\nselect 3 from dual;";
        int at = sql.IndexOf("select a", StringComparison.Ordinal);
        var st = StatementSplitter.GetStatementAt(sql, at, Core.Language.SplitOptions.Default)!;
        var r = Formatter().Format(sql, Default, st.Range);
        Assert.True(r.Succeeded);
        Assert.Equal("select 1 from dual;\n  SELECT a,\n      b\n  FROM t\n  WHERE c = 1;\nselect 3 from dual;", r.Text);
        Assert.Equal(new TextRange(20, sql.IndexOf("\nselect 3", StringComparison.Ordinal) - 20), r.Range);
    }

    [Fact]
    public void Range_inside_multiline_comment_expands_to_token()
    {
        const string sql = "select 1 from dual;\n/* a\n b */ select 2 from dual;";
        var r = Formatter().Format(sql, Default, new TextRange(sql.IndexOf(" b */", StringComparison.Ordinal), 2));
        Assert.True(r.Succeeded);
        Assert.Equal("select 1 from dual;\n/* a\n b */\nSELECT 2\nFROM dual;", r.Text);
    }

    [Fact]
    public void Crlf_input_keeps_crlf()
    {
        var text = Fmt("select a\r\nfrom t\r\nwhere b = 1;\r\n");
        Assert.Equal("SELECT a\r\nFROM t\r\nWHERE b = 1;\r\n", text);
    }

    [Fact]
    public void Formatter_is_registered_as_service()
    {
        var attr = typeof(LanguageAssembly).Assembly.GetType("Oranda.Language.Formatting.SqlFormatter")!
            .GetCustomAttribute<Core.Composition.OrandaServiceAttribute>();
        Assert.NotNull(attr);
    }

    // ---- 성능 ----

    [Trait("Category", "Perf")]
    [Fact]
    public void Formats_2000_lines_within_500ms()
    {
        var sb = new System.Text.StringBuilder();
        int lines = 0;
        while (lines < 2000)
        {
            foreach (var s in Samples)
            {
                sb.Append(s).Append(";\n");
                lines += s.Count(c => c == '\n') + 1;
            }
        }

        string sql = sb.ToString();
        var f = Formatter();
        for (int i = 0; i < 3; i++) f.Format(sql, Default);
        long best = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = f.Format(sql, Default);
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
