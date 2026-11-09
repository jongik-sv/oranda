using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Text;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// 복사가 보내는 문장(<see cref="SqlStatement"/>)을 한곳에서 만든다. 사전 점검의 미리 보기와 실행기가 같은 글을 쓰게 하려는 것이다:
/// 화면이 미리 받은 확인(<see cref="ExecutionOptions.PreConfirmed"/>)은 글을 서수로 비교하므로, 글이 한 글자라도 다르면 확인이 묻힌다.
/// 식별자는 모두 <see cref="SqlIdentifier.QuoteAlways"/> 로 감싼다. 문장만 만들고 실행하지 않는다.
/// </summary>
internal static class CopyStatements
{
    /// <summary>배치마다 거는 세이브포인트. 배열 DML 이 실패하면 여기로 되돌린 뒤 한 행씩 다시 넣는다.</summary>
    public const string BatchSavepoint = "ORANDA_COPY_BATCH";

    /// <summary>표 적재를 시작할 때 거는 세이브포인트. 실패 행 상한을 넘은 표를 되돌릴 때 쓴다.</summary>
    public const string TableSavepoint = "ORANDA_COPY_TABLE";

    /// <summary>시퀀스 보정 문장의 바인드 이름(한 번에 소비할 개수).</summary>
    public const string NextValuesBind = "N";

    public static string Qualified(string owner, string name) => SqlIdentifier.QuoteAlways(owner) + "." + SqlIdentifier.QuoteAlways(name);

    /// <summary>조회 문장. 원본 읽기·건수·카탈로그 조회 모두 이것으로 만든다(대량 읽기는 첫 낱말이 SELECT·WITH 인지 다시 본다).</summary>
    public static SqlStatement Query(string sql) => CopyCommonStatements.Query(sql);

    public static SqlStatement Query(SqlWithBinds sql) => CopyCommonStatements.Query(sql);

    public static SqlStatement SetTransactionReadOnly() =>
        new("SET TRANSACTION READ ONLY", default, StatementKind.TransactionControl) { Verb = "SET TRANSACTION" };

    public static SqlStatement Commit() => new("COMMIT", default, StatementKind.TransactionControl) { Verb = "COMMIT" };

    public static SqlStatement Rollback() => new("ROLLBACK", default, StatementKind.TransactionControl) { Verb = "ROLLBACK" };

    public static SqlStatement Savepoint(string name) =>
        new("SAVEPOINT " + name, default, StatementKind.TransactionControl) { Verb = "SAVEPOINT" };

    public static SqlStatement RollbackTo(string name) =>
        new("ROLLBACK TO SAVEPOINT " + name, default, StatementKind.TransactionControl) { Verb = "ROLLBACK" };

    /// <summary>대상 DELETE(<see cref="CopySqlBuilder.TargetDelete"/>). WHERE 가 없으면 정책이 확인을 내도록 <c>IsUnfilteredDml</c> 을 붙인다.</summary>
    public static SqlStatement Delete(SqlWithBinds delete) => CopyCommonStatements.Delete(delete);

    /// <summary>바인드 배열로 보내는 INSERT·MERGE. 바인드 이름은 <c>C1</c>…<c>Cn</c>(<see cref="CopySqlBuilder.Insert"/>·<see cref="CopySqlBuilder.Merge"/>).</summary>
    public static SqlStatement ArrayDml(string sql, int columnCount)
    {
        var verb = sql.StartsWith("MERGE", StringComparison.Ordinal) ? "MERGE" : "INSERT";
        return new SqlStatement(sql, default, StatementKind.Dml) { Verb = verb, BindNames = BindNames(columnCount) };
    }

    /// <summary><c>C1</c>…<c>Cn</c>.</summary>
    public static IReadOnlyList<string> BindNames(int columnCount) =>
        [.. Enumerable.Range(1, columnCount).Select(i => "C" + i.ToString(CultureInfo.InvariantCulture))];

    public static SqlStatement DisableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER {Qualified(owner, trigger)} DISABLE");

    public static SqlStatement EnableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER {Qualified(owner, trigger)} ENABLE");

    public static SqlStatement DisableConstraint(DisabledObject fk) =>
        Ddl($"ALTER TABLE {Qualified(fk.Owner, fk.Table)} DISABLE CONSTRAINT {SqlIdentifier.QuoteAlways(fk.Name)}");

    /// <summary>다시 만들 표를 참조하는 FK 를 DROP 전에 떼어 낸다(적재 뒤 받아 둔 DDL 로 다시 붙인다).</summary>
    public static SqlStatement DropConstraint(DisabledObject fk) =>
        Ddl($"ALTER TABLE {Qualified(fk.Owner, fk.Table)} DROP CONSTRAINT {SqlIdentifier.QuoteAlways(fk.Name)}");

    /// <summary><paramref name="validate"/> 가 거짓이면 <c>ENABLE NOVALIDATE</c>(부모 없는 자식 행이 있어도 켠다).</summary>
    public static SqlStatement EnableConstraint(DisabledObject fk, bool validate) =>
        Ddl($"ALTER TABLE {Qualified(fk.Owner, fk.Table)} ENABLE {(validate ? "VALIDATE" : "NOVALIDATE")} CONSTRAINT {SqlIdentifier.QuoteAlways(fk.Name)}");

    /// <summary>
    /// 「테이블 다시 만들기」의 DROP(설계 6.2.3). <c>PURGE</c> 를 붙이지 않는다(휴지통에 남는다). 정책의 위험 명령이라
    /// 대상 이름(<see cref="SqlStatement.DangerousTarget"/> = 표 이름) 재입력 확인을 받는다. 변경 일지가 지우기 전 DDL 을 보관한다.
    /// <c>CASCADE CONSTRAINTS</c> 를 붙이지 않는다(조정 결정 16, 계획과 다른 점): 대상 계정에 보이지 않는 다른 스키마의 FK 를 말없이 지우지 않게,
    /// 받아 둔 참조 FK 를 먼저 떼어 낸 뒤 지우고, 남은 FK 가 있으면 서버가 ORA-02449 로 거부한다.
    /// </summary>
    public static SqlStatement DropTable(string owner, string table) =>
        new($"DROP TABLE {Qualified(owner, table)}", default, StatementKind.Ddl)
        {
            Verb = "DROP",
            DangerousTarget = table,
            ChangeTarget = new ChangeTarget(owner, table, "TABLE", ChangeOperation.Drop),
        };

    /// <summary>이 세션의 휴지통을 켠다(DB 기본이 꺼져 있으면 PURGE 없이도 DROP 이 영구 삭제다). 권한이 필요 없고 커밋하지 않는다.</summary>
    public static SqlStatement RecycleBinOn() =>
        new("ALTER SESSION SET RECYCLEBIN = ON", default, StatementKind.Other) { Verb = "ALTER SESSION" };

    /// <summary>다시 만들기·되살리기 DDL(CREATE TABLE·INDEX, COMMENT, ALTER TABLE ADD, CREATE TRIGGER, GRANT). 글은 만든 쪽 그대로 보낸다.</summary>
    public static SqlStatement RecreateDdl(string text)
    {
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return new SqlStatement(text, default, StatementKind.Ddl) { Verb = space < 0 ? text : text[..space] };
    }

    /// <summary>시퀀스 값을 한 번에 여러 개 소비한다(설계 8.4). 바인드 <c>:N</c> = 개수. 행을 끝까지 읽어야 그만큼 소비된다.</summary>
    public static SqlStatement NextValues(string owner, string sequence) =>
        Query($"SELECT {Qualified(owner, sequence)}.NEXTVAL FROM DUAL CONNECT BY LEVEL <= :{NextValuesBind}");

    private static SqlStatement Ddl(string text) => new(text, default, StatementKind.Ddl) { Verb = "ALTER" };
}
