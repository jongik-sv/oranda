using System.Text.RegularExpressions;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema;
using Oranda.Core.Text;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 객체 관리 문장 만들기(INVALID 컴파일·휴지통 Purge/Flashback·파라미터 변경 DDL) — Oracle <c>OracleSchemaStatements</c> 대응(D1).
/// 문장만 만들고 실행하지 않는다(실행은 <c>IExecutionService</c> 정책 관문으로). 세션을 쓰지 않는 순수 문장 조립이다.
/// Oracle 과 달라진 점: Tibero 에는 Java 저장 객체가 없어 JAVA SOURCE·JAVA CLASS 는 컴파일 대상이 아니다(null).
/// PURGE·FLASHBACK 문법은 Oracle 과 같다고 보고 만들었으며, 서버에서 통하는지는 쓰기 IT(<c>TiberoSchemaStatementsIt</c>)가 확인한다.
/// 파라미터 변경의 SCOPE 절은 Tibero 서버에서 측정하지 않았다(미리보기 문장일 뿐이고 이 서비스는 실행하지 않는다).
/// </summary>
[OrandaService(typeof(ISchemaStatements), Dbms = Dbms.Tibero)]
internal sealed partial class TiberoSchemaStatements : ISchemaStatements
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$#.]*$")]
    private static partial Regex ParameterNameRegex();

    [GeneratedRegex(@"^(\d+[KMGTkmgt]?|TRUE|FALSE|true|false)$")]
    private static partial Regex BareValueRegex();

    public Dbms Dbms => Dbms.Tibero;

    /// <summary>컴파일할 수 없는 유형(Java 포함)이면 null.</summary>
    public SqlStatement? Compile(DbObjectRef obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var target = string.Equals(obj.Owner, "PUBLIC", StringComparison.Ordinal) && obj.ObjectType == "SYNONYM"
            ? Quote(obj.Name)
            : Quote(obj.Owner) + "." + Quote(obj.Name);
        var text = obj.ObjectType switch
        {
            "PACKAGE BODY" => $"ALTER PACKAGE {target} COMPILE BODY",
            "TYPE BODY" => $"ALTER TYPE {target} COMPILE BODY",
            "SYNONYM" when string.Equals(obj.Owner, "PUBLIC", StringComparison.Ordinal) => $"ALTER PUBLIC SYNONYM {target} COMPILE",
            "PACKAGE" or "TYPE" or "PROCEDURE" or "FUNCTION" or "TRIGGER" or "VIEW" or "MATERIALIZED VIEW" or "SYNONYM"
                => $"ALTER {obj.ObjectType} {target} COMPILE",
            _ => null, // JAVA SOURCE·JAVA CLASS 를 포함해, Tibero 에서 컴파일하지 않는 유형
        };
        return text is null ? null : new SqlStatement(text, default, StatementKind.Ddl) { Verb = "ALTER" };
    }

    /// <summary>휴지통 항목 하나를 영구 삭제한다. 되돌릴 수 없어 이름 재입력 확인 대상이다.</summary>
    public SqlStatement PurgeItem(RecycleBinItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var kind = string.Equals(item.ObjectType, "INDEX", StringComparison.OrdinalIgnoreCase) ? "INDEX" : "TABLE";
        return new SqlStatement($"PURGE {kind} {Quote(item.ObjectName)}", default, StatementKind.Ddl)
        {
            Verb = "PURGE",
            DangerousTarget = item.OriginalName,
        };
    }

    /// <summary>현재 스키마의 휴지통을 모두 비운다.</summary>
    public SqlStatement PurgeAll()
        => new("PURGE RECYCLEBIN", default, StatementKind.Ddl) { Verb = "PURGE", DangerousTarget = "RECYCLEBIN" };

    /// <summary>휴지통의 테이블을 되살린다. <paramref name="renameTo"/> 가 있으면 그 이름으로.</summary>
    public SqlStatement Flashback(RecycleBinItem item, string? renameTo = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var text = $"FLASHBACK TABLE {Quote(item.ObjectName)} TO BEFORE DROP";
        if (!string.IsNullOrWhiteSpace(renameTo))
        {
            text += " RENAME TO " + Quote(renameTo.Trim());
        }

        return new SqlStatement(text, default, StatementKind.Ddl) { Verb = "FLASHBACK" };
    }

    /// <summary>
    /// 파라미터 변경 DDL 미리보기 문장(<c>ALTER SYSTEM SET</c>). 실행하지 않는다.
    /// Tibero 의 <c>SCOPE</c> 절은 서버로 확인하지 않았다(ALTER SYSTEM 은 시험에서 돌리지 않는다). 그래서 확인된 꼴만 낸다:
    /// 메모리(지금 인스턴스) = <c>SCOPE</c> 없는 문장, 파라미터 파일(SPFILE·BOTH)·정의되지 않은 범위 = <see cref="NotSupportedException"/>(이유는 화면의 미리보기 오류로 보인다).
    /// </summary>
    public string ParameterChangeDdl(string name, string value, ParameterScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        if (!ParameterNameRegex().IsMatch(name))
        {
            throw new ArgumentException("파라미터 이름이 올바르지 않습니다: " + name, nameof(name));
        }

        if (scope != ParameterScope.Memory)
        {
            throw new NotSupportedException("Tibero 의 파라미터 파일 적용 범위(SPFILE·BOTH) 문장은 아직 확인하지 않아 만들지 않습니다. 메모리 범위만 됩니다: " + scope);
        }

        var v = value.Trim();
        var literal = v.Length == 0 ? "''" : BareValueRegex().IsMatch(v) ? v : "'" + v.Replace("'", "''", StringComparison.Ordinal) + "'";
        return $"ALTER SYSTEM SET {name.Trim()} = {literal};";
    }

    private static string Quote(string identifier) => SqlIdentifier.QuoteAlways(identifier);
}
