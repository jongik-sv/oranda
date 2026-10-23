using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

/// <summary>엔진이 쓰는 토큰 종류(렉서 <c>TokenKind</c> 를 줄인 것).</summary>
internal enum ConvertTokKind
{
    Other,
    Word,
    Quoted,
    Bind,
    Subst,
    Open,
    Close,
    Dot,
    Comment,
}

/// <summary>렉서 토큰을 옮긴 것. <see cref="Start"/> 는 렉싱한 글 안 위치.</summary>
internal readonly record struct ConvertTok(int Start, int Length, ConvertTokKind Kind)
{
    public int End => Start + Length;

    /// <summary>토큰 글이 <paramref name="word"/> 와 같은가(대소문자 무시).</summary>
    public static bool Is(string sql, ConvertTok t, string word) =>
        t.Kind == ConvertTokKind.Word && sql.AsSpan(t.Start, t.Length).Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>렉서가 한 토큰으로 낸 <c>..</c>(마침표 둘)인가. <c>&amp;schema..T</c> 에서 둘째 마침표가 SQL*Plus 의 구분 마침표가 아니라 스키마 점이다.</summary>
    public static bool IsDoubleDot(string sql, ConvertTok t) =>
        t.Kind == ConvertTokKind.Other && t.Length == 2 && sql[t.Start] == '.' && sql[t.Start + 1] == '.';

    /// <summary>바인드·치환 토큰은 접두(<c>:</c>·<c>&amp;</c>·<c>&amp;&amp;</c>)를 뺀 이름, 따옴표 이름은 따옴표를 뺀 글, 그 밖은 토큰 글.</summary>
    public static string Name(string sql, ConvertTok t) => t.Kind switch
    {
        ConvertTokKind.Bind => sql.Substring(t.Start + 1, t.Length - 1),
        ConvertTokKind.Subst => sql.Substring(t.Start + (sql[t.Start + 1] == '&' ? 2 : 1), t.Length - (sql[t.Start + 1] == '&' ? 2 : 1)),
        ConvertTokKind.Quoted => sql.Substring(t.Start, t.Length).Trim('"'),
        _ => sql.Substring(t.Start, t.Length),
    };
}

/// <summary>
/// 바인드(<c>:x</c>)·치환(<c>&amp;x</c>) 토큰을 <c>#{x}</c>·<c>${x}</c> 로 바꾸는 편집 목록과 알림을 만든다.
/// 이름은 한 문장 안에서 대소문자만 다른 것을 처음 나온 철자로 통일한다.
/// </summary>
internal sealed class ConvertRewriter(
    string sql,
    List<ConvertTok> toks,
    IReadOnlyList<string>? originalNames,
    IReadOnlyList<ConvertBind> binds,
    ConvertStatementKind kind,
    ConvertOptions options)
{
    /// <summary>글 <c>[Start, End)</c> 를 <see cref="Text"/> 로 바꾼다.</summary>
    public readonly record struct Edit(int Start, int End, string Text);

    public List<Edit> Edits { get; } = [];

    public List<ConvertNote> Notes { get; } = [];

    /// <summary>통일한 바인드 이름(위치 바인드는 <c>p1</c>), 나온 순서.</summary>
    public List<string> BindNames { get; } = [];

    /// <summary>통일한 치환 변수 이름, 나온 순서.</summary>
    public List<string> SubstNames { get; } = [];

    /// <summary>토큰 번호 → 통일한 바인드 이름.</summary>
    public Dictionary<int, string> CanonByTok { get; } = [];

    /// <summary>통일한 이름 → 탭 바인드를 찾을 때 쓸 처음 철자.</summary>
    public Dictionary<string, string> TypedByCanon { get; } = new(StringComparer.Ordinal);

    public void Run()
    {
        var bindMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var substMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var refCursorSeen = new HashSet<string>(StringComparer.Ordinal);
        var positionalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 위치 바인드의 새 이름(:1 → p1)이 같은 문장의 이름 있는 바인드와 겹치지 않게 미리 모은다.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tok in toks)
        {
            if (tok.Kind != ConvertTokKind.Bind) continue;
            string bare = ConvertTok.Name(sql, tok);
            if (!char.IsAsciiDigit(bare[0])) reserved.Add(bare);
        }

        int ordinal = 0;

        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind is not (ConvertTokKind.Bind or ConvertTokKind.Subst)) continue;

            string typed = ConvertTok.Name(sql, t);
            if (originalNames is not null && ordinal < originalNames.Count
                && string.Equals(originalNames[ordinal], typed, StringComparison.OrdinalIgnoreCase))
                typed = originalNames[ordinal];
            ordinal++;

            if (t.Kind == ConvertTokKind.Subst)
            {
                if (!substMap.TryGetValue(typed, out string? subst))
                {
                    subst = typed;
                    substMap[typed] = typed;
                    SubstNames.Add(typed);
                    Notes.Add(new ConvertNote(ConvertNoteKind.Substitution, typed));
                }

                // 바로 뒤 SQL*Plus 마침표(&x. 의 .)는 변수 이름의 끝 표지라 버린다.
                // &schema..T 처럼 마침표가 둘이면 첫 마침표만 버리고 둘째(스키마 점)는 남긴다.
                int end = t.End;
                if (i + 1 < toks.Count && toks[i + 1].Start == t.End)
                {
                    if (toks[i + 1].Kind == ConvertTokKind.Dot)
                    {
                        end = toks[i + 1].End;
                        i++;
                    }
                    else if (ConvertTok.IsDoubleDot(sql, toks[i + 1]))
                    {
                        end = t.End + 1;
                        i++;
                    }
                }

                Edits.Add(new Edit(t.Start, end, "${" + subst + "}"));
                continue;
            }

            bool positional = char.IsAsciiDigit(typed[0]);
            string name = typed;
            if (positional && !positionalMap.TryGetValue(typed, out name!))
            {
                name = FreeName("p" + typed, reserved);
                positionalMap[typed] = name;
                reserved.Add(name);
            }

            if (!bindMap.TryGetValue(name, out string? canon))
            {
                canon = name;
                bindMap[name] = name;
                BindNames.Add(name);
                TypedByCanon[name] = typed;
                if (positional) Notes.Add(new ConvertNote(ConvertNoteKind.PositionalBind, name));
            }

            CanonByTok[i] = canon;
            var info = Find(TypedByCanon[canon]) ?? Find(canon);
            if (info is { Type: BindDataType.RefCursor, Direction: not BindDirection.In } && refCursorSeen.Add(canon))
                Notes.Add(new ConvertNote(ConvertNoteKind.RefCursorOut, canon));
            Edits.Add(new Edit(t.Start, t.End, BindText(canon, info, IsAssignTarget(i))));
        }

        EscapeTemplateTokens();
    }

    /// <summary><paramref name="baseName"/>, 겹치면 <c>baseName_2</c>, <c>baseName_3</c>… 중 <paramref name="reserved"/> 에 없는 첫 것.</summary>
    static string FreeName(string baseName, HashSet<string> reserved)
    {
        string name = baseName;
        for (int n = 2; reserved.Contains(name); n++) name = baseName + "_" + n.ToString(CultureInfo.InvariantCulture);
        return name;
    }

    /// <summary>토큰 <paramref name="index"/> 의 바인드가 <c>:x :=</c> 처럼 대입 대상인가(주석은 건너뜀).</summary>
    bool IsAssignTarget(int index)
    {
        int j = index + 1;
        while (j < toks.Count && toks[j].Kind == ConvertTokKind.Comment) j++;
        return j < toks.Count && toks[j].Kind == ConvertTokKind.Other && sql.AsSpan(toks[j].Start, toks[j].Length).SequenceEqual(":=");
    }

    /// <summary>
    /// 바인드·치환이 아닌 글(문자열·주석·따옴표 이름 등) 안의 <c>#{</c>·<c>${</c> 는 MyBatis 가 해석하므로 <c>\#{</c>·<c>\${</c> 로 이스케이프한다
    /// (GenericTokenParser 가 앞의 백슬래시를 지우고 글자로 남긴다).
    /// </summary>
    void EscapeTemplateTokens()
    {
        List<Edit>? escapes = null;
        int e = 0;
        for (int i = 0; i + 1 < sql.Length; i++)
        {
            if (sql[i + 1] != '{' || sql[i] is not ('#' or '$')) continue;
            while (e < Edits.Count && Edits[e].End <= i) e++;
            if (e < Edits.Count && Edits[e].Start < i + 2) continue;
            (escapes ??= []).Add(new Edit(i, i + 2, sql[i] == '#' ? "\\#{" : "\\${"));
            i++;
        }

        if (escapes is null) return;
        Edits.AddRange(escapes);
        Edits.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    ConvertBind? Find(string name)
    {
        foreach (var b in binds)
        {
            if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) return b;
        }

        return null;
    }

    /// <summary>
    /// <c>#{x}</c>, CALLABLE 의 OUT·IN OUT 은 <c>#{x,mode=OUT,jdbcType=…}</c>(대응 jdbcType 이 없으면 VARCHAR).
    /// 탭 바인드 줄이 없어도 대입 대상(<paramref name="assignTarget"/>)이면 <c>mode=OUT,jdbcType=VARCHAR</c> 를 붙인다.
    /// </summary>
    string BindText(string canon, ConvertBind? info, bool assignTarget)
    {
        string? mode = null;
        if (kind == ConvertStatementKind.Callable)
        {
            if (info is not null)
            {
                mode = info.Direction switch
                {
                    BindDirection.Out or BindDirection.ReturnValue => "OUT",
                    BindDirection.InOut => "INOUT",
                    _ => null,
                };
            }
            else if (assignTarget)
            {
                mode = "OUT";
            }
        }

        string? jdbc = null;
        if (info is not null)
        {
            if (info.Type == BindDataType.RefCursor) jdbc = mode is null ? null : "CURSOR";
            else if (mode is not null || options.AddJdbcType
                     || info.Type is BindDataType.Date or BindDataType.Timestamp or BindDataType.TimestampTz or BindDataType.FixedChar)
                jdbc = JdbcName(info.Type) ?? (mode is null ? null : "VARCHAR");
        }
        else if (mode is not null)
        {
            jdbc = "VARCHAR";
        }

        return "#{" + canon + (mode is null ? "" : ",mode=" + mode) + (jdbc is null ? "" : ",jdbcType=" + jdbc) + "}";
    }

    /// <summary>탭 바인드 형 → MyBatis jdbcType 이름(prep.md 대응표). 대응이 없으면 null.</summary>
    internal static string? JdbcName(BindDataType type) => type switch
    {
        BindDataType.Varchar2 => "VARCHAR",
        BindDataType.NVarchar2 => "NVARCHAR",
        BindDataType.FixedChar => "CHAR",
        BindDataType.Number => "NUMERIC",
        BindDataType.BinaryDouble => "DOUBLE",
        BindDataType.Date => "DATE",
        BindDataType.Timestamp => "TIMESTAMP",
        BindDataType.TimestampTz => "TIMESTAMP_WITH_TIMEZONE",
        BindDataType.Clob => "CLOB",
        BindDataType.Blob => "BLOB",
        BindDataType.Raw => "BINARY",
        BindDataType.Boolean => "BOOLEAN",
        _ => null,
    };

    /// <summary>문장이 참조하는 바인드 전부의 첫 시나리오 값(키 = 통일한 철자). 치환 이름은 Null.</summary>
    public Dictionary<string, MyBatisScenarioValue> ScenarioValues()
    {
        var values = new Dictionary<string, MyBatisScenarioValue>(StringComparer.Ordinal);
        foreach (string canon in BindNames)
        {
            var info = Find(TypedByCanon[canon]) ?? Find(canon);
            values[canon] = info is null || string.IsNullOrEmpty(info.Text) ? MyBatisScenarioValue.Null : ValueOf(info);
        }

        foreach (string name in SubstNames) values.TryAdd(name, MyBatisScenarioValue.Null);
        return values;
    }

    static MyBatisScenarioValue ValueOf(ConvertBind bind)
    {
        string text = bind.Text;
        switch (bind.Type)
        {
            case BindDataType.Number or BindDataType.BinaryDouble:
                string trimmed = text.Trim();
                bool number = decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    || (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d));
                return number ? new MyBatisScenarioValue(MyBatisScenarioValueKind.Number, trimmed) : new MyBatisScenarioValue(MyBatisScenarioValueKind.Text, text);
            case BindDataType.Boolean:
                return text.Trim().ToUpperInvariant() switch
                {
                    "TRUE" or "T" or "Y" or "YES" or "1" => new MyBatisScenarioValue(MyBatisScenarioValueKind.Boolean, "true"),
                    "FALSE" or "F" or "N" or "NO" or "0" => new MyBatisScenarioValue(MyBatisScenarioValueKind.Boolean, "false"),
                    _ => new MyBatisScenarioValue(MyBatisScenarioValueKind.Text, text),
                };
            default:
                return new MyBatisScenarioValue(MyBatisScenarioValueKind.Text, text);
        }
    }
}
