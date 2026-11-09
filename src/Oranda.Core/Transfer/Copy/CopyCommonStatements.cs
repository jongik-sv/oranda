using Oranda.Core.Execution;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 복사 문장 가운데 DBMS 와 무관한 것(조회·대상 DELETE). DBMS 마다 다른 문장은 <see cref="ICopyDialect"/>.
/// Oracle 실행기의 <c>CopyStatements.Query</c>·<c>Delete</c> 가 이것을 부르므로 미리 보기와 실행기의 글이 같다.
/// </summary>
public static class CopyCommonStatements
{
    /// <summary>조회 문장. 원본 읽기·건수·카탈로그 조회 모두 이것으로 만든다(대량 읽기는 첫 낱말이 SELECT·WITH 인지 다시 본다).</summary>
    public static SqlStatement Query(string sql) => new(sql, default, StatementKind.Query) { Verb = "SELECT" };

    /// <inheritdoc cref="Query(string)"/>
    public static SqlStatement Query(SqlWithBinds sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return Query(sql.Sql);
    }

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
}
