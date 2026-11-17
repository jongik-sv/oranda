using System.Globalization;
using Oranda.App.Features.Localization;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Text;

namespace Oranda.App.Features.TableEdit;

/// <summary>편집 칸의 이름·목록 글 ↔ 편집 모델(사전 표기) 바꾸기. 순수 함수.</summary>
public static class TableEditCellText
{
    /// <summary>
    /// 칸에 쓴 이름 → 사전 표기. 큰따옴표로 감싸면 안의 글 그대로(<c>""</c> → <c>"</c>), 아니면 앞뒤 공백을 떼고 대문자(서버가 따옴표 없는 이름을 대문자로 바꾸는 규칙).
    /// </summary>
    public static string Parse(string? text) => Oranda.Core.Schema.TableEdit.TableEditNames.FromInput(text);

    /// <summary>
    /// 형식 칸 글 → 형식 이름과 괄호 안 값. <c>VARCHAR2(20 CHAR)</c>·<c>NUMBER(10,2)</c> 처럼 괄호를 붙여 쓰면 길이·정밀도·소수·길이 기준으로 나눈다
    /// (모델은 형식 이름에 괄호를 넣지 않는다). 서버 사전이 이름에 붙이는 괄호(<c>TIMESTAMP(6)</c>·<c>INTERVAL …</c>)는 그대로 둔다. 괄호가 없으면 Size 는 null.
    /// </summary>
    public static (string? Type, (int? First, int? Second, CharSemantics? Semantics)? Size) ParseType(string? text)
    {
        var t = (text ?? string.Empty).Trim().ToUpperInvariant();
        if (t.Length == 0)
        {
            return (null, null);
        }

        var m = System.Text.RegularExpressions.Regex.Match(t, @"^([A-Z][A-Z0-9_ ]*?)\s*\(\s*(\d+|\*)\s*(?:,\s*(\d+)\s*)?(BYTE|CHAR)?\s*\)$");
        if (!m.Success || m.Groups[1].Value.StartsWith("TIMESTAMP", StringComparison.Ordinal) || m.Groups[1].Value.StartsWith("INTERVAL", StringComparison.Ordinal))
        {
            return (t, null);
        }

        int? Num(System.Text.RegularExpressions.Group g) =>
            g.Success && g.Value != "*" ? int.Parse(g.Value, NumberStyles.Integer, CultureInfo.InvariantCulture) : null;
        CharSemantics? semantics = m.Groups[4].Value switch { "BYTE" => CharSemantics.Bytes, "CHAR" => CharSemantics.Characters, _ => null };
        return (m.Groups[1].Value.TrimEnd(), (Num(m.Groups[2]), Num(m.Groups[3]), semantics));
    }

    /// <summary>사전 표기 → 칸에 보일 글(일반 식별자는 그대로, 아니면 큰따옴표).</summary>
    public static string Show(string? name) => string.IsNullOrEmpty(name) ? string.Empty : SqlIdentifier.Quote(name);

    /// <summary>쉼표로 나눈다(큰따옴표 안의 쉼표는 나누지 않음). 빈 조각은 뺀다.</summary>
    public static IReadOnlyList<string> SplitList(string? text)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return parts;
        }

        var start = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                quoted = !quoted;
            }
            else if (text[i] == ',' && !quoted)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return [.. parts.Select(p => p.Trim()).Where(p => p.Length > 0)];
    }

    /// <summary>
    /// 열 목록 글(<c>A, B</c>) → 열 줄 키. 이름은 지우지 않은 열에서 찾는다. 못 찾은 이름이 있으면 null 과 그 이름.
    /// </summary>
    public static (IReadOnlyList<string>? Keys, string? Unknown) ColumnKeys(TableDraft draft, string? text)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var keys = new List<string>();
        foreach (var part in SplitList(text))
        {
            var name = Parse(part);
            var column = draft.Columns.FirstOrDefault(c => !c.IsDropped && string.Equals(c.Name, name, StringComparison.Ordinal));
            if (column is null)
            {
                return (null, part);
            }

            keys.Add(column.RowKey);
        }

        return (keys, null);
    }

    /// <summary>열 줄 키 → 목록 글(<c>A, B</c>). 없는 키는 <c>?</c>.</summary>
    public static string ColumnsText(TableDraft draft, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return string.Join(", ", keys.Select(k => draft.Column(k) is { } c ? Show(c.Name) : "?"));
    }

    /// <summary>인덱스 열 → 글(<c>A, B DESC</c>, 식 자리는 식 그대로).</summary>
    public static string IndexColumnsText(TableDraft draft, IEnumerable<IndexColumnDraft> columns)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return string.Join(", ", columns.Select(c =>
            (c.Expression ?? (c.ColumnKey is { } k && draft.Column(k) is { } col ? Show(col.Name) : "?")) + (c.Descending ? " DESC" : string.Empty)));
    }

    /// <summary>인덱스 열 글 → 인덱스 열(일반 열만, <c>ASC</c>/<c>DESC</c> 꼬리). 못 찾은 이름이 있으면 null 과 그 조각.</summary>
    public static (IReadOnlyList<IndexColumnDraft>? Columns, string? Unknown) IndexColumns(TableDraft draft, string? text)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var result = new List<IndexColumnDraft>();
        foreach (var part in SplitList(text))
        {
            var p = part;
            var desc = false;
            if (p.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase))
            {
                desc = true;
                p = p[..^5].TrimEnd();
            }
            else if (p.EndsWith(" ASC", StringComparison.OrdinalIgnoreCase))
            {
                p = p[..^4].TrimEnd();
            }

            var (keys, unknown) = ColumnKeys(draft, p);
            if (keys is not { Count: 1 })
            {
                return (null, unknown ?? part);
            }

            result.Add(new IndexColumnDraft(keys[0], desc));
        }

        return (result, null);
    }

    /// <summary>FK 참조 → 글(<c>SAMPLE.ORDERS (ORDER_ID)</c>).</summary>
    public static string ReferenceText(ConstraintDraft c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.ReferencedTable is not { } t)
        {
            return string.Empty;
        }

        var cols = c.ReferencedColumns.Count == 0 ? string.Empty : " (" + string.Join(", ", c.ReferencedColumns.Select(Show)) + ")";
        return Show(t.Owner) + "." + Show(t.Name) + cols;
    }

    /// <summary>
    /// 참조 글(<c>[소유자.]테이블 (열, …)</c>) → 참조 테이블·열. 소유자를 안 쓰면 <paramref name="defaultOwner"/>. 꼴이 틀리면 null.
    /// </summary>
    public static (DbObjectRef Table, IReadOnlyList<string> Columns)? ParseReference(string? text, string defaultOwner)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return null;
        }

        var open = t.IndexOf('(', StringComparison.Ordinal);
        var head = open < 0 ? t : t[..open].Trim();
        IReadOnlyList<string> cols = [];
        if (open >= 0)
        {
            var close = t.LastIndexOf(')');
            if (close < open)
            {
                return null;
            }

            cols = [.. SplitList(t[(open + 1)..close]).Select(Parse)];
        }

        var dot = DotOutsideQuotes(head);
        var owner = dot < 0 ? defaultOwner : Parse(head[..dot]);
        var name = Parse(dot < 0 ? head : head[(dot + 1)..]);
        return name.Length == 0 || owner.Length == 0 ? null : (new DbObjectRef(owner, name, "TABLE"), cols);
    }

    /// <summary>정수 칸 글 → 값(빈 칸 = null). 숫자가 아니면 false.</summary>
    public static bool TryParseInt(string? text, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (text.Trim() == "*")
        {
            return true; // NUMBER(*,0) 의 정밀도 자리
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0)
        {
            value = n;
            return true;
        }

        return false;
    }

    private static int DotOutsideQuotes(string s)
    {
        var quoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"')
            {
                quoted = !quoted;
            }
            else if (s[i] == '.' && !quoted)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>편집기 화면 문구(한국어 / 영어). 화면이 글을 고를 때 한 곳에서.</summary>
internal static class TableEditText
{
    public static string T(string ko, string en) => LiteralTranslator.IsEnglish ? en : ko;
}
