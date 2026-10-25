using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Formatting;
using Oranda.Core.Text;

namespace Oranda.Language.Tests.Formatting;

/// <summary>
/// 기본 제공 프로필 「Oranda 기본」(oranda-default = <see cref="FormatterOptions.OrandaDefault"/>)이 쿼리 서식 샘플 4개와 글자 하나까지 같은지 본다(TSK-13-43).
/// 자료: Formatting/HouseStyle/*(샘플 사본. 표·칼럼은 예시 이름). 문장마다 (가) 그대로 (나) 한 줄로 뭉갬 (다) 소문자·들여쓰기 흐트림 을
/// 포맷해 샘플과 비교한다.
/// <para>주석 규칙: ① 문장 앞 설명 주석(자기 줄)은 그대로 자기 줄에 두고 다음 문장 첫 줄 열에 맞춘다. ② 줄 끝 주석은 앞 코드 뒤에
/// 원래 앞 공백 수 그대로 붙고 다음 코드는 다음 줄에서 시작한다. ③ 문장 안 자기 줄 주석은 다음 코드 줄의 열에 맞춘다.
/// (나) 뭉갬도 줄 주석 뒤 줄바꿈은 남긴다(줄 주석 뒤를 이으면 코드가 주석이 된다).</para>
/// <para>샘플끼리·샘플과 포매터가 어긋나 맞출 수 없는 곳(예외): ① analysis 1번(사용자 원문)은 「"년월", A.PRD_NM_CD 품명」 두 항목이 한 줄이고
/// <c>SUBSTR(MTL_CD, 7,1)</c> 쉼표 뒤 공백이 없다. 포매터는 항목을 한 줄에 하나·쉼표 뒤 한 칸으로 쓰므로 골든 자료는 그 모양으로 고쳐 두었다
/// (<see cref="Analysis_user_original_is_normalized"/>). ② with 2번의 EXISTS 서브쿼리는 6번과 같은 짧은 모양인데 블록으로 펼쳐 있다.
/// 포매터는 원문이 이미 여러 줄이면 블록을 지키고, 한 줄이면(짧고 단순하면) 한 줄로 둔다. 그래서 (나) 뭉갬만 한 줄 EXISTS 가 된다.</para>
/// </summary>
public class HouseStyleGoldenTests
{
    static readonly FormatterOptions House = FormatterOptions.OrandaDefault;

    static ISqlFormatter Formatter()
    {
        var type = typeof(LanguageAssembly).Assembly.GetType("Oranda.Language.Formatting.SqlFormatter")!;
        return (ISqlFormatter)Activator.CreateInstance(type, nonPublic: true)!;
    }

    static string Fmt(string sql, TextRange? range = null)
    {
        var r = Formatter().Format(sql, House, range);
        Assert.True(r.Succeeded, $"{r.Failure}: {r.Message}\n{sql}");
        return r.Text;
    }

    internal static string Resource(string name)
    {
        using var s = typeof(HouseStyleGoldenTests).Assembly.GetManifestResourceStream("HouseStyle." + name)
            ?? throw new InvalidOperationException("자료 없음: " + name);
        using var rd = new StreamReader(s, Encoding.UTF8);
        return rd.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>파일을 빈 줄로 나눈 묶음 중 SQL 이 든 것(앞 설명 주석 줄 포함, 끝 줄바꿈 없음).</summary>
    internal static List<string> Statements(string file)
    {
        var list = new List<string>();
        foreach (var block in file.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var b = block.Trim('\n');
            if (b.Split('\n').Any(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal) && l.Trim().Length > 0)) list.Add(b);
        }

        return list;
    }

    // ---- 변형 ----

    /// <summary>(나) 한 줄로 뭉갬: 줄을 공백 하나로 잇고 따옴표 밖 연속 공백을 하나로. 줄 주석 뒤 줄바꿈과 줄 끝 주석 앞 공백은 남긴다.</summary>
    internal static string Collapse(string stmt)
    {
        var sb = new StringBuilder();
        foreach (var raw in stmt.Split('\n'))
        {
            var (code, comment) = SplitComment(raw);
            string c = Squeeze(code.Trim());
            if (sb.Length > 0 && sb[^1] != '\n' && c.Length > 0) sb.Append(' ');
            sb.Append(c);
            if (comment is not null)
            {
                if (c.Length > 0) sb.Append(Gap(code));
                sb.Append(comment).Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>(다) 소문자·들여쓰기 흐트림: 따옴표·바인드·주석 밖 글자를 소문자로, 줄 앞 공백을 줄마다 다르게, 따옴표 밖 연속 공백을 하나로.</summary>
    internal static string Scramble(string stmt, bool keepFirstIndent = false)
    {
        var lines = stmt.Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            var (code, comment) = SplitComment(lines[i]);
            string lead = keepFirstIndent && i == 0 ? code[..(code.Length - code.TrimStart().Length)] : new string(' ', (i * 5) % 7) + (i % 4 == 3 ? "\t" : "");
            string c = Lower(Squeeze(code.Trim()));
            if (i > 0) sb.Append('\n');
            sb.Append(lead).Append(c);
            if (comment is not null) sb.Append(c.Length > 0 ? Gap(code) : "").Append(comment);
        }

        return sb.ToString();
    }

    static string Gap(string code) => new(' ', Math.Max(1, code.Length - code.TrimEnd().Length));

    static (string Code, string? Comment) SplitComment(string line)
    {
        bool sq = false, dq = false;
        for (int i = 0; i + 1 < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '\'' && !dq) sq = !sq;
            else if (ch == '"' && !sq) dq = !dq;
            else if (!sq && !dq && ch == '-' && line[i + 1] == '-') return (line[..i], line[i..]);
        }

        return (line, null);
    }

    static string Squeeze(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool sq = false, dq = false;
        foreach (char ch in s)
        {
            if (ch == '\'' && !dq) sq = !sq;
            else if (ch == '"' && !sq) dq = !dq;
            if (ch == ' ' && !sq && !dq && sb.Length > 0 && sb[^1] == ' ') continue;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    static string Lower(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool sq = false, dq = false, bind = false, param = false;
        foreach (char ch in s)
        {
            if (ch == '\'' && !dq) sq = !sq;
            else if (ch == '"' && !sq) dq = !dq;
            if (!sq && !dq && ch == ':') bind = true;
            else if (bind && !(char.IsLetterOrDigit(ch) || ch == '_')) bind = false;
            if (!sq && !dq && ch == '#') param = true;
            sb.Append(sq || dq || bind || param ? ch : char.ToLowerInvariant(ch));
            if (ch == '}') param = false;
        }

        return sb.ToString();
    }

    // ---- MyBatis: #{이름} 은 SQL 렉서가 읽지 못하므로 같은 길이 바인드로 바꿨다가 되돌린다 ----

    static readonly Regex MyBatisParam = new(@"#\{(\w+)\}", RegexOptions.CultureInvariant);

    static readonly Regex Placeholder = new(@":(\w+?)__(?!\w)", RegexOptions.CultureInvariant);

    internal static string ToBinds(string sql) => MyBatisParam.Replace(sql, m => ":" + m.Groups[1].Value + "__");

    internal static string FromBinds(string sql) => Placeholder.Replace(sql, m => "#{" + m.Groups[1].Value + "}");

    /// <summary>CDATA 본문(<c>&lt;![CDATA[</c> 다음 줄 ~ <c>]]&gt;</c> 앞 줄) 목록.</summary>
    internal static List<string> CdataBodies(string xml)
    {
        var list = new List<string>();
        int at = 0;
        while ((at = xml.IndexOf("<![CDATA[\n", at, StringComparison.Ordinal)) >= 0)
        {
            int s = at + "<![CDATA[\n".Length;
            int e = xml.IndexOf("]]>", s, StringComparison.Ordinal);
            int lineEnd = xml.LastIndexOf('\n', e);
            list.Add(xml[s..lineEnd]);
            at = e;
        }

        return list;
    }

    /// <summary>CDATA 본문 SQL 을 범위 포맷(첫 줄 들여쓰기 = 기준 들여쓰기)으로 포맷한다.</summary>
    static string FmtCdata(string body)
    {
        string sql = ToBinds(body);
        return FromBinds(Fmt(sql, new TextRange(0, sql.Length)));
    }

    // ---- 시험 ----

    public static TheoryData<string, int> SqlStatements()
    {
        var data = new TheoryData<string, int>();
        foreach (var f in new[] { "query-format-basic.sql", "query-format-with.sql", "query-format-analysis.sql" })
        {
            int n = Statements(Resource(f)).Count;
            for (int i = 0; i < n; i++) data.Add(f, i);
        }

        return data;
    }

    /// <summary>예외 ②: with 2번을 한 줄로 뭉개면 EXISTS 서브쿼리(짧고 단순)는 한 줄이 된다.</summary>
    static string ExpectedCollapsed(string file, int index, string golden)
    {
        if (file == "query-format-with.sql" && index == 1)
        {
            return golden.Replace(
                "AND    EXISTS\n       (\n           SELECT 1\n           FROM   TB_ORD_LINE C\n           WHERE  C.PROC_CD = A.PROC_CD\n       );",
                "AND    EXISTS (SELECT 1 FROM TB_ORD_LINE C WHERE C.PROC_CD = A.PROC_CD);",
                StringComparison.Ordinal);
        }

        return golden;
    }

    [Theory]
    [MemberData(nameof(SqlStatements))]
    public void As_is_statement_stays_the_same(string file, int index)
    {
        var golden = Statements(Resource(file))[index];
        Assert.Equal(golden, Fmt(golden));
    }

    [Theory]
    [MemberData(nameof(SqlStatements))]
    public void Collapsed_statement_formats_back_to_the_sample(string file, int index)
    {
        var golden = Statements(Resource(file))[index];
        Assert.Equal(ExpectedCollapsed(file, index, golden), Fmt(Collapse(golden)));
    }

    [Theory]
    [MemberData(nameof(SqlStatements))]
    public void Lowercased_and_reindented_statement_formats_back_to_the_sample(string file, int index)
    {
        var golden = Statements(Resource(file))[index];
        Assert.Equal(golden, Fmt(Scramble(golden)));
    }

    [Theory]
    [MemberData(nameof(SqlStatements))]
    public void Formatting_twice_is_stable(string file, int index)
    {
        var golden = Statements(Resource(file))[index];
        foreach (var input in new[] { Collapse(golden), Scramble(golden) })
        {
            var once = Fmt(input);
            Assert.Equal(once, Fmt(once));
        }
    }

    [Theory]
    [InlineData("query-format-basic.sql")]
    [InlineData("query-format-with.sql")]
    [InlineData("query-format-analysis.sql")]
    public void Whole_sample_file_has_zero_difference(string file)
    {
        var golden = Resource(file);
        Assert.Equal(golden, Fmt(golden));
        var crlf = golden.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.Equal(crlf, Fmt(crlf));
    }

    public static TheoryData<int> MyBatisBodies()
    {
        var data = new TheoryData<int>();
        int n = CdataBodies(Resource("query-format-mybatis.xml")).Count;
        for (int i = 0; i < n; i++) data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(MyBatisBodies))]
    public void MyBatis_cdata_sql_formats_to_the_sample_in_all_variants(int index)
    {
        var golden = CdataBodies(Resource("query-format-mybatis.xml"))[index];
        Assert.Equal(golden, FmtCdata(golden));
        Assert.Equal(golden, FmtCdata(golden[..8] + Collapse(golden)));
        Assert.Equal(golden, FmtCdata(Scramble(golden, keepFirstIndent: true)));
        var once = FmtCdata(golden[..8] + Collapse(golden));
        Assert.Equal(once, FmtCdata(once));
    }

    [Fact]
    public void Whole_mybatis_file_has_zero_difference()
    {
        var xml = Resource("query-format-mybatis.xml");
        var sb = new StringBuilder(xml);
        foreach (var body in CdataBodies(xml)) sb.Replace(body, FmtCdata(body));
        Assert.Equal(xml, sb.ToString());
        Assert.Equal(4, CdataBodies(xml).Count);
    }

    /// <summary>예외 ①: 사용자 원문(한 줄 두 항목·쉼표 뒤 공백 없음)은 항목 한 줄에 하나·쉼표 뒤 한 칸으로 맞춘다.</summary>
    [Fact]
    public void Analysis_user_original_is_normalized()
    {
        const string original = """
            SELECT A.PROC_CD
                 , SUBSTR(A.PDN_PST_DD, 1, 6) "년월", A.PRD_NM_CD 품명
                 , SUBSTR(MTL_CD, 7,1) SURFACE
                 , SUBSTR(MTL_CD, 9,2) 후처리
                 , SUM(UNIT_WGT) / 1000 "중량(T)"
                 , SUM(UNIT_WTH * UNIT_LTH) / 1000 "면적(M2)"
                 , SUM(UNIT_WK_TIM) "작업시간(분)"
            FROM   TB_ORD_PRD_ACT_CMN A
            WHERE  A.PDN_PST_DD BETWEEN '20250101' AND '20250102'
            AND    A.PROC_CD IN ('A1', 'B2', 'C3', 'D1', 'D2', 'D3', 'D4')
            GROUP BY A.PROC_CD
                 , SUBSTR(A.PDN_PST_DD, 1, 6)
                 , A.PRD_NM_CD
                 , SUBSTR(MTL_CD, 7,1)
                 , SUBSTR(MTL_CD, 9,2)
            ORDER BY A.PROC_CD, SUBSTR(A.PDN_PST_DD, 1, 6), A.PRD_NM_CD, SUBSTR(MTL_CD, 7,1), SUBSTR(MTL_CD, 9,2);
            """;
        var golden = Statements(Resource("query-format-analysis.sql"))[0];
        string sql = golden[(golden.IndexOf("\nSELECT", StringComparison.Ordinal) + 1)..];
        Assert.Equal(sql, Fmt(original.Replace("\r\n", "\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void Test_helpers_make_the_expected_variants()
    {
        const string s = "-- head\nSELECT A.X   -- tail\n     , 'Ab  C' \"Q  r\"\nFROM   T A\nWHERE  A.Y = :fromDd";
        Assert.Equal("-- head\nSELECT A.X   -- tail\n, 'Ab  C' \"Q  r\" FROM T A WHERE A.Y = :fromDd", Collapse(s));
        var sc = Scramble(s);
        Assert.Contains("select a.x   -- tail", sc, StringComparison.Ordinal);
        Assert.Contains("'Ab  C' \"Q  r\"", sc, StringComparison.Ordinal);
        Assert.Contains(":fromDd", sc, StringComparison.Ordinal);
        Assert.Equal("A = #{p} OR #{lotNo}", FromBinds(ToBinds("A = #{p} OR #{lotNo}")));
        Assert.Equal("#{p}".Length, ToBinds("#{p}").Length);
    }
}
