using Oranda.Core.Text;
using Oranda.Language.Navigation;

namespace Oranda.Language.Tests.Navigation;

// 소유: TSK-04-06. 식별자 판별·문서 안 정의·참조 찾기(L1 토큰 기반).
public class SymbolLocatorTests
{
    private static SymbolLocator Locator() => new();

    private static int At(string text, string marker, int delta = 0) => text.IndexOf(marker, StringComparison.Ordinal) + delta;

    private static string Slice(string text, TextRange r) => text.Substring(r.Start, r.Length);

    [Fact]
    public void Plain_identifier_in_from_is_object_name()
    {
        const string sql = "select * from emp where 1 = 1";
        var s = Locator().GetSymbolAt(sql, At(sql, "emp", 1));
        Assert.NotNull(s);
        Assert.Equal("EMP", s!.Name);
        Assert.Null(s.Owner);
        Assert.Null(s.Member);
        Assert.Equal("emp", Slice(sql, s.Range));
    }

    [Fact]
    public void Owner_dot_name_in_table_position()
    {
        const string sql = "select * from hr.employees e";
        var onName = Locator().GetSymbolAt(sql, At(sql, "employees", 2))!;
        Assert.Equal("HR", onName.Owner);
        Assert.Equal("EMPLOYEES", onName.Name);
        Assert.Equal("hr.employees", Slice(sql, onName.Range));

        var onOwner = Locator().GetSymbolAt(sql, At(sql, "hr", 1))!;
        Assert.Equal("HR", onOwner.Name);
        Assert.Null(onOwner.Owner);
    }

    [Fact]
    public void Quoted_identifier_keeps_case()
    {
        const string sql = "select * from \"MixedCase\"";
        var s = Locator().GetSymbolAt(sql, At(sql, "Mixed", 1))!;
        Assert.Equal("MixedCase", s.Name);
    }

    [Fact]
    public void Alias_column_resolves_to_table_and_member()
    {
        const string sql = "select e.sal from scott.emp e where e.deptno = 10";
        var s = Locator().GetSymbolAt(sql, At(sql, "sal", 1))!;
        Assert.Equal("EMP", s.Name);
        Assert.Equal("SCOTT", s.Owner);
        Assert.Equal("SAL", s.Member);

        var alias = Locator().GetSymbolAt(sql, At(sql, "e.sal"))!;
        Assert.Equal("EMP", alias.Name);
        Assert.Null(alias.Member);
    }

    [Fact]
    public void Package_member_call()
    {
        const string sql = "begin\n  emp_pkg.raise_salary(10, 5);\nend;";
        var onMember = Locator().GetSymbolAt(sql, At(sql, "raise_salary", 3))!;
        Assert.Equal("EMP_PKG", onMember.Name);
        Assert.Equal("RAISE_SALARY", onMember.Member);
        Assert.Null(onMember.Owner);

        var onPkg = Locator().GetSymbolAt(sql, At(sql, "emp_pkg", 2))!;
        Assert.Equal("EMP_PKG", onPkg.Name);
        Assert.Null(onPkg.Member);
    }

    [Fact]
    public void Three_part_chain_is_owner_name_member()
    {
        const string sql = "begin hr.emp_pkg.hire(1); end;";
        var s = Locator().GetSymbolAt(sql, At(sql, "hire", 1))!;
        Assert.Equal("HR", s.Owner);
        Assert.Equal("EMP_PKG", s.Name);
        Assert.Equal("HIRE", s.Member);
    }

    [Theory]
    [InlineData("select 'emp' from dual", "emp")]
    [InlineData("select 1 -- emp\nfrom dual", "emp")]
    [InlineData("select 12345 from dual", "12345")]
    [InlineData("select * from dual where x = :emp", ":emp")]
    public void Non_identifiers_return_null(string sql, string marker)
    {
        Assert.Null(Locator().GetSymbolAt(sql, At(sql, marker, 1)));
    }

    [Fact]
    public void Caret_at_end_of_word_still_counts_and_gap_does_not()
    {
        const string sql = "select * from emp where x=1";
        Assert.NotNull(Locator().GetSymbolAt(sql, At(sql, "emp", 3)));
        Assert.Null(Locator().GetSymbolAt(sql, At(sql, "emp where", 4)));
    }

    [Fact]
    public void Local_definition_of_alias_cte_and_derived_alias()
    {
        const string sql = "with big as (select * from emp) select b.x from big b join (select 1 x from dual) d on 1=1 where d.x = 1";
        var cte = SymbolAnalyzer.FindLocalDefinition(sql, At(sql, "big b", 1));
        Assert.NotNull(cte);
        Assert.Equal(LocalSymbolKind.Cte, cte!.Kind);
        Assert.Equal(At(sql, "big as"), cte.Start);

        var alias = SymbolAnalyzer.FindLocalDefinition(sql, At(sql, "b.x"));
        Assert.Equal(LocalSymbolKind.TableAlias, alias!.Kind);
        Assert.Equal(At(sql, "b join"), alias.Start);

        var derived = SymbolAnalyzer.FindLocalDefinition(sql, At(sql, "d.x"));
        Assert.Equal(LocalSymbolKind.DerivedAlias, derived!.Kind);

        // 열 쪽(b.x 의 x)은 DB 쪽 이름이다.
        Assert.Null(SymbolAnalyzer.FindLocalDefinition(sql, At(sql, "b.x", 2)));
        // 정의 자리 자신은 정의가 아니라 대상 테이블로 풀린다.
        Assert.Null(SymbolAnalyzer.FindLocalDefinition(sql, At(sql, "b join")));
        Assert.Equal("EMP", Locator().GetSymbolAt("select 1 from emp e", At("select 1 from emp e", "e", 17))!.Name);
    }

    private const string Plsql = """
        declare
          v_total number := 0;
          c_max constant number := 10;
          cursor c_emp (p_dept number) is select * from emp where deptno = p_dept;
          procedure bump(p_n in out number) is
            v_local number;
          begin
            v_local := p_n + 1;
            p_n := v_local;
          end;
        begin
          for r in c_emp(10) loop
            v_total := v_total + r.sal;
          end loop;
          bump(v_total);
        end;
        """;

    [Fact]
    public void Plsql_variable_cursor_subprogram_parameter_definitions()
    {
        int Def(string marker, int nth = 0)
        {
            int p = -1;
            for (int i = 0; i <= nth; i++)
            {
                p = Plsql.IndexOf(marker, p + 1, StringComparison.Ordinal);
            }

            return p;
        }

        var v = SymbolAnalyzer.FindLocalDefinition(Plsql, Def("v_total", 2));
        Assert.Equal(LocalSymbolKind.Variable, v!.Kind);
        Assert.Equal(Def("v_total"), v.Start);

        var cur = SymbolAnalyzer.FindLocalDefinition(Plsql, Def("c_emp(10)"));
        Assert.Equal(LocalSymbolKind.Cursor, cur!.Kind);

        var sub = SymbolAnalyzer.FindLocalDefinition(Plsql, Def("bump(v_total)"));
        Assert.Equal(LocalSymbolKind.Subprogram, sub!.Kind);

        var param = SymbolAnalyzer.FindLocalDefinition(Plsql, Def("p_n + 1"));
        Assert.Equal(LocalSymbolKind.Parameter, param!.Kind);

        var cparam = SymbolAnalyzer.FindLocalDefinition(Plsql, Def("p_dept;") > 0 ? Def("p_dept", 1) : 0);
        Assert.Equal(LocalSymbolKind.Parameter, cparam!.Kind);

        var loop = SymbolAnalyzer.FindLocalDefinition(Plsql, Def("r.sal"));
        Assert.Equal(LocalSymbolKind.LoopVariable, loop!.Kind);

        var local = SymbolAnalyzer.FindLocalDefinition(Plsql, Def("v_local", 1));
        Assert.Equal(Def("v_local"), local!.Start);
    }

    [Fact]
    public void Find_references_for_variable_covers_definition_and_uses_only()
    {
        var refs = Locator().FindReferences(Plsql, Plsql.IndexOf("v_total := v_total", StringComparison.Ordinal) + 1);
        Assert.All(refs, r => Assert.Equal("v_total", Slice(Plsql, r)));
        Assert.Equal(4, refs.Count);
        Assert.Equal(refs.OrderBy(r => r.Start).ToList(), refs.ToList());
    }

    [Fact]
    public void Find_references_for_alias_stays_inside_its_statement()
    {
        const string sql = "select e.sal, e.ename from emp e; select e.x from other e;";
        var refs = Locator().FindReferences(sql, At(sql, "e.sal"));
        Assert.Equal(3, refs.Count);
        Assert.All(refs, r => Assert.True(r.End <= sql.IndexOf(';')));
    }

    [Fact]
    public void Find_references_for_table_and_column_names()
    {
        const string sql = "select emp.sal, sal from scott.emp where sal > 1; delete from emp";
        var table = Locator().FindReferences(sql, At(sql, "from scott.emp", 13));
        Assert.Equal(["emp", "emp", "emp"], table.Select(r => Slice(sql, r)).ToArray());

        var col = Locator().FindReferences(sql, At(sql, "sal >", 1));
        Assert.Equal(3, col.Count);
        Assert.All(col, r => Assert.Equal("sal", Slice(sql, r)));
    }

    [Fact]
    public void Find_references_on_non_identifier_is_empty()
    {
        Assert.Empty(Locator().FindReferences("select 1 from dual", 8));
    }

    [Fact]
    public void Extract_from_does_not_make_a_table_reference()
    {
        const string sql = "select extract(year from d) from t";
        var s = Locator().GetSymbolAt(sql, At(sql, "d)"))!;
        Assert.Equal("D", s.Name);
        var t = Locator().GetSymbolAt(sql, sql.Length - 1)!;
        Assert.Equal("T", t.Name);
    }

    [Fact]
    public void Find_subprogram_prefers_implementation_over_declaration()
    {
        const string pkg = """
            create or replace package body emp_pkg as
              procedure raise_salary(p_id number);
              function helper return number is begin return 1; end;
              procedure raise_salary(p_id number) is
              begin
                null;
              end;
            end emp_pkg;
            """;
        var r = SymbolAnalyzer.FindSubprogram(pkg, "RAISE_SALARY");
        Assert.NotNull(r);
        Assert.Equal(pkg.LastIndexOf("raise_salary", StringComparison.Ordinal), r!.Value.Start);
        Assert.Null(SymbolAnalyzer.FindSubprogram(pkg, "nope"));
        Assert.NotNull(SymbolAnalyzer.FindSubprogram(pkg, "helper"));
    }
}
