using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Execution;
using Oranda.Core.Rollback;
using Oranda.Core.Security;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.ChangeJournal;

/// <summary>
/// 변경 일지의 문장 목록용: 바인드 자리(<c>:v1</c>)를 저장된 값의 리터럴로 바꾼 글을 만든다(보여 주기만 한다 — 실행한 문장은 바뀌지 않는다).
/// 자리는 렉서의 바인드 토큰 위치로만 찾아 문자열·주석 안의 <c>:x</c> 는 건드리지 않는다.
/// 값이 저장되지 않은 자리(민감한 이름·저장 목록에 없음·커서)는 이름 그대로 두어 값을 지어내지 않는다.
/// </summary>
internal static partial class BindLiteralSubstituter
{
    /// <summary>값 글자가 이 수를 넘으면 앞부분만 보이고 「…(N자)」 를 붙인다.</summary>
    public const int MaxValueChars = 200;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [GeneratedRegex(@"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$")]
    private static partial Regex PlainNumber();

    public static string Substitute(string sql, IReadOnlyList<RollbackBind>? binds, int maxValueChars = MaxValueChars)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (binds is null || binds.Count == 0 || sql.IndexOf(':', StringComparison.Ordinal) < 0)
        {
            return sql;
        }

        var byName = new Dictionary<string, RollbackBind>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in binds)
        {
            byName.TryAdd(b.Name.TrimStart(':'), b);
        }

        var sb = new StringBuilder(sql.Length + 32);
        var at = 0;
        foreach (var token in SqlLexer.Tokenize(sql))
        {
            if (token.Kind != TokenKind.Bind || token.Start < at)
            {
                continue;
            }

            var name = sql.Substring(token.Start, token.Length).TrimStart(':');
            if (!byName.TryGetValue(name, out var bind) || Literal(bind, maxValueChars) is not { } literal)
            {
                continue;
            }

            sb.Append(sql, at, token.Start - at).Append(literal);
            at = token.End;
        }

        return sb.Append(sql, at, sql.Length - at).ToString();
    }

    /// <summary>바인드 하나의 표시용 리터럴. 값을 모르면 null(이름 그대로 둔다).</summary>
    internal static string? Literal(RollbackBind bind, int maxValueChars = MaxValueChars)
    {
        if (bind.Value is null)
        {
            // 저장 때 민감한 이름의 값은 null 로 비운다 — NULL 이라고 지어내지 않는다.
            return SensitiveBindNames.IsSensitive(bind.Name) ? null : "NULL";
        }

        var v = bind.Value;
        switch (bind.Type)
        {
            case BindDataType.RefCursor:
                return null;
            case BindDataType.Number or BindDataType.BinaryDouble when PlainNumber().IsMatch(v.Trim()):
                // 음수는 괄호로 감싼다: 「qty-:d」 에 -3 을 그대로 넣으면 「qty--3」 이 줄 주석이 된다.
                return v.Trim().StartsWith('-') ? "(" + v.Trim() + ")" : v.Trim();
            case BindDataType.Boolean when bool.TryParse(v, out var b):
                return b ? "TRUE" : "FALSE";
            case BindDataType.Date when DateTime.TryParse(v, Inv, DateTimeStyles.RoundtripKind, out var d):
                return $"TO_DATE('{d.ToString("yyyy-MM-dd HH:mm:ss", Inv)}', 'YYYY-MM-DD HH24:MI:SS')";
            case BindDataType.Timestamp or BindDataType.TimestampLtz when DateTime.TryParse(v, Inv, DateTimeStyles.RoundtripKind, out var t):
                return $"TO_TIMESTAMP('{t.ToString("yyyy-MM-dd HH:mm:ss.ffffff", Inv)}', 'YYYY-MM-DD HH24:MI:SS.FF6')";
            case BindDataType.TimestampTz when DateTimeOffset.TryParse(v, Inv, DateTimeStyles.RoundtripKind, out var o):
                return $"TO_TIMESTAMP_TZ('{o.ToString("yyyy-MM-dd HH:mm:ss.ffffff zzz", Inv)}', 'YYYY-MM-DD HH24:MI:SS.FF6 TZH:TZM')";
            case BindDataType.Raw or BindDataType.Blob:
                return "HEXTORAW(" + Quote(v, maxValueChars) + ")";
            default:
                return Quote(v, maxValueChars);
        }
    }

    private static string Quote(string text, int max)
    {
        if (text.Length > max)
        {
            return "'" + text[..max].Replace("'", "''", StringComparison.Ordinal) + "…(" + text.Length.ToString(Inv) + "자)'";
        }

        return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
