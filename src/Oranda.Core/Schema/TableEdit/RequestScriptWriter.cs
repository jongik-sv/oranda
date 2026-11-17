using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>의뢰용 스크립트 머리 주석에 쓰는 값(설계 9.4). 접속 이름·호스트는 넣지 않는다.</summary>
/// <param name="Name">반영 묶음 이름(편집기의 스크립트 저장은 테이블 이름)</param>
/// <param name="CreatedOn">만든 날짜</param>
/// <param name="Dbms">대상 DBMS</param>
public sealed record RequestScriptHeader(string Name, DateOnly CreatedOn, Dbms Dbms)
{
    /// <summary>
    /// 열 형식 변환(설계 18.9)의 UPDATE·COMMIT 을 담아도 되나(TE-44, 「DDL 만」 규칙의 유일한 예외). 반영 묶음은 「변환 DML」 항목이 있을 때만 켠다.
    /// 켜면 변환 UPDATE(<c>UPDATE … SET &lt;열&gt;$N = …</c>, 임시 열에 쓰는 꼴)와 COMMIT 만 받고, 머리 주석에 <c>-- DML 포함</c>,
    /// UPDATE 뒤에 COMMIT 이 없으면 <c>COMMIT;</c> 을 붙인다. 다른 UPDATE·INSERT·DELETE·MERGE 는 그대로 막는다.
    /// </summary>
    public bool AllowsConversionDml { get; init; }
}

/// <summary>
/// 의뢰용 <c>.sql</c> 글 만들기(설계 9.4, TE-17·TE-22). 글만 만들고 파일은 쓰지 않는다 — 파일은 <see cref="Encoding"/> 으로 쓴다.
/// 머리 주석(이름·만든 날짜·DBMS·문장 수·테이블스페이스 안내) 뒤에 문장을 빈 줄로 나눠 쓴다.
/// 일반 DDL 은 끝에 <c>;</c>, PL/SQL 블록·PL/SQL 단위(프로시저·함수·패키지·트리거·타입) 생성은 다음 줄 <c>/</c>.
/// DDL·PL/SQL 이 아닌 문장이 하나라도 있으면 <see cref="InvalidOperationException"/> — 의뢰 스크립트에는 DDL 만 담는다.
/// 예외 하나(TE-44): <see cref="RequestScriptHeader.AllowsConversionDml"/> 이면 열 형식 변환의 UPDATE·COMMIT.
/// 소유: te-m1 (T2).
/// </summary>
public static class RequestScriptWriter
{
    /// <summary>파일 인코딩: UTF-8, BOM 없음(TE-17).</summary>
    public static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>머리 주석의 테이블스페이스 안내(TE-22).</summary>
    public const string TablespaceNote = "테이블스페이스를 쓰지 않은 문장은 실행 계정의 기본 테이블스페이스에 만들어진다.";

    // CREATE [OR REPLACE] [EDITIONABLE|NONEDITIONABLE] {FUNCTION|PROCEDURE|PACKAGE|TRIGGER|TYPE}
    private static readonly Regex PlsqlUnit = new(
        @"^\s*CREATE\s+(OR\s+REPLACE\s+)?((NON)?EDITIONABLE\s+)?(FUNCTION|PROCEDURE|PACKAGE|TRIGGER|TYPE)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>기본 줄바꿈(<see cref="Environment.NewLine"/>, TE-17)으로 쓴다.</summary>
    public static string Write(RequestScriptHeader header, IReadOnlyList<SqlStatement> statements) =>
        Write(header, statements, Environment.NewLine);

    /// <summary>스크립트 글을 만든다. 문장 안의 줄바꿈도 <paramref name="newline"/> 으로 맞춘다.</summary>
    /// <exception cref="InvalidOperationException">DDL(또는 PL/SQL) 이 아닌 문장이 있거나 문장 글이 비었을 때</exception>
    public static string Write(RequestScriptHeader header, IReadOnlyList<SqlStatement> statements, string newline)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentException.ThrowIfNullOrEmpty(newline);

        // 파일을 쓰기 전에 전부 확인해 일부만 쓰이는 일이 없게 한다.
        var bodies = new List<string>(statements.Count);
        var dml = false;
        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];
            bodies.Add(Body(statement, newline, header.AllowsConversionDml));
            if (statement.Kind == StatementKind.Dml)
            {
                dml = true;
                if (i + 1 >= statements.Count || !IsCommit(statements[i + 1]))
                {
                    bodies.Add("COMMIT;"); // 변환 UPDATE 는 이 단계 끝에 한 번 커밋(설계 18.9)
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append("-- 이름: ").Append(OneLine(header.Name)).Append(newline);
        sb.Append("-- 만든 날짜: ").Append(header.CreatedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(newline);
        sb.Append("-- 대상 DBMS: ").Append(header.Dbms).Append(newline);
        sb.Append("-- 문장 수: ").Append(bodies.Count.ToString(CultureInfo.InvariantCulture)).Append(newline);
        sb.Append("-- ").Append(TablespaceNote).Append(newline);
        if (dml)
        {
            sb.Append(Conversion.ColumnConversionBuilder.DmlHeader).Append(newline);
        }

        foreach (var body in bodies)
        {
            sb.Append(newline).Append(body).Append(newline);
        }

        return sb.ToString();
    }

    private static string Body(SqlStatement statement, string newline, bool allowsConversionDml)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var plsql = statement.Kind switch
        {
            StatementKind.PlSql => true,
            StatementKind.Ddl => PlsqlUnit.IsMatch(statement.Text),
            StatementKind.Dml when allowsConversionDml && IsUpdate(statement) => false,
            StatementKind.TransactionControl when allowsConversionDml && IsCommit(statement) => false,
            _ => throw new InvalidOperationException(
                $"의뢰용 스크립트에는 DDL 만 담을 수 있다(문장 종류: {statement.Kind})."),
        };

        var text = statement.Text.Trim();
        if (!plsql)
        {
            text = text.TrimEnd(';', ' ', '\t', '\r', '\n');
        }

        if (text.Length == 0)
        {
            throw new InvalidOperationException("빈 문장은 의뢰용 스크립트에 쓸 수 없다.");
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", newline, StringComparison.Ordinal);
        return plsql ? text + newline + "/" : text + ";";
    }

    private static bool IsUpdate(SqlStatement s) => ConversionUpdate.IsMatch(s.Text);

    // UPDATE o.t SET C$1 = … / SET "c x$2" = … (ColumnConversionBuilder 의 임시 열)
    private static readonly Regex ConversionUpdate = new(
        @"^\s*UPDATE\s+\S+\s+SET\s+(""[^""]*\$\d+""|[A-Za-z][A-Za-z0-9_#$]*\$\d+)\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsCommit(SqlStatement s) => s.Kind == StatementKind.TransactionControl && FirstWord(s) == "COMMIT";

    private static string FirstWord(SqlStatement s) =>
        Regex.Match(s.Text, @"^\s*([A-Za-z]+)").Groups[1].Value.ToUpperInvariant();

    /// <summary>머리 주석 한 줄에 들어가도록 줄바꿈을 공백 하나로 바꾼다(주석 밖으로 새어 나가 문장이 되는 것을 막는다).</summary>
    private static string OneLine(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s*[\r\n]+\s*", " ").Trim();
}
