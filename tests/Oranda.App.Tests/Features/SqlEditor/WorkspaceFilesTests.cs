using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>작업 공간 파일: Markdown 저장·읽기와 MyBatis 매퍼 읽기.</summary>
public sealed class WorkspaceFilesTests
{
    [Fact]
    public void Markdown_round_trips_tab_titles_text_and_connection()
    {
        var conn = Guid.NewGuid().ToString();
        var tabs = new[]
        {
            new WorkspaceFileTab("부서별 인원", "select deptno, count(*)\n  from emp\n group by deptno;", conn),
            new WorkspaceFileTab("SQL 2", "begin\n  null;\nend;\n/"),
        };

        var md = WorkspaceFiles.WriteMarkdown("월말 점검", tabs);
        var read = WorkspaceFiles.ReadMarkdown(md, "fallback");

        Assert.Contains(WorkspaceFiles.Marker, md, StringComparison.Ordinal);
        Assert.Equal("월말 점검", read.Name);
        Assert.Equal(tabs, read.Tabs);
    }

    [Fact]
    public void Markdown_fence_grows_when_sql_contains_backticks()
    {
        var tab = new WorkspaceFileTab("백틱", "select '```' as x from dual");
        var read = WorkspaceFiles.ReadMarkdown(WorkspaceFiles.WriteMarkdown("w", [tab]), "w");
        Assert.Equal(tab.Text, Assert.Single(read.Tabs).Text);
    }

    [Fact]
    public void Hand_written_markdown_without_headings_opens_each_code_block_as_a_tab()
    {
        var read = WorkspaceFiles.ReadMarkdown("메모\n\n```sql\nselect 1 from dual\n```\n\n~~~\nselect 2 from dual\n~~~\n", "notes");
        Assert.Equal(["SQL 1", "SQL 2"], read.Tabs.Select(t => t.Title));
        Assert.Equal("select 2 from dual", read.Tabs[1].Text);
        Assert.Equal("notes", read.Name);
    }

    [Fact]
    public void Markdown_without_code_blocks_becomes_one_tab()
    {
        var read = WorkspaceFiles.ReadMarkdown("select 1 from dual", "plain");
        Assert.Equal("select 1 from dual", Assert.Single(read.Tabs).Text);
    }

    private const string Mapper = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE mapper PUBLIC "-//mybatis.org//DTD Mapper 3.0//EN" "http://mybatis.org/dtd/mybatis-3-mapper.dtd">
        <mapper namespace="com.example.mes.EmpMapper">
          <sql id="cols">empno, ename, sal</sql>
          <select id="findEmp" resultType="map">
            SELECT <include refid="cols"/>
              FROM emp
            <where>
              <if test="deptno != null">AND deptno = #{deptno, jdbcType=NUMERIC}</if>
              <if test="name != null">AND ename LIKE #{name} || '%'</if>
            </where>
            ORDER BY ${orderBy}
          </select>
          <update id="raise">
            UPDATE emp
            <set>
              <if test="sal != null">sal = #{sal},</if>
              <if test="comm != null">comm = #{comm},</if>
            </set>
            WHERE empno IN
            <foreach collection="ids" item="id" open="(" separator="," close=")">#{id}</foreach>
          </update>
          <delete id="purge"><![CDATA[DELETE FROM nopk_log WHERE created < #{before}]]></delete>
        </mapper>
        """;

    [Fact]
    public void MyBatis_mapper_statements_become_tabs_with_binds_and_simplified_dynamic_sql()
    {
        var read = WorkspaceFiles.ReadMyBatis(Mapper, "EmpMapper.xml");

        Assert.Equal("EmpMapper", read.Name);
        Assert.Equal(["findEmp", "raise", "purge"], read.Tabs.Select(t => t.Title));

        var find = read.Tabs[0].Text;
        Assert.StartsWith("-- MyBatis com.example.mes.EmpMapper.findEmp <select>", find, StringComparison.Ordinal);
        Assert.Contains("SELECT empno, ename, sal", find, StringComparison.Ordinal);
        Assert.Contains("WHERE deptno = :deptno", find, StringComparison.Ordinal); // <where> 가 맨 앞 AND 를 뗐다
        Assert.Contains("AND ename LIKE :name || '%'", find, StringComparison.Ordinal);
        Assert.Contains("/* if deptno != null */", find, StringComparison.Ordinal);
        Assert.Contains("ORDER BY &orderBy", find, StringComparison.Ordinal);
        Assert.DoesNotContain("#{", find, StringComparison.Ordinal);

        var raise = read.Tabs[1].Text;
        Assert.Contains("SET sal = :sal,", raise, StringComparison.Ordinal);
        Assert.DoesNotContain(":comm,", raise, StringComparison.Ordinal); // <set> 이 끝 쉼표를 뗐다
        Assert.Contains("(:id)", raise, StringComparison.Ordinal);

        Assert.Contains("DELETE FROM nopk_log WHERE created < :before", read.Tabs[2].Text, StringComparison.Ordinal);
        Assert.Contains(read.Notes, n => n.Contains("동적 태그", StringComparison.Ordinal));
    }

    [Fact]
    public void MyBatis_reader_rejects_other_xml_and_never_fetches_the_dtd()
    {
        Assert.Throws<FormatException>(() => WorkspaceFiles.ReadMyBatis("<beans/>", "x"));

        // DTD 가 외부 주소를 가리켜도 읽지 않는다(네트워크 없이 바로 끝난다).
        var read = WorkspaceFiles.ReadMyBatis(Mapper, "m");
        Assert.Equal(3, read.Tabs.Count);
    }

    [Theory]
    [InlineData("/d/01. 작업.workspace.md", "01. 작업")]
    [InlineData("/d/코일이력.WORKSPACE.MD", "코일이력")]
    [InlineData("/d/예전.md", "예전")]
    [InlineData("/d/UserMapper.xml", "UserMapper")]
    [InlineData("/d/01. 작업.sqd", "01. 작업")]
    public void Workspace_name_drops_the_workspace_md_suffix(string path, string expected)
        => Assert.Equal(expected, WorkspaceFiles.NameOf(path));

    [Fact]
    public void New_files_are_saved_as_name_dot_workspace_dot_md_and_existing_markdown_keeps_its_path()
    {
        var dir = Directory.CreateTempSubdirectory("oranda-ws-name-").FullName;
        try
        {
            var old = Path.Combine(dir, "예전 작업.md");
            File.WriteAllText(old, WorkspaceFiles.WriteMarkdown("예전 작업", [new("a", "select 1 from dual")]));

            Assert.Equal(Path.Combine(dir, "새 작업.workspace.md"), WorkspaceFiles.SavePath(Path.Combine(dir, "새 작업")));
            Assert.Equal(Path.Combine(dir, "새 작업.workspace.md"), WorkspaceFiles.SavePath(Path.Combine(dir, "새 작업.md")));
            Assert.Equal(Path.Combine(dir, "새 작업.workspace.md"), WorkspaceFiles.SavePath(Path.Combine(dir, "새 작업.markdown")));
            Assert.Equal(Path.Combine(dir, "새 작업.workspace.md"), WorkspaceFiles.SavePath(Path.Combine(dir, "새 작업.workspace.md")));
            Assert.Equal(Path.Combine(dir, "v1.2.workspace.md"), WorkspaceFiles.SavePath(Path.Combine(dir, "v1.2")));
            Assert.Equal(old, WorkspaceFiles.SavePath(old)); // 예전 이름.md 작업 공간은 그 자리에 다시 쓴다
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
