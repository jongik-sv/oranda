using System.Text.RegularExpressions;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Text;

namespace Oranda.Oracle.Schema;

/// <summary>파라미터 변경 적용 범위(ALTER SYSTEM SET … SCOPE).</summary>
public enum ParameterScope
{
    Memory,
    Spfile,
    Both,
}

/// <summary>
/// 객체 관리 문장 만들기(INVALID 컴파일·휴지통 Purge/Flashback·파라미터 변경 DDL). 문장만 만들고 실행하지 않는다 —
/// 실행은 반드시 <c>IExecutionService</c>(정책 관문)로 보낸다. 소유: TSK-07-04.
/// </summary>
public static partial class SchemaStatements
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$#.]*$")]
    private static partial Regex ParameterNameRegex();

    [GeneratedRegex(@"^(\d+[KMGTkmgt]?|TRUE|FALSE|true|false)$")]
    private static partial Regex BareValueRegex();

    public static string Quote(string identifier) => SqlIdentifier.QuoteAlways(identifier);

    /// <summary>컴파일할 수 없는 유형이면 null.</summary>
    public static SqlStatement? Compile(DbObjectRef obj)
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
            _ => null,
        };
        return text is null ? null : new SqlStatement(text, default, StatementKind.Ddl) { Verb = "ALTER" };
    }

    /// <summary>휴지통 항목 하나를 영구 삭제한다. 되돌릴 수 없어 이름 재입력 확인 대상이다.</summary>
    public static SqlStatement PurgeItem(RecycleBinItem item)
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
    public static SqlStatement PurgeAll()
        => new("PURGE RECYCLEBIN", default, StatementKind.Ddl) { Verb = "PURGE", DangerousTarget = "RECYCLEBIN" };

    /// <summary>휴지통의 테이블을 되살린다. <paramref name="renameTo"/> 가 있으면 그 이름으로.</summary>
    public static SqlStatement Flashback(RecycleBinItem item, string? renameTo = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var text = $"FLASHBACK TABLE {Quote(item.ObjectName)} TO BEFORE DROP";
        if (!string.IsNullOrWhiteSpace(renameTo))
        {
            text += " RENAME TO " + Quote(renameTo.Trim());
        }

        return new SqlStatement(text, default, StatementKind.Ddl) { Verb = "FLASHBACK" };
    }

    /// <summary>파라미터 변경 DDL 미리보기 문장(<c>ALTER SYSTEM SET</c>). 실행하지 않는다.</summary>
    public static string ParameterChangeDdl(string name, string value, ParameterScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        if (!ParameterNameRegex().IsMatch(name))
        {
            throw new ArgumentException("파라미터 이름이 올바르지 않습니다: " + name, nameof(name));
        }

        var v = value.Trim();
        var literal = v.Length == 0 ? "''" : BareValueRegex().IsMatch(v) ? v : "'" + v.Replace("'", "''", StringComparison.Ordinal) + "'";
        var scopeText = scope switch
        {
            ParameterScope.Memory => "MEMORY",
            ParameterScope.Spfile => "SPFILE",
            _ => "BOTH",
        };
        return $"ALTER SYSTEM SET {name.Trim()} = {literal} SCOPE = {scopeText};";
    }
}
