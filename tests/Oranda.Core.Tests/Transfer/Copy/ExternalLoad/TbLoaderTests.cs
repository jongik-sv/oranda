using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.Core.Tests.Transfer.Copy.ExternalLoad;

// 소유: XL2. tbLoader 제어 파일·실행 묶음·로그 읽기(설명서 기준, 실서버 확인 전).
public sealed class TbLoaderTests : IDisposable
{
    private const string Password = "Tb#Secret@7!";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-tbloader-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static LoaderControlSpec Spec(IReadOnlyList<LoaderColumn> columns) => new(
        "SCOTT", "EMP", columns, @"C:\t\oranda-load\r1\1-EMP\data.dat", @"C:\t\oranda-load\r1\1-EMP\load.bad",
        @"C:\t\oranda-load\r1\1-EMP\load.dsc", 50, Direct: true);

    private static string[] Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    // ---- 제어 파일 ----

    [Fact]
    public void Control_file_names_the_file_format_and_the_table()
    {
        var lines = Lines(TbLoaderControlFile.Build(Spec([new("ID", "NUMBER(10)"), new("NAME", "VARCHAR2(20)")])));

        Assert.Equal("LOAD DATA", lines[0]);
        Assert.Equal("CHARACTERSET UTF8", lines[1]);
        Assert.StartsWith("INFILE ", lines[2], StringComparison.Ordinal);
        Assert.Contains("data.dat", lines[2], StringComparison.Ordinal);
        Assert.Contains("APPEND", lines);
        Assert.Contains("INTO TABLE \"SCOTT\".\"EMP\"", lines);
        Assert.Contains("FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '\"' ESCAPED BY '\\\\'", lines);
        Assert.Contains("TRAILING NULLCOLS", lines);

        // 줄 끝 = 0x1E(글자 그대로) + 역슬래시 n — 데이터 파일의 레코드 구분(LoaderDataWriter.RecordTerminator)과 짝.
        var terminator = Assert.Single(lines, l => l.StartsWith("LINES TERMINATED BY", StringComparison.Ordinal));
        Assert.Contains('\u001E', terminator);
        Assert.Equal("LINES TERMINATED BY '\u001E\\n'", terminator);
    }

    [Fact]
    public void Numbers_are_read_as_text_and_converted_by_the_server_so_an_empty_field_stays_null()
    {
        Assert.Equal("\"ID\" CHAR(400) \"TO_NUMBER(:ID)\"", TbLoaderControlFile.FieldClause(new LoaderColumn("ID", "NUMBER(10)")));
        Assert.Equal("\"BF\" CHAR(100) \"TO_BINARY_FLOAT(:BF)\"", TbLoaderControlFile.FieldClause(new LoaderColumn("BF", "BINARY_FLOAT")));
        Assert.Equal("\"BD\" CHAR(100) \"TO_BINARY_DOUBLE(:BD)\"", TbLoaderControlFile.FieldClause(new LoaderColumn("BD", "BINARY_DOUBLE")));
        Assert.Equal("\"YM\" CHAR(100) \"TO_YMINTERVAL(:YM)\"", TbLoaderControlFile.FieldClause(new LoaderColumn("YM", "INTERVAL YEAR(2) TO MONTH")));
        Assert.Equal("\"R\" CHAR(32) \"HEXTORAW(:R)\"", TbLoaderControlFile.FieldClause(new LoaderColumn("R", "RAW(16)")));
        Assert.StartsWith("\"TZ\" CHAR(100) \"TO_TIMESTAMP_TZ(:TZ, '", TbLoaderControlFile.FieldClause(new LoaderColumn("TZ", "TIMESTAMP(6) WITH TIME ZONE")), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_dates_and_lobs_get_their_field_clauses()
    {
        Assert.Equal("\"NAME\" CHAR(4000)", TbLoaderControlFile.FieldClause(new LoaderColumn("NAME", "VARCHAR2(20)")));
        Assert.Equal("\"BIG\" CHAR(16000)", TbLoaderControlFile.FieldClause(new LoaderColumn("BIG", "VARCHAR2(4000)")));
        Assert.StartsWith("\"D\" DATE \"", TbLoaderControlFile.FieldClause(new LoaderColumn("D", "DATE")), StringComparison.Ordinal);
        Assert.StartsWith("\"TS\" TIMESTAMP \"", TbLoaderControlFile.FieldClause(new LoaderColumn("TS", "TIMESTAMP(6)")), StringComparison.Ordinal);
        Assert.Equal("\"DOC\" OUTFILE NULLIF \"DOC\" = BLANKS", TbLoaderControlFile.FieldClause(new LoaderColumn("DOC", "CLOB")));
        Assert.Equal("\"PIC\" OUTFILE NULLIF \"PIC\" = BLANKS", TbLoaderControlFile.FieldClause(new LoaderColumn("PIC", "BLOB")));
    }

    [Fact]
    public void Columns_are_listed_in_order_with_commas_between()
    {
        var lines = Lines(TbLoaderControlFile.Build(Spec([new("ID", "NUMBER(10)"), new("NAME", "VARCHAR2(20)"), new("DOC", "CLOB")])));
        var open = Array.IndexOf(lines, "(");

        Assert.Equal("  \"ID\" CHAR(400) \"TO_NUMBER(:ID)\",", lines[open + 1]);
        Assert.Equal("  \"NAME\" CHAR(4000),", lines[open + 2]);
        Assert.Equal("  \"DOC\" OUTFILE NULLIF \"DOC\" = BLANKS", lines[open + 3]);
        Assert.Equal(")", lines[open + 4]);
    }

    [Fact]
    public void Expression_fields_need_a_simple_name_and_supports_says_so()
    {
        Assert.Throws<ArgumentException>(() => TbLoaderControlFile.FieldClause(new LoaderColumn("Mixed Id", "NUMBER")));
        Assert.False(TbLoaderControlFile.Supports(new LoaderColumn("Mixed Id", "NUMBER")));
        Assert.True(TbLoaderControlFile.Supports(new LoaderColumn("Mixed Id", "VARCHAR2(10)"))); // 식 없이 받는 형식은 이름을 묻지 않는다
        Assert.True(TbLoaderControlFile.Supports(new LoaderColumn("ID", "NUMBER")));
        Assert.Throws<ArgumentException>(() => TbLoaderControlFile.Build(Spec([])));
    }

    // ---- 실행 묶음 ----

    private LoaderWorkspace Workspace() => LoaderWorkspace.Create("r1", 1, "EMP", _root);

    [Fact]
    public void Command_keeps_the_password_out_of_arguments_and_gives_it_on_standard_input()
    {
        using var workspace = Workspace();
        var (start, description) = TbLoaderCommand.Build(@"C:\tibero\client\bin\tbloader.exe", workspace,
            new ExternalLoadLogin("SCOTT", "dbhost:8700/tibero", Password, null), maxErrors: 50, direct: true);

        Assert.All(start.Arguments, a => Assert.DoesNotContain(Password, a, StringComparison.Ordinal));
        Assert.DoesNotContain(Password, description, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, start.ToString(), StringComparison.Ordinal);
        Assert.All(start.Environment.Values, v => Assert.DoesNotContain(Password, v ?? string.Empty, StringComparison.Ordinal));
        Assert.Equal(Password + "\n", start.StandardInput);

        Assert.Equal("userid=SCOTT@" + TbLoaderCommand.DsnAlias, start.Arguments[0]);
        Assert.Equal("userid=SCOTT@ORANDA_LOAD", start.Arguments[0]);
        Assert.Contains("control=" + workspace.ControlFile, start.Arguments);
        Assert.Contains("data=" + workspace.DataFile, start.Arguments);
        Assert.Contains("log=" + workspace.LogFile, start.Arguments);
        Assert.Contains("bad=" + workspace.BadFile, start.Arguments);
        Assert.Contains("discard=" + workspace.DiscardFile, start.Arguments);
        Assert.Contains("errors=50", start.Arguments);
        Assert.Contains("direct=Y", start.Arguments);
        Assert.Equal(workspace.Directory, start.WorkingDirectory);
        Assert.Equal(@"C:\tibero\client\bin\tbloader.exe", start.FileName);
        Assert.Contains("(비밀번호는 표준 입력)", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_writes_the_alias_file_and_points_tbdsn_file_at_it()
    {
        using var workspace = Workspace();
        var (start, _) = TbLoaderCommand.Build("tbloader", workspace, new ExternalLoadLogin("SCOTT", "dbhost:8700/tibero", Password, null), 0, direct: false);

        var dsn = Path.Combine(workspace.Directory, TbLoaderCommand.DsnFileName);
        Assert.Equal(dsn, start.Environment["TBDSN_FILE"]);
        Assert.Equal("UTF8", start.Environment["TB_NLS_LANG"]);
        Assert.Contains("direct=N", start.Arguments);
        Assert.Contains("errors=0", start.Arguments);

        var text = File.ReadAllText(dsn);
        Assert.StartsWith("ORANDA_LOAD=(", text, StringComparison.Ordinal);
        Assert.Contains("(INSTANCE=(HOST=dbhost)(PORT=8700)(DB_NAME=tibero))", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dbhost:8700/tibero", "(INSTANCE=(HOST=dbhost)(PORT=8700)(DB_NAME=tibero))")]
    [InlineData("dbhost/tibero", "(INSTANCE=(HOST=dbhost)(PORT=8629)(DB_NAME=tibero))")] // 기본 포트
    [InlineData(" 10.0.0.5:9000/ TB7 ", "(INSTANCE=(HOST=10.0.0.5)(PORT=9000)(DB_NAME=TB7))")]
    [InlineData("[fe80::1]:8700/tibero", "(INSTANCE=(HOST=fe80::1)(PORT=8700)(DB_NAME=tibero))")]
    [InlineData("[fe80::1]/tibero", "(INSTANCE=(HOST=fe80::1)(PORT=8629)(DB_NAME=tibero))")]
    [InlineData("(INSTANCE=(HOST=h)(PORT=1)(DB_NAME=d))", "(INSTANCE=(HOST=h)(PORT=1)(DB_NAME=d))")] // 기술자는 그대로
    [InlineData("((INSTANCE=(HOST=h)(PORT=1)(DB_NAME=d)))", "(INSTANCE=(HOST=h)(PORT=1)(DB_NAME=d))")] // 바깥 괄호 한 겹은 벗긴다
    public void Connect_address_becomes_a_tbdsn_instance(string connect, string expected) =>
        Assert.Equal(expected, TbLoaderCommand.Instance(connect));

    [Theory]
    [InlineData("dbhost")]
    [InlineData("dbhost:8700/")]
    [InlineData("/tibero")]
    [InlineData("dbhost:/tibero")] // 빈 포트: FormatException 이 아니라 ArgumentException(실행기가 기본 방식으로 넣는다)
    [InlineData("dbhost:x8629/tibero")]
    [InlineData("dbhost:99999999999/tibero")]
    public void Connect_address_without_a_db_name_or_with_a_bad_port_is_refused(string connect) =>
        Assert.Throws<ArgumentException>(() => TbLoaderCommand.Instance(connect));

    [Fact]
    public void User_names_that_would_break_the_command_line_are_refused()
    {
        using var workspace = Workspace();

        Assert.Throws<ArgumentException>(() => TbLoaderCommand.Build("tbloader", workspace, new ExternalLoadLogin("SCOTT/x", "h/d", Password, null), 0, true));
        Assert.Throws<ArgumentException>(() => TbLoaderCommand.Build("tbloader", workspace, new ExternalLoadLogin("A B", "h/d", Password, null), 0, true));
    }

    // ---- 로그 ----

    private const string ManualLog = """
        Table 'CLUB' was loaded from the data file.
        Record 2 was rejected.
          TBR-80053 : Some relatively positioned columns are not present in the record.
        Record 3 was rejected.
          TBR-80025 : Column data exceeds data buffer size.
        Table 'CLUB'
        8 Rows were requested to load.
        5 Rows were loaded successfully.
        3 Rows were failed to load because of some errors
        """;

    [Fact]
    public void Manual_sample_log_reads_counts_and_rejects()
    {
        var log = TbLoaderLog.Parse(ManualLog);

        Assert.Equal(5, log.Loaded);
        Assert.Equal(8, log.Read);
        Assert.Equal(3, log.Rejected); // 요약의 실패 수(거절 줄보다 많다)
        Assert.Equal(
            [
                new LoaderRejected(2, "TBR-80053 : Some relatively positioned columns are not present in the record."),
                new LoaderRejected(3, "TBR-80025 : Column data exceeds data buffer size."),
            ],
            log.Rejects);
        Assert.Empty(log.Fatal); // 거절 줄에 붙은 TBR 줄은 치명 오류가 아니다
        Assert.False(log.ErrorLimitHit);
        Assert.Empty(log.UnusableIndexes);
    }

    [Fact]
    public void Crlf_log_and_a_standalone_tbr_line_is_fatal()
    {
        var log = TbLoaderLog.Parse("TBR-2131 : Generic I/O error.\r\n0 Rows were loaded successfully.\r\n");

        Assert.Equal(0, log.Loaded);
        Assert.Equal(["TBR-2131 : Generic I/O error."], log.Fatal);
        Assert.Empty(log.Rejects);
    }

    [Fact]
    public void Empty_log_has_no_count_and_only_exit_zero_is_ok()
    {
        Assert.Same(LoaderLogResult.Empty, TbLoaderLog.Parse(null));
        Assert.Same(LoaderLogResult.Empty, TbLoaderLog.Parse("  "));
        Assert.Null(TbLoaderLog.Parse("Table 'CLUB' was loaded from the data file.").Loaded);
        Assert.True(TbLoaderLog.ExitOk(0));
        Assert.False(TbLoaderLog.ExitOk(1));
        Assert.False(TbLoaderLog.ExitOk(2));
    }
}
