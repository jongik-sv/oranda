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
public sealed record RequestScriptHeader(string Name, DateOnly CreatedOn, Dbms Dbms);

/// <summary>
/// 의뢰용 <c>.sql</c> 글 만들기(설계 9.4, TE-17·TE-22). 글만 만들고 파일은 쓰지 않는다 — 파일은 <see cref="Encoding"/> 으로 쓴다.
/// 머리 주석(이름·만든 날짜·DBMS·문장 수·테이블스페이스 안내) 뒤에 문장을 빈 줄로 나눠 쓴다.
/// 일반 DDL 은 끝에 <c>;</c>, PL/SQL 블록·PL/SQL 단위(프로시저·함수·패키지·트리거·타입) 생성은 다음 줄 <c>/</c>.
/// DDL·PL/SQL 이 아닌 문장이 하나라도 있으면 <see cref="InvalidOperationException"/> — 의뢰 스크립트에는 DDL 만 담는다.
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
        foreach (var statement in statements)
        {
            bodies.Add(Body(statement, newline));
        }

        var sb = new StringBuilder();
        sb.Append("-- 이름: ").Append(OneLine(header.Name)).Append(newline);
        sb.Append("-- 만든 날짜: ").Append(header.CreatedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(newline);
        sb.Append("-- 대상 DBMS: ").Append(header.Dbms).Append(newline);
        sb.Append("-- 문장 수: ").Append(statements.Count.ToString(CultureInfo.InvariantCulture)).Append(newline);
        sb.Append("-- ").Append(TablespaceNote).Append(newline);

        foreach (var body in bodies)
        {
            sb.Append(newline).Append(body).Append(newline);
        }

        return sb.ToString();
    }

    private static string Body(SqlStatement statement, string newline)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var plsql = statement.Kind switch
        {
            StatementKind.PlSql => true,
            StatementKind.Ddl => PlsqlUnit.IsMatch(statement.Text),
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

    /// <summary>머리 주석 한 줄에 들어가도록 줄바꿈을 공백 하나로 바꾼다(주석 밖으로 새어 나가 문장이 되는 것을 막는다).</summary>
    private static string OneLine(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s*[\r\n]+\s*", " ").Trim();
}
