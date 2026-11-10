using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Text;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 복사 문장 가운데 DBMS 와 무관한 것(조회·대상 DELETE·배열 DML·COMMIT·ROLLBACK·되살리기 DDL 감싸기). DBMS 마다 다른 문장은 <see cref="ICopyDialect"/>.
/// Oracle 실행기의 <c>CopyStatements</c> 같은 이름 멤버가 이것을 부르므로 미리 보기와 실행기의 글이 같다.
/// 식별자는 모두 <see cref="SqlIdentifier.QuoteAlways"/> 로 감싼다. 문장만 만들고 실행하지 않는다.
/// </summary>
public static class CopyCommonStatements
{
    /// <summary>배치마다 거는 세이브포인트. 배열 DML 이 실패하면 여기로 되돌린 뒤 한 행씩 다시 넣는다.</summary>
    public const string BatchSavepoint = "ORANDA_COPY_BATCH";

    /// <summary>표 적재를 시작할 때 거는 세이브포인트. 실패 행 상한을 넘은 표를 되돌릴 때 쓴다.</summary>
    public const string TableSavepoint = "ORANDA_COPY_TABLE";

    /// <summary>시퀀스 보정 문장(<see cref="ICopyDialect.NextValues"/>)의 바인드 이름(한 번에 소비할 개수).</summary>
    public const string NextValuesBind = "N";

    /// <summary><c>"OWNER"."NAME"</c>.</summary>
    public static string Qualified(string owner, string name) => SqlIdentifier.QuoteAlways(owner) + "." + SqlIdentifier.QuoteAlways(name);

    /// <summary>조회 문장. 원본 읽기·건수·카탈로그 조회 모두 이것으로 만든다(대량 읽기는 첫 낱말이 SELECT·WITH 인지 다시 본다).</summary>
    public static SqlStatement Query(string sql) => new(sql, default, StatementKind.Query) { Verb = "SELECT" };

    /// <inheritdoc cref="Query(string)"/>
    public static SqlStatement Query(SqlWithBinds sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return Query(sql.Sql);
    }

    public static SqlStatement Commit() => new("COMMIT", default, StatementKind.TransactionControl) { Verb = "COMMIT" };

    public static SqlStatement Rollback() => new("ROLLBACK", default, StatementKind.TransactionControl) { Verb = "ROLLBACK" };

    /// <summary>대상 DELETE(<see cref="CopySqlBuilder.TargetDelete"/>). WHERE 가 없으면 정책이 확인을 내도록 <c>IsUnfilteredDml</c> 을 붙인다.</summary>
    public static SqlStatement Delete(SqlWithBinds delete)
    {
        ArgumentNullException.ThrowIfNull(delete);
        return new(delete.Sql, default, StatementKind.Dml)
        {
            Verb = "DELETE",
            IsUnfilteredDml = !delete.Sql.Contains(" WHERE ", StringComparison.Ordinal),
        };
    }

    /// <summary>바인드 배열로 보내는 INSERT·MERGE. 바인드 이름은 <c>C1</c>…<c>Cn</c>(<see cref="CopySqlBuilder.Insert"/>·<see cref="CopySqlBuilder.Merge"/>).</summary>
    public static SqlStatement ArrayDml(string sql, int columnCount)
    {
        var verb = sql.StartsWith("MERGE", StringComparison.Ordinal) ? "MERGE" : "INSERT";
        return new SqlStatement(sql, default, StatementKind.Dml) { Verb = verb, BindNames = BindNames(columnCount) };
    }

    /// <summary><c>C1</c>…<c>Cn</c>.</summary>
    public static IReadOnlyList<string> BindNames(int columnCount) =>
        [.. Enumerable.Range(1, columnCount).Select(i => "C" + i.ToString(CultureInfo.InvariantCulture))];

    /// <summary>
    /// 「없으면 만들기」로 방금 만든 표를 적재 실패 뒤 지우는 DROP(<c>PURGE</c>: 휴지통을 거치지 않는다. 되살릴 것이 없는 표다).
    /// 정책의 위험 명령이라 대상 이름 재입력 확인을 받는다(사전 점검 미리 보기에 들어 있어 미리 받은 확인으로 넘어간다).
    /// <c>CASCADE CONSTRAINTS</c> 를 붙이지 않는다: 작업 안 FK 는 적재 뒤에 붙이므로 지우는 시점에는 이 표를 참조하는 FK 가 없다.
    /// </summary>
    public static SqlStatement DropCreatedTable(string owner, string table) =>
        new($"DROP TABLE {Qualified(owner, table)} PURGE", default, StatementKind.Ddl)
        {
            Verb = "DROP",
            DangerousTarget = table,
            ChangeTarget = new ChangeTarget(owner, table, "TABLE", ChangeOperation.Drop),
        };

    /// <summary>
    /// 이번 실행이 테이블보다 먼저 만든 시퀀스(<see cref="CopySequencePlan"/>)를, 그 시퀀스를 쓰는 테이블을 모두 만들지 못했거나 적재를 끝내지 못해 지웠을 때 함께 지우는 DROP.
    /// 정책의 위험 명령이라 사전 점검 미리 보기에 같은 글이 들어 있어 미리 받은 확인으로 넘어간다.
    /// </summary>
    public static SqlStatement DropCreatedSequence(string owner, string sequence) =>
        new($"DROP SEQUENCE {Qualified(owner, sequence)}", default, StatementKind.Ddl)
        {
            Verb = "DROP",
            DangerousTarget = sequence,
            ChangeTarget = new ChangeTarget(owner, sequence, "SEQUENCE", ChangeOperation.Drop),
        };

    /// <summary>다시 만들기·되살리기 DDL(CREATE TABLE·INDEX, COMMENT, ALTER TABLE ADD, CREATE TRIGGER, GRANT). 글은 만든 쪽 그대로 보낸다.</summary>
    public static SqlStatement RecreateDdl(string text)
    {
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return new SqlStatement(text, default, StatementKind.Ddl) { Verb = space < 0 ? text : text[..space] };
    }
}
