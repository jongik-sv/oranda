using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Text;

namespace Oranda.Language.PlSql;

/// <summary>호출할 프로시저·함수의 위치. <see cref="Package"/> 가 null 이면 단독 프로시저·함수.</summary>
public sealed record CallTarget(string Owner, string? Package, string Name)
{
    /// <summary>호출문에 쓰는 이름(<c>OWNER.PKG.NAME</c>, 필요한 부분만 큰따옴표).</summary>
    public string QualifiedName => Package is null
        ? $"{CallBlockBuilder.Quote(Owner)}.{CallBlockBuilder.Quote(Name)}"
        : $"{CallBlockBuilder.Quote(Owner)}.{CallBlockBuilder.Quote(Package)}.{CallBlockBuilder.Quote(Name)}";
}

/// <summary>대화상자 한 줄에 대응하는 인자 설명. 반환값은 <see cref="IsReturn"/>.</summary>
public sealed record CallParameter(string Name, int Position, string DataType, ArgumentDirection Direction, bool HasDefault, bool IsReturn)
{
    /// <summary>서버에 보낼 바인드 형식. 지원하지 않는 형식이면 null.</summary>
    public BindDataType? BindType { get; init; }

    /// <summary>PL/SQL BOOLEAN — 지역 변수와 숫자 바인드(1·0)로 중개한다.</summary>
    public bool IsBoolean { get; init; }

    /// <summary>지원하지 않는 이유(레코드·컬렉션·객체 등). 지원하면 null.</summary>
    public string? UnsupportedReason { get; init; }

    /// <summary>호출문에 쓰는 바인드 이름(<c>:</c> 제외).</summary>
    public string BindName { get; init; } = string.Empty;

    public bool IsSupported => UnsupportedReason is null;

    public bool IsInput => !IsReturn && Direction is ArgumentDirection.In or ArgumentDirection.InOut;

    public bool IsOutput => IsReturn || Direction is ArgumentDirection.Out or ArgumentDirection.InOut;
}

/// <summary>사용자가 한 인자에 입력한 값. 이름은 대소문자 무시.</summary>
/// <param name="Name">인자 이름(반환값은 <see cref="CallBlockBuilder.ReturnName"/>)</param>
/// <param name="Text">입력 글</param>
/// <param name="IsNull">NULL 을 보낸다</param>
/// <param name="UseDefault">호출에서 이 인자를 빼서 서버 기본값을 쓴다(기본값이 있는 IN 인자만)</param>
public sealed record CallInput(string Name, string Text = "", bool IsNull = false, bool UseDefault = false);

/// <summary>생성한 익명 블록의 OUT 값 한 개(실행 뒤 <c>OutValues</c> 키 = <see cref="BindName"/>).</summary>
public sealed record CallOutput(string BindName, string Label, bool IsBoolean, bool IsCursor);

/// <summary>호출 블록 조립 결과.</summary>
public sealed class CallBlock
{
    public required string Text { get; init; }

    public required SqlStatement Statement { get; init; }

    public required IReadOnlyDictionary<string, BindValue> Binds { get; init; }

    public required IReadOnlyList<CallOutput> Outputs { get; init; }

    /// <summary>실행할 수 없는 이유(값 형식 오류·지원하지 않는 필수 인자). 비어 있으면 실행 가능.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    public bool CanRun => Problems.Count == 0;

    /// <summary>OUT 값을 화면 글로 바꾼다. BOOLEAN 은 TRUE/FALSE, NULL 은 「NULL」.</summary>
    public static string FormatOut(CallOutput output, object? value)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (value is null)
        {
            return "NULL";
        }

        if (output.IsBoolean)
        {
            return value switch
            {
                bool b => b ? "TRUE" : "FALSE",
                decimal d => d != 0 ? "TRUE" : "FALSE",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
            };
        }

        return value switch
        {
            string s => s,
            byte[] raw => Convert.ToHexString(raw),
            DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset t => t.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "NULL",
        };
    }
}

/// <summary>
/// PL/SQL 실행 대화상자(TSK-08-02)의 익명 블록·바인드 조립. 메타데이터(<see cref="ProcedureInfo"/>)와 사용자 입력에서
/// <c>BEGIN :ret := PKG.F(A => :p_a); END;</c> 모양의 블록과 바인드 사전을 만든다. 이름 지정 표기(<c>=&gt;</c>)를 써서
/// 기본값 있는 인자를 빼도 위치가 어긋나지 않는다. 문자열 연결로 값을 끼워 넣지 않고 모두 바인드로 보낸다(SAFE).
/// 이 형식은 어셈블리를 늦게 깨우는 규칙(시작 경로에 Oracle·ANTLR 금지)과 무관한 순수 계산이다.
/// </summary>
public static class CallBlockBuilder
{
    /// <summary>함수 반환값을 가리키는 입력 이름.</summary>
    public const string ReturnName = "<RETURN>";

    private const int MaxBindNameLength = 28;

    /// <summary>식별자를 필요할 때만 큰따옴표로 감싼다(대문자 단순 이름은 그대로).</summary>
    public static string Quote(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return SqlIdentifier.Quote(name, IsReserved);
    }

    /// <summary>오버로드 한 개를 대화상자 줄로 풀어 준다(반환값이 있으면 맨 앞). 지원하지 않는 형식은 이유를 단다.</summary>
    public static IReadOnlyList<CallParameter> Describe(ProcedureInfo procedure)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        var list = new List<CallParameter>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (procedure.IsFunction)
        {
            var type = procedure.ReturnType ?? "UNKNOWN";
            var returned = Classify(new ArgumentInfo(ReturnName, 0, type, ArgumentDirection.Out), isReturn: true);
            used.Add("RET");
            list.Add(returned with { BindName = "RET" });
        }

        foreach (var a in procedure.Arguments.OrderBy(x => x.Position))
        {
            var p = Classify(a, isReturn: false);
            var bind = ("P_" + a.Name).ToUpperInvariant();
            if (bind.Length > MaxBindNameLength || !IsPlainBindName(bind))
            {
                bind = "B" + a.Position.ToString(CultureInfo.InvariantCulture);
            }

            while (!used.Add(bind))
            {
                bind += "_";
            }

            list.Add(p with { BindName = bind });
        }

        return list;
    }

    /// <summary>호출 블록과 바인드를 만든다. 입력에 없는 인자는 값 없음(NULL) 으로 본다.</summary>
    public static CallBlock Build(CallTarget target, ProcedureInfo procedure, IEnumerable<CallInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(procedure);
        ArgumentNullException.ThrowIfNull(inputs);

        var byName = new Dictionary<string, CallInput>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in inputs)
        {
            byName[i.Name] = i;
        }

        var parameters = Describe(procedure);
        var problems = new List<string>();
        var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        var bindNames = new List<string>();
        var outputs = new List<CallOutput>();
        var declare = new List<string>();
        var before = new List<string>();
        var after = new List<string>();
        var callArgs = new List<string>();
        string? returnExpr = null;

        foreach (var p in parameters)
        {
            byName.TryGetValue(p.Name, out var input);
            var label = p.IsReturn ? "반환값" : p.Name;
            bool omit = !p.IsReturn && p.HasDefault && p.Direction == ArgumentDirection.In && (input?.UseDefault ?? false);

            if (!p.IsSupported)
            {
                // 기본값이 있는 IN 인자는 빼고 부르면 된다. 그 밖에는 호출할 수 없다.
                if (!p.IsReturn && p.HasDefault && p.Direction == ArgumentDirection.In)
                {
                    continue;
                }

                problems.Add($"{label}: {p.UnsupportedReason}");
                continue;
            }

            if (omit)
            {
                continue;
            }

            var direction = p.IsReturn || p.Direction == ArgumentDirection.Out ? BindDirection.Out
                : p.Direction == ArgumentDirection.InOut ? BindDirection.InOut
                : BindDirection.In;
            var bindType = p.IsBoolean ? BindDataType.Number : p.BindType!.Value;

            object? value = null;
            if (direction != BindDirection.Out && p.BindType != BindDataType.RefCursor)
            {
                if (!TryParse(p, input, out value, out var error))
                {
                    problems.Add($"{label}: {error}");
                    continue;
                }
            }

            binds[p.BindName] = new BindValue(bindType, value, direction);
            bindNames.Add(p.BindName);
            if (direction != BindDirection.In)
            {
                outputs.Add(new CallOutput(p.BindName, label, p.IsBoolean, p.BindType == BindDataType.RefCursor));
            }

            string actual = ":" + p.BindName;
            if (p.IsBoolean)
            {
                var local = "V_" + p.BindName;
                declare.Add($"  {local} BOOLEAN;");
                if (direction != BindDirection.Out)
                {
                    before.Add($"  {local} := CASE :{p.BindName} WHEN 1 THEN TRUE WHEN 0 THEN FALSE END;");
                }

                if (direction != BindDirection.In)
                {
                    after.Add($"  :{p.BindName} := CASE WHEN {local} THEN 1 WHEN NOT {local} THEN 0 END;");
                }

                actual = local;
            }

            if (p.IsReturn)
            {
                returnExpr = actual;
            }
            else
            {
                callArgs.Add($"{Quote(p.Name)} => {actual}");
            }
        }

        var text = Compose(target, declare, before, after, callArgs, returnExpr);
        var verb = declare.Count > 0 ? "DECLARE" : "BEGIN";
        var stmt = new SqlStatement(text, default, StatementKind.PlSql) { Verb = verb, BindNames = bindNames };
        return new CallBlock { Text = text, Statement = stmt, Binds = binds, Outputs = outputs, Problems = problems };
    }

    private static string Compose(CallTarget target, List<string> declare, List<string> before, List<string> after,
                                  List<string> callArgs, string? returnExpr)
    {
        var sb = new StringBuilder();
        if (declare.Count > 0)
        {
            sb.AppendLine("DECLARE");
            foreach (var d in declare)
            {
                sb.AppendLine(d);
            }
        }

        sb.AppendLine("BEGIN");
        foreach (var b in before)
        {
            sb.AppendLine(b);
        }

        var head = returnExpr is null ? "  " : $"  {returnExpr} := ";
        var call = target.QualifiedName;
        if (callArgs.Count == 0)
        {
            sb.Append(head).Append(call).AppendLine(";");
        }
        else
        {
            sb.Append(head).Append(call).AppendLine("(");
            for (int i = 0; i < callArgs.Count; i++)
            {
                sb.Append("    ").Append(callArgs[i]).AppendLine(i < callArgs.Count - 1 ? "," : ");");
            }
        }

        foreach (var a in after)
        {
            sb.AppendLine(a);
        }

        sb.Append("END;");
        return sb.ToString();
    }

    // ---- 형식 ------------------------------------------------------------------------------

    private static CallParameter Classify(ArgumentInfo a, bool isReturn)
    {
        var baseParam = new CallParameter(a.Name, a.Position, a.DataType, a.Direction, a.HasDefault, isReturn);
        var t = a.DataType.Trim().ToUpperInvariant();
        BindDataType? bind = t switch
        {
            "VARCHAR2" or "VARCHAR" or "LONG" or "ROWID" or "UROWID" or "STRING" => BindDataType.Varchar2,
            "NVARCHAR2" or "NCHAR VARYING" => BindDataType.NVarchar2,
            "CHAR" or "NCHAR" or "CHARACTER" => BindDataType.FixedChar,
            "NUMBER" or "FLOAT" or "INTEGER" or "INT" or "SMALLINT" or "DECIMAL" or "NUMERIC" or "REAL"
                or "BINARY_INTEGER" or "PLS_INTEGER" or "NATURAL" or "NATURALN" or "POSITIVE" or "POSITIVEN"
                or "SIMPLE_INTEGER" or "PL/SQL BINARY INTEGER" or "PL/SQL PLS INTEGER" => BindDataType.Number,
            "BINARY_FLOAT" or "BINARY_DOUBLE" or "DOUBLE PRECISION" => BindDataType.BinaryDouble,
            "DATE" => BindDataType.Date,
            "TIMESTAMP" or "TIMESTAMP WITH LOCAL TIME ZONE" => BindDataType.Timestamp,
            "TIMESTAMP WITH TIME ZONE" => BindDataType.TimestampTz,
            "CLOB" or "NCLOB" => BindDataType.Clob,
            "BLOB" => BindDataType.Blob,
            "RAW" or "LONG RAW" => BindDataType.Raw,
            "REF CURSOR" or "SYS_REFCURSOR" => BindDataType.RefCursor,
            _ => null,
        };
        if (bind is { } known)
        {
            return known == BindDataType.RefCursor && !isReturn && a.Direction == ArgumentDirection.In
                ? baseParam with { UnsupportedReason = "REF CURSOR 는 입력(IN)으로 보낼 수 없습니다. 편집기에서 직접 열어 넘기세요." }
                : baseParam with { BindType = known };
        }

        if (t is "PL/SQL BOOLEAN" or "BOOLEAN")
        {
            return baseParam with { IsBoolean = true, BindType = BindDataType.Number };
        }

        var reason = t switch
        {
            "PL/SQL RECORD" => "레코드 형식은 대화상자에서 입력할 수 없습니다. 편집기에서 직접 호출하세요.",
            "PL/SQL TABLE" or "TABLE" or "VARRAY" or "NESTED TABLE" or "VARRAY TABLE" =>
                "컬렉션 형식은 대화상자에서 입력할 수 없습니다. 편집기에서 직접 호출하세요.",
            "OBJECT" or "REF" or "XMLTYPE" or "JSON" or "ANYDATA" or "ANYTYPE" =>
                "객체·XML·JSON 형식은 대화상자에서 입력할 수 없습니다. 편집기에서 직접 호출하세요.",
            _ => $"형식 {a.DataType} 은(는) 대화상자에서 지원하지 않습니다.",
        };
        return baseParam with { UnsupportedReason = reason };
    }

    private static bool TryParse(CallParameter p, CallInput? input, out object? value, out string? error)
    {
        value = null;
        error = null;
        if (input is null)
        {
            return true;
        }

        if (input.IsNull)
        {
            return true;
        }

        var text = input.Text ?? string.Empty;
        var bind = p.BindType!.Value;
        if (p.IsBoolean)
        {
            if (text.Trim().Length == 0)
            {
                return true;
            }

            switch (text.Trim().ToUpperInvariant())
            {
                case "TRUE" or "T" or "Y" or "1":
                    value = 1m;
                    return true;
                case "FALSE" or "F" or "N" or "0":
                    value = 0m;
                    return true;
                default:
                    error = $"'{text}' 은(는) BOOLEAN 값(TRUE/FALSE)이 아닙니다.";
                    return false;
            }
        }

        if (text.Length == 0 && bind is not (BindDataType.Varchar2 or BindDataType.NVarchar2 or BindDataType.FixedChar or BindDataType.Clob))
        {
            return true; // 빈 칸 = NULL
        }

        try
        {
            value = bind switch
            {
                BindDataType.Number => decimal.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                BindDataType.BinaryDouble => double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                BindDataType.Date or BindDataType.Timestamp => DateTime.Parse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None),
                BindDataType.TimestampTz => DateTimeOffset.Parse(text.Trim(), CultureInfo.InvariantCulture),
                BindDataType.Raw or BindDataType.Blob => Convert.FromHexString(text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal)),
                _ => text,
            };
            return true;
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            error = $"'{text}' 을(를) {p.DataType} 로 읽을 수 없습니다.";
            return false;
        }
    }

    private static bool IsPlainBindName(string name) => PlainBind.IsMatch(name);

    private static readonly Regex PlainBind = new("^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool IsReserved(string name) => ReservedWords.Contains(name);

    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "ACCESS", "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUDIT", "BETWEEN", "BY", "CHAR", "CHECK", "CLUSTER",
        "COLUMN", "COMMENT", "COMPRESS", "CONNECT", "CREATE", "CURRENT", "DATE", "DECIMAL", "DEFAULT", "DELETE", "DESC",
        "DISTINCT", "DROP", "ELSE", "EXCLUSIVE", "EXISTS", "FILE", "FLOAT", "FOR", "FROM", "GRANT", "GROUP", "HAVING",
        "IDENTIFIED", "IMMEDIATE", "IN", "INCREMENT", "INDEX", "INITIAL", "INSERT", "INTEGER", "INTERSECT", "INTO", "IS",
        "LEVEL", "LIKE", "LOCK", "LONG", "MAXEXTENTS", "MINUS", "MLSLABEL", "MODE", "MODIFY", "NOAUDIT", "NOCOMPRESS",
        "NOT", "NOWAIT", "NULL", "NUMBER", "OF", "OFFLINE", "ON", "ONLINE", "OPTION", "OR", "ORDER", "PCTFREE", "PRIOR",
        "PUBLIC", "RAW", "RENAME", "RESOURCE", "REVOKE", "ROW", "ROWID", "ROWNUM", "ROWS", "SELECT", "SESSION", "SET",
        "SHARE", "SIZE", "SMALLINT", "START", "SUCCESSFUL", "SYNONYM", "SYSDATE", "TABLE", "THEN", "TO", "TRIGGER", "UID",
        "UNION", "UNIQUE", "UPDATE", "USER", "VALIDATE", "VALUES", "VARCHAR", "VARCHAR2", "VIEW", "WHENEVER", "WHERE", "WITH",
    };
}
