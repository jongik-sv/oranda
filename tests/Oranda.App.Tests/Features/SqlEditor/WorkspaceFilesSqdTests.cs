using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>Orange 작업 공간(.sqd) 읽기. 가짜 SQL 은 시험에서 CP949 바이트로 만들고, 실제 표본은 TestData 에서 읽는다.</summary>
public sealed class WorkspaceFilesSqdTests
{
    internal static string SamplePath => Path.Combine(AppContext.BaseDirectory, "TestData", "orange-workspace-sample.sqd");

    internal static byte[] Sqd(params string[] lines) =>
        FileEncoding.Encode(string.Join("\r\n", lines) + "\r\n", "cp949");

    private static readonly string[] Header = ["[HEADER]", "VERSION=5.0.12", "TOOL=", "CHAR=ASCII", "DATE=2023-09-04 08:39"];

    internal static string[] WithHeader(params string[] workspaceLines) => [.. Header, "[WORKSPACE]", .. workspaceLines];

    [Fact]
    public void Reads_korean_names_comments_and_multi_line_bodies_from_cp949_bytes()
    {
        var bytes = Sqd(WithHeader(
            "COUNT=2",
            "NAME=월말 점검.sql", "STMT=3", "-- 부서별 인원 (한글 주석)", "select deptno, count(*)", "  from emp group by deptno", "VAR=0",
            "NAME=재고", "STMT=1", "select '가나다' from dual", "VAR=0"));

        var read = WorkspaceFiles.ReadSqd(bytes, "fallback");

        Assert.Equal("fallback", read.Name);
        Assert.Equal(["월말 점검.sql", "재고"], read.Tabs.Select(t => t.Title));
        Assert.Equal("-- 부서별 인원 (한글 주석)\nselect deptno, count(*)\n  from emp group by deptno", read.Tabs[0].Text);
        Assert.Equal("select '가나다' from dual", read.Tabs[1].Text);
        Assert.Empty(read.Notes);
        Assert.All(read.Tabs, t => Assert.Null(t.Binds));
    }

    [Fact]
    public void Body_lines_that_look_like_keys_are_not_read_as_keys()
    {
        var read = WorkspaceFiles.ReadSqd(Sqd(WithHeader(
            "COUNT=2",
            "NAME=a", "STMT=4", "NAME=가짜", "STMT=99", "VAR=1", "[WORKSPACE]", "VAR=0",
            "NAME=b", "STMT=1", "select 1 from dual", "VAR=0")), "x");

        Assert.Equal(["a", "b"], read.Tabs.Select(t => t.Title));
        Assert.Equal("NAME=가짜\nSTMT=99\nVAR=1\n[WORKSPACE]", read.Tabs[0].Text);
        Assert.Empty(read.Notes);
    }

    [Fact]
    public void Blank_body_lines_are_kept_and_an_empty_body_is_allowed()
    {
        var read = WorkspaceFiles.ReadSqd(Sqd(WithHeader(
            "COUNT=2", "NAME=a", "STMT=3", "select 1", "", "from dual", "VAR=0", "NAME=빈 탭", "STMT=0", "VAR=0")), "x");

        Assert.Equal("select 1\n\nfrom dual", read.Tabs[0].Text);
        Assert.Equal(string.Empty, read.Tabs[1].Text);
        Assert.Empty(read.Notes);
    }

    [Fact]
    public void Binds_keep_empty_values_commas_and_the_part_after_the_first_comma()
    {
        var read = WorkspaceFiles.ReadSqd(Sqd(WithHeader(
            "COUNT=1", "NAME=a", "STMT=1", "select :EMPNO, :LIST, :한글 from dual", "VAR=3",
            ":EMPNO=0,", ":LIST=0,7369,7499, 7521", ":한글=0,홍길동")), "x");

        var binds = Assert.Single(read.Tabs).Binds!;
        Assert.Equal(["EMPNO", "LIST", "한글"], binds.Select(b => b.Name));
        Assert.Equal(string.Empty, binds[0].Value);
        Assert.True(binds[0].IsNull);
        Assert.Equal("7369,7499, 7521", binds[1].Value);
        Assert.False(binds[1].IsNull);
        Assert.Equal("홍길동", binds[2].Value);
        Assert.All(binds, b => Assert.Equal(Oranda.Core.Execution.BindDataType.Varchar2, b.Type));
        Assert.Empty(read.Notes);
    }

    [Fact]
    public void Secret_looking_bind_names_lose_their_value_and_unknown_types_leave_a_note()
    {
        var read = WorkspaceFiles.ReadSqd(Sqd(WithHeader(
            "COUNT=1", "NAME=a", "STMT=1", "select 1 from dual", "VAR=3",
            ":DB_PASSWORD=0,hunter2", ":api_token=0,abc", ":QTY=3,5")), "x");

        var binds = Assert.Single(read.Tabs).Binds!;
        Assert.Equal(string.Empty, binds[0].Value);
        Assert.Equal(string.Empty, binds[1].Value);
        Assert.Equal("5", binds[2].Value);
        Assert.Contains(read.Notes, n => n.Contains("타입 코드(3)", StringComparison.Ordinal));
    }

    [Fact]
    public void Short_body_keeps_what_was_read_and_adds_a_note()
    {
        var read = WorkspaceFiles.ReadSqd(Sqd(WithHeader("COUNT=1", "NAME=잘림", "STMT=5", "select 1", "from dual")), "x");

        var tab = Assert.Single(read.Tabs);
        Assert.Equal("잘림", tab.Title);
        Assert.StartsWith("select 1\nfrom dual", tab.Text, StringComparison.Ordinal);
        Assert.NotEmpty(read.Notes);
    }

    [Fact]
    public void Count_mismatch_opens_the_tabs_that_exist_and_adds_a_note()
    {
        var read = WorkspaceFiles.ReadSqd(Sqd(WithHeader("COUNT=5", "NAME=a", "STMT=1", "select 1", "VAR=0")), "x");

        Assert.Single(read.Tabs);
        Assert.Contains(read.Notes, n => n.Contains("COUNT", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_stmt_and_var_lines_and_garbage_between_tabs_do_not_throw()
    {
        var read = WorkspaceFiles.ReadSqd(Sqd(WithHeader("COUNT=2", "NAME=a", "쓰레기 줄", "NAME=b", "STMT=1", "select 2", "VAR=2", ":X=0,1")), "x");

        Assert.Equal(["a", "b"], read.Tabs.Select(t => t.Title));
        Assert.Equal("select 2", read.Tabs[1].Text);
        Assert.NotEmpty(read.Notes);
    }

    [Fact]
    public void File_without_workspace_section_is_rejected_and_empty_workspace_is_noted()
    {
        Assert.Throws<FormatException>(() => WorkspaceFiles.ReadSqd(Sqd("그냥 글"), "x"));
        Assert.Throws<FormatException>(() => WorkspaceFiles.ReadSqd([], "x"));
        var read = WorkspaceFiles.ReadSqd(Sqd(WithHeader("COUNT=0")), "x");
        Assert.Empty(read.Tabs);
        Assert.NotEmpty(read.Notes);
    }

    [Fact]
    public void Utf8_with_or_without_bom_and_lf_line_ends_are_read()
    {
        var text = "[HEADER]\nCHAR=UTF-8\n[WORKSPACE]\nCOUNT=1\nNAME=한글\nSTMT=1\nselect '가' from dual\nVAR=0\n";
        var plain = WorkspaceFiles.ReadSqd(System.Text.Encoding.UTF8.GetBytes(text), "x");
        var bom = WorkspaceFiles.ReadSqd([0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(text)], "x");

        Assert.Equal("select '가' from dual", Assert.Single(plain.Tabs).Text);
        Assert.Equal("한글", Assert.Single(bom.Tabs).Title);
        Assert.Empty(bom.Notes);
    }

    [Fact]
    public void Load_dispatches_on_the_sqd_extension_and_names_the_workspace_after_the_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-sqd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "월간 작업.SQD");
            File.WriteAllBytes(path, Sqd(WithHeader("COUNT=1", "NAME=a", "STMT=1", "select 1", "VAR=0")));
            var read = WorkspaceFiles.Load(path);

            Assert.Equal("월간 작업", read.Name);
            Assert.Equal("select 1", Assert.Single(read.Tabs).Text);
            Assert.True(WorkspaceFiles.IsReadOnlySource(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Real_orange_sample_has_19_tabs_with_matching_names_line_counts_and_binds()
    {
        var bytes = File.ReadAllBytes(SamplePath);
        var read = WorkspaceFiles.ReadSqd(bytes, "표본");

        string[] names =
        [
            "라인길이 계산 공정.sql", "작업시간(휴지).sql", "코일이력", "3004", "30042", "코일삭제", "SQL2", "SQL3", "전수검사 미완료", "M20",
            "전문Invoke", "SQL4", "SQL6", "SQL7", "SQL8", "SQL9", "SQL5", "생산일보", "SQL10",
        ];
        int[] statementLines = [35, 84, 60, 218, 34, 20, 52, 63, 16, 8, 6, 18, 162, 6, 33, 20, 33, 588, 96];
        int[] bindCounts = [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 2];

        Assert.Equal(names, read.Tabs.Select(t => t.Title));
        Assert.Equal(statementLines, read.Tabs.Select(t => t.Text.Split('\n').Length));
        Assert.Equal(bindCounts, read.Tabs.Select(t => t.Binds?.Count ?? 0));
        Assert.Empty(read.Notes);

        // 독립 확인: 파일의 STMT 숫자 19개가 읽은 줄 수와 같다(줄 수로 자르는 규칙).
        var lines = FileEncoding.Decode(bytes).Text.Split("\r\n");
        var stmt = lines.Where(l => l.StartsWith("STMT=", StringComparison.Ordinal)).Select(l => int.Parse(l[5..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(statementLines, stmt);

        var sql8 = read.Tabs[14].Binds!.Single();
        Assert.Equal(("ObjectId", string.Empty), (sql8.Name, sql8.Value));
        var daily = read.Tabs[17].Binds!.Single();
        Assert.Equal(("WORD_DH", "2022-07-12"), (daily.Name, daily.Value));
        var sql10 = read.Tabs[18].Binds!;
        Assert.Equal([("XFR_DATE", "20220701"), ("XTO_DATE", "20220701")], sql10.Select(b => (b.Name, b.Value)));
        Assert.Equal(["작업시간", "하영재", "문종배"], read.Tabs[0].Binds!.Select(b => b.Name));
        Assert.All(read.Tabs[0].Binds!, b => Assert.True(b.IsNull));
    }
}
