using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.Core.ChangeBundle;

/// <summary>묶음 문장 하나의 검사 계획(<see cref="ChangeBundleCheckPlanner"/>). <see cref="Index"/> 는 실행 순서(0부터), <see cref="Table"/> 은 검사할 테이블.</summary>
public sealed record ChangeBundleStatementPlan(int Index, DbObjectRef Table, TableEditCheckPlan Plan);

/// <summary>
/// 반영 묶음 「실행…」 전 검사(테이블 편집기 설계 9.2·10장, T10). 묶음 문장에는 편집 모델이 없으므로 문장을 해석할 수 있는 것만 검사 계획으로 바꾼다
/// (나머지는 검사 없이 보낸다, 설계 9.2 [구현: 해석 범위]):
/// <list type="bullet">
/// <item><c>ALTER TABLE t MODIFY (c VARCHAR2(n) …)</c> 문자·RAW 길이 → 넘치는 행 ✖, <c>… NOT NULL</c> → NULL 행 ✖</item>
/// <item><c>ALTER TABLE t ADD [CONSTRAINT n] PRIMARY KEY|UNIQUE (…)</c> → 중복 ✖, <c>FOREIGN KEY (…) REFERENCES r (…)</c> → 짝 없는 행 ✖, <c>CHECK (…)</c> → 어기는 행 ✖
/// (<c>NOVALIDATE</c>·<c>DISABLE</c> 면 데이터 검사를 하지 않는다. 참조 열을 쓰지 않은 FK 는 건너뛴다)</item>
/// <item><c>ALTER TABLE t DROP COLUMN|CONSTRAINT|PRIMARY KEY|UNIQUE</c>·<c>SET UNUSED</c>·<c>DROP TABLE</c>·<c>DROP INDEX</c> → 되돌릴 수 없음 ⚠(열·테이블은 의존 객체도)</item>
/// <item>운영 접속에서 MODIFY·제약 추가가 있는 테이블 → 큰 테이블 ⚠(<see cref="CheckCode.LongLock"/>, 테이블마다 한 번)</item>
/// </list>
/// 문자열·따옴표 이름·주석은 토큰으로 읽어 그 안의 글자에 속지 않는다. 판정 줄의 <see cref="CheckFinding.Subject"/> 는 「#순번 이름」이다.
/// </summary>
public static class ChangeBundleCheckPlanner
{
    private static readonly HashSet<string> LengthTypes = new(StringComparer.Ordinal) { "VARCHAR2", "VARCHAR", "CHAR", "NVARCHAR2", "NCHAR", "RAW" };
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal) { "ADD", "MODIFY", "DROP", "SET", "RENAME" };

    /// <summary>지금 데이터를 세는 검사(앞 문장이 바꾼 테이블에서는 하지 않는다).</summary>
    private static readonly HashSet<TableEditCheckKind> DataKinds =
    [
        TableEditCheckKind.Overflow, TableEditCheckKind.Nulls, TableEditCheckKind.DuplicateKeys, TableEditCheckKind.Orphans, TableEditCheckKind.CheckViolations,
    ];

    /// <param name="statements">실행할 문장 글(순서대로)</param>
    /// <param name="defaultOwner">소유자를 안 쓴 이름의 소유자(실행 접속의 현재 스키마, 이름 규칙대로)</param>
    /// <param name="options">접속 환경(운영 큰 테이블 ⚠)</param>
    /// <param name="purgesOnDrop">DROP 이 휴지통을 거치지 않는 DBMS(⚠ 문구)</param>
    public static IReadOnlyList<ChangeBundleStatementPlan> Plan(IReadOnlyList<string> statements, string defaultOwner, TableEditCheckOptions options,
        bool purgesOnDrop = false)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(options);
        var plans = new List<ChangeBundleStatementPlan>();
        var locked = new HashSet<(string, string)>();
        var touched = new HashSet<(string, string)>(); // 앞 문장이 만들거나 지우거나 열·데이터를 바꾼 테이블
        for (var i = 0; i < statements.Count; i++)
        {
            List<Token> tokens;
            try
            {
                tokens = Tokenize(statements[i] ?? string.Empty);
            }
            catch (FormatException)
            {
                continue; // 끝나지 않은 문자열·주석: 해석하지 않고 검사 없이 보낸다
            }

            var one = PlanOne(i, statements[i]!, tokens, defaultOwner, purgesOnDrop);
            var touchedBefore = one is not null && touched.Contains((one.Table.Owner, one.Table.Name));
            foreach (var name in Touches(tokens, defaultOwner))
            {
                touched.Add(name);
            }

            if (one is null)
            {
                continue;
            }

            var checks = one.Checks.ToList();
            if (touchedBefore && checks.RemoveAll(c => DataKinds.Contains(c.Kind)) > 0)
            {
                // 앞 문장이 이 테이블을 지우고 다시 만들거나 열을 더했을 수 있다: 지금 서버 상태로 세면 거짓 ✖ 가 난다(⚠ 「확인 못 함」).
                one.Findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.NotChecked, RowKey(i))
                {
                    Subject = Subject(i, one.Table.Name), Detail = "앞 문장이 이 테이블을 바꿔 실행 전에는 확인할 수 없다",
                });
            }

            if (one.Locks && options.Environment == ConnectionEnvironment.Production && locked.Add((one.Table.Owner, one.Table.Name)))
            {
                checks.Add(new TableEditCheck(TableEditCheckKind.LargeTable, CheckCode.LongLock, RowKey(i), Subject(i, one.Table.Name)));
            }

            if (one.Findings.Count > 0 || checks.Count > 0)
            {
                plans.Add(new ChangeBundleStatementPlan(i, one.Table, new TableEditCheckPlan(one.Findings, checks)));
            }
        }

        return plans;
    }

    /// <summary>
    /// 이 문장이 구조·데이터를 바꾸는 테이블(뒤 문장의 데이터 검사를 지금 서버 상태로 하면 거짓 ✖ 가 나는 것): CREATE·DROP·TRUNCATE TABLE, RENAME a TO b(둘 다),
    /// ALTER TABLE t ADD 열·DROP·SET UNUSED·RENAME·MOVE, INSERT·UPDATE·DELETE·MERGE 대상. 제약만 더하는 ALTER 는 데이터를 바꾸지 않아 넣지 않는다.
    /// </summary>
    private static IEnumerable<(string Owner, string Name)> Touches(List<Token> t, string defaultOwner)
    {
        if (t.Count < 2)
        {
            yield break;
        }

        var p = 0;
        string? head = t[0].Kind == TokenKind.Word ? t[0].Upper : null;
        switch (head)
        {
            case "CREATE":
                var c = 1;
                while (c < t.Count && t[c].Kind == TokenKind.Word && t[c].Upper is "OR" or "REPLACE" or "GLOBAL" or "PRIVATE" or "TEMPORARY" or "SHARDED"
                           or "DUPLICATED" or "IMMUTABLE" or "BLOCKCHAIN")
                {
                    c++;
                }

                if (c < t.Count && t[c].Is("TABLE"))
                {
                    p = SkipIfNotExists(t, c + 1);
                    if (ReadName(t, ref p, defaultOwner) is { } created)
                    {
                        yield return created;
                    }
                }

                break;
            case "DROP" or "TRUNCATE" when t[1].Is("TABLE"):
                p = SkipIfExists(t, 2);
                if (ReadName(t, ref p, defaultOwner) is { } dropped)
                {
                    yield return dropped;
                }

                break;
            case "RENAME":
                p = 1;
                if (ReadName(t, ref p, defaultOwner) is { } from)
                {
                    yield return from;
                    if (p < t.Count && t[p].Is("TO"))
                    {
                        p++;
                        if (ReadName(t, ref p, defaultOwner) is { } to)
                        {
                            yield return to;
                        }
                    }
                }

                break;
            case "ALTER" when t[1].Is("TABLE"):
                p = SkipIfExists(t, 2);
                if (ReadName(t, ref p, defaultOwner) is not { } altered)
                {
                    break;
                }

                for (var i = p; i + 1 < t.Count; i++)
                {
                    var changes = t[i].Is("DROP") && !t[i + 1].Is("CONSTRAINT") && !t[i + 1].Is("PRIMARY") && !t[i + 1].Is("UNIQUE")
                        || t[i].Is("RENAME") || t[i].Is("MOVE") || t[i].Is("SET") && t[i + 1].Is("UNUSED")
                        || t[i].Is("ADD") && !t[i + 1].Is("CONSTRAINT") && !t[i + 1].Is("PRIMARY") && !t[i + 1].Is("UNIQUE") && !t[i + 1].Is("FOREIGN")
                           && !t[i + 1].Is("CHECK") && !(t[i + 1].Kind == TokenKind.Open && i + 2 < t.Count && t[i + 2].Is("CONSTRAINT"));
                    if (!changes)
                    {
                        continue;
                    }

                    yield return altered;
                    if (t[i].Is("RENAME") && t[i + 1].Is("TO") && i + 2 < t.Count)
                    {
                        var q = i + 2;
                        if (ReadName(t, ref q, altered.Owner) is { } renamed)
                        {
                            yield return renamed;
                        }
                    }

                    break;
                }

                break;
            case "INSERT" or "MERGE":
                p = 1;
                while (p < t.Count && !t[p].Is("INTO"))
                {
                    p++;
                }

                p++;
                if (ReadName(t, ref p, defaultOwner) is { } inserted)
                {
                    yield return inserted;
                }

                break;
            case "UPDATE" or "DELETE":
                p = 1;
                if (p < t.Count && t[p].Is("FROM"))
                {
                    p++;
                }

                if (ReadName(t, ref p, defaultOwner) is { } updated)
                {
                    yield return updated;
                }

                break;
        }
    }

    private static int SkipIfNotExists(List<Token> t, int p) =>
        p + 2 < t.Count && t[p].Is("IF") && t[p + 1].Is("NOT") && t[p + 2].Is("EXISTS") ? p + 3 : p;

    /// <summary><c>IF EXISTS</c> 를 건너뛴 자리(23ai·Tibero 꼴).</summary>
    private static int SkipIfExists(List<Token> t, int p) => p + 1 < t.Count && t[p].Is("IF") && t[p + 1].Is("EXISTS") ? p + 2 : p;

    internal static string RowKey(int index) => "#" + (index + 1).ToString(CultureInfo.InvariantCulture);

    private static string Subject(int index, string name) => RowKey(index) + " " + name;

    private sealed record One(DbObjectRef Table, List<CheckFinding> Findings, List<TableEditCheck> Checks, bool Locks);

    private static One? PlanOne(int index, string text, List<Token> t, string defaultOwner, bool purge)
    {
        if (t.Count < 3)
        {
            return null;
        }

        if (t[0].Is("DROP") && (t[1].Is("TABLE") || t[1].Is("INDEX")))
        {
            var p = SkipIfExists(t, 2);
            if (ReadName(t, ref p, defaultOwner) is not { } name)
            {
                return null;
            }

            var table = t[1].Is("TABLE");
            var target = new DbObjectRef(name.Owner, name.Name, table ? "TABLE" : "INDEX");
            var findings = new List<CheckFinding> { Irreversible(index, name.Name, purge) };
            var checks = new List<TableEditCheck>();
            if (table)
            {
                checks.Add(new TableEditCheck(TableEditCheckKind.Dependents, CheckCode.Irreversible, RowKey(index), Subject(index, name.Name)));
            }

            return new One(target, findings, checks, false);
        }

        if (!(t[0].Is("ALTER") && t[1].Is("TABLE")))
        {
            return null;
        }

        var pos = SkipIfExists(t, 2);
        if (ReadName(t, ref pos, defaultOwner) is not { } tableName)
        {
            return null;
        }

        var tableRef = new DbObjectRef(tableName.Owner, tableName.Name, "TABLE");
        var result = new One(tableRef, [], [], false);
        var locks = false;
        // 깊이 0 의 동작 낱말(ADD·MODIFY·DROP·SET·RENAME)로 동작을 나눈다.
        var starts = new List<int>();
        var depth = 0;
        for (var i = pos; i < t.Count; i++)
        {
            if (t[i].Kind == TokenKind.Open)
            {
                depth++;
            }
            else if (t[i].Kind == TokenKind.Close)
            {
                depth--;
            }
            else if (depth == 0 && t[i].Kind == TokenKind.Word && Actions.Contains(t[i].Upper))
            {
                starts.Add(i);
            }
        }

        for (var a = 0; a < starts.Count; a++)
        {
            var from = starts[a];
            var to = a + 1 < starts.Count ? starts[a + 1] : t.Count;
            var action = t.GetRange(from, to - from);
            switch (action[0].Upper)
            {
                case "MODIFY":
                    locks |= Modify(index, action, result);
                    break;
                case "ADD":
                    locks |= Add(index, text, action, result, defaultOwner);
                    break;
                case "DROP":
                    Drop(index, action, result, purge);
                    break;
                case "SET" when action.Count > 1 && action[1].Is("UNUSED"):
                    Drop(index, action.GetRange(1, action.Count - 1), result, purge, unused: true);
                    break;
            }
        }

        return result with { Locks = locks };
    }

    /// <summary>MODIFY (c 형식(n) … NOT NULL, …) 또는 MODIFY c …. 해석한 열이 있으면 true(잠그는 변경).</summary>
    private static bool Modify(int index, List<Token> action, One result)
    {
        if (action.Count < 2 || action[1].Kind == TokenKind.Word && action[1].Upper is "CONSTRAINT" or "PRIMARY" or "UNIQUE" or "PARTITION"
                or "SUBPARTITION" or "LOB" or "DEFAULT" or "NESTED" or "VARRAY" or "OPAQUE")
        {
            return false;
        }

        var items = action[1].Kind == TokenKind.Open && Closing(action, 1) is var close and > 1
            ? SplitTop(action.GetRange(2, close - 2))
            : [action.GetRange(1, action.Count - 1)];
        var any = false;
        foreach (var item in items)
        {
            if (item.Count == 0 || item[0].Kind is not (TokenKind.Word or TokenKind.QuotedName))
            {
                continue;
            }

            var column = item[0].Name;
            var subject = Subject(index, column);
            if (item.Count >= 5 && item[1].Kind == TokenKind.Word && LengthTypes.Contains(item[1].Upper) && item[2].Kind == TokenKind.Open
                && item[3].Kind == TokenKind.Number && int.TryParse(item[3].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                var type = item[1].Upper == "VARCHAR" ? "VARCHAR2" : item[1].Upper;
                CharSemantics? semantics = type.StartsWith('N') ? CharSemantics.Characters
                    : item[4].Is("CHAR") ? CharSemantics.Characters
                    : item[4].Is("BYTE") ? CharSemantics.Bytes
                    : null; // 기준을 안 쓰면 세션 기본(BYTE·CHAR 모름) — 글자로 센다: 거짓 ✖ 는 없고, 바이트로만 넘치는 행은 서버가 실행에서 거부한다
                var target = new ColumnDraft(RowKey(index), column) { DataType = type, Length = length, CharSemantics = semantics };
                result.Checks.Add(new TableEditCheck(TableEditCheckKind.Overflow, CheckCode.Overflow, RowKey(index), subject) { Column = column, Target = target });
                any = true;
            }

            for (var i = 1; i + 1 < item.Count; i++)
            {
                if (!item[i].Is("NOT") || !item[i + 1].Is("NULL"))
                {
                    continue;
                }

                if (!item.Any(x => x.Is("NOVALIDATE") || x.Is("DISABLE")))
                {
                    result.Checks.Add(new TableEditCheck(TableEditCheckKind.Nulls, CheckCode.NullRows, RowKey(index), subject) { Column = column });
                }

                any = true;
                break;
            }
        }

        return any;
    }

    /// <summary>ADD [CONSTRAINT n] PRIMARY KEY|UNIQUE|FOREIGN KEY|CHECK. 괄호로 싼 ADD (…) 안의 제약도 본다. 제약을 더하면 true.</summary>
    private static bool Add(int index, string text, List<Token> action, One result, string defaultOwner)
    {
        var items = action.Count > 1 && action[1].Kind == TokenKind.Open && Closing(action, 1) is var close and > 1
            ? SplitTop(action.GetRange(2, close - 2))
            : [action.GetRange(1, action.Count - 1)];
        var any = false;
        foreach (var item in items)
        {
            var p = 0;
            string? name = null;
            if (p < item.Count && item[p].Is("CONSTRAINT") && p + 1 < item.Count)
            {
                name = item[p + 1].Name;
                p += 2;
            }

            if (p >= item.Count || item[p].Kind != TokenKind.Word)
            {
                continue;
            }

            var noData = item.Any(x => x.Is("NOVALIDATE") || x.Is("DISABLE"));
            var head = item[p].Upper;
            if (head is "PRIMARY" or "UNIQUE")
            {
                var q = p + (head == "PRIMARY" ? 2 : 1);
                if (ReadList(item, q) is { Count: > 0 } cols)
                {
                    any = true;
                    if (!noData)
                    {
                        result.Checks.Add(new TableEditCheck(TableEditCheckKind.DuplicateKeys, CheckCode.DuplicateKeys, RowKey(index),
                            Subject(index, name ?? string.Join(", ", cols))) { Columns = cols });
                        if (head == "PRIMARY")
                        {
                            // PK 열에 NULL 이 있으면 ORA-01449 — 중복 검사는 모두 NULL 인 키를 겹침으로 보지 않는다(테이블 편집기 검사와 같다).
                            foreach (var c in cols)
                            {
                                result.Checks.Add(new TableEditCheck(TableEditCheckKind.Nulls, CheckCode.NullRows, RowKey(index), Subject(index, c)) { Column = c });
                            }
                        }
                    }
                }
            }
            else if (head == "FOREIGN")
            {
                var q = p + 2;
                if (ReadList(item, q) is not { Count: > 0 } cols)
                {
                    continue;
                }

                any = true;
                var r = Closing(item, q) + 1;
                if (r < item.Count && item[r].Is("REFERENCES"))
                {
                    r++;
                    if (ReadName(item, ref r, defaultOwner) is { } parent && ReadList(item, r) is { Count: > 0 } refCols && refCols.Count == cols.Count && !noData)
                    {
                        result.Checks.Add(new TableEditCheck(TableEditCheckKind.Orphans, CheckCode.OrphanRows, RowKey(index),
                            Subject(index, name ?? string.Join(", ", cols)))
                        {
                            Columns = cols,
                            ReferencedTable = new DbObjectRef(parent.Owner, parent.Name, "TABLE"),
                            ReferencedColumns = refCols,
                        });
                    }
                }
            }
            else if (head == "CHECK" && p + 1 < item.Count && item[p + 1].Kind == TokenKind.Open)
            {
                var end = Closing(item, p + 1);
                if (end > p + 2)
                {
                    any = true;
                    if (!noData)
                    {
                        var condition = text[item[p + 2].Start..item[end - 1].End].Trim();
                        result.Checks.Add(new TableEditCheck(TableEditCheckKind.CheckViolations, CheckCode.CheckViolations, RowKey(index),
                            Subject(index, name ?? "CHECK")) { Condition = condition });
                    }
                }
            }
        }

        return any;
    }

    /// <summary>DROP COLUMN c · DROP (c, …) · DROP CONSTRAINT n · DROP PRIMARY KEY · DROP UNIQUE (…) · SET UNUSED …: 되돌릴 수 없음 ⚠.</summary>
    private static void Drop(int index, List<Token> action, One result, bool purge, bool unused = false)
    {
        if (action.Count < 2)
        {
            return;
        }

        var first = action[1];
        if (first.Is("COLUMN") && action.Count > 2)
        {
            DroppedColumns(index, [action[2].Name], result, purge, unused);
        }
        else if (first.Kind == TokenKind.Open && ReadList(action, 1) is { Count: > 0 } cols)
        {
            DroppedColumns(index, cols, result, purge, unused);
        }
        else if (first.Is("CONSTRAINT") && action.Count > 2)
        {
            result.Findings.Add(Irreversible(index, action[2].Name, purge));
        }
        else if (first.Is("PRIMARY"))
        {
            result.Findings.Add(Irreversible(index, "PRIMARY KEY", purge));
        }
        else if (first.Is("UNIQUE") && ReadList(action, 2) is { Count: > 0 } ukCols)
        {
            result.Findings.Add(Irreversible(index, "UNIQUE (" + string.Join(", ", ukCols) + ")", purge));
        }
    }

    private static void DroppedColumns(int index, IReadOnlyList<string> columns, One result, bool purge, bool unused)
    {
        foreach (var c in columns)
        {
            result.Findings.Add(Irreversible(index, c, purge && !unused));
        }

        if (!result.Checks.Any(c => c.Kind == TableEditCheckKind.Dependents))
        {
            result.Checks.Add(new TableEditCheck(TableEditCheckKind.Dependents, CheckCode.Irreversible, RowKey(index), Subject(index, result.Table.Name)));
        }
    }

    private static CheckFinding Irreversible(int index, string name, bool purge) =>
        new(CheckSeverity.Warning, CheckCode.Irreversible, RowKey(index)) { Subject = Subject(index, name), Detail = purge ? "purge" : null };

    /// <summary>[소유자.]이름 (이름 규칙대로). 이름이 아니면 null.</summary>
    private static (string Owner, string Name)? ReadName(List<Token> t, ref int p, string defaultOwner)
    {
        if (p >= t.Count || t[p].Kind is not (TokenKind.Word or TokenKind.QuotedName))
        {
            return null;
        }

        var first = t[p].Name;
        if (p + 2 < t.Count && t[p + 1].Kind == TokenKind.Dot && t[p + 2].Kind is TokenKind.Word or TokenKind.QuotedName)
        {
            var second = t[p + 2].Name;
            p += 3;
            return p < t.Count && t[p].Kind == TokenKind.At ? null : (first, second); // @링크는 다른 DB
        }

        p += 1;
        return p < t.Count && t[p].Kind == TokenKind.At ? null : (defaultOwner, first);
    }

    /// <summary><paramref name="open"/> 자리의 ( … ) 안 이름 목록. 괄호가 아니거나 이름만이 아니면 null.</summary>
    private static List<string>? ReadList(List<Token> t, int open)
    {
        if (open >= t.Count || t[open].Kind != TokenKind.Open)
        {
            return null;
        }

        var close = Closing(t, open);
        if (close < 0)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var part in SplitTop(t.GetRange(open + 1, close - open - 1)))
        {
            if (part.Count != 1 || part[0].Kind is not (TokenKind.Word or TokenKind.QuotedName))
            {
                return null;
            }

            names.Add(part[0].Name);
        }

        return names;
    }

    private static int Closing(List<Token> t, int open)
    {
        var depth = 0;
        for (var i = open; i < t.Count; i++)
        {
            if (t[i].Kind == TokenKind.Open)
            {
                depth++;
            }
            else if (t[i].Kind == TokenKind.Close && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<List<Token>> SplitTop(List<Token> t)
    {
        var parts = new List<List<Token>> { new() };
        var depth = 0;
        foreach (var token in t)
        {
            if (token.Kind == TokenKind.Open)
            {
                depth++;
            }
            else if (token.Kind == TokenKind.Close)
            {
                depth--;
            }

            if (depth == 0 && token.Kind == TokenKind.Comma)
            {
                parts.Add([]);
                continue;
            }

            parts[^1].Add(token);
        }

        return parts;
    }

    private enum TokenKind
    {
        Word,
        QuotedName,
        String,
        Number,
        Open,
        Close,
        Comma,
        Dot,
        At,
        Other,
    }

    private sealed record Token(TokenKind Kind, string Text, int Start, int End)
    {
        public string Upper { get; } = Kind == TokenKind.Word ? Text.ToUpperInvariant() : Text;

        /// <summary>이름 규칙: 따옴표 없는 이름은 대문자, 따옴표 이름은 따옴표를 벗겨 그대로.</summary>
        public string Name => Kind == TokenKind.QuotedName ? Text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : Upper;

        public bool Is(string word) => Kind == TokenKind.Word && string.Equals(Upper, word, StringComparison.Ordinal);
    }

    /// <summary>낱말·따옴표 이름·문자열·숫자·기호. 주석은 버린다. 끝나지 않은 문자열·따옴표·주석은 <see cref="FormatException"/>.</summary>
    private static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                var nl = s.IndexOf('\n', i);
                i = nl < 0 ? s.Length : nl + 1;
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? throw new FormatException("comment") : end + 2;
            }
            else if (c is '\'' or '"')
            {
                var start = i;
                if (c == '\'' && start > 0 && list.Count > 0 && list[^1].End == start && list[^1].Kind == TokenKind.Word
                    && list[^1].Upper is "N" or "Q" or "NQ")
                {
                    start = list[^1].Start;
                    list.RemoveAt(list.Count - 1);
                }

                i = QuotedEnd(s, i, c);
                list.Add(new Token(c == '"' ? TokenKind.QuotedName : TokenKind.String, s[start..i], start, i));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$' or '#'))
                {
                    i++;
                }

                list.Add(new Token(TokenKind.Word, s[start..i], start, i));
            }
            else if (char.IsDigit(c))
            {
                var start = i;
                while (i < s.Length && char.IsDigit(s[i]))
                {
                    i++;
                }

                list.Add(new Token(TokenKind.Number, s[start..i], start, i));
            }
            else
            {
                var kind = c switch
                {
                    '(' => TokenKind.Open,
                    ')' => TokenKind.Close,
                    ',' => TokenKind.Comma,
                    '.' => TokenKind.Dot,
                    '@' => TokenKind.At,
                    _ => TokenKind.Other,
                };
                list.Add(new Token(kind, c.ToString(), i, i + 1));
                i++;
            }
        }

        return list;
    }

    /// <summary>따옴표 끝 다음 자리. q'[…]' 꼴은 문자열 앞 Q 를 보고 짝 괄호로 끝낸다.</summary>
    private static int QuotedEnd(string s, int open, char quote)
    {
        var isQ = quote == '\'' && open > 0 && (s[open - 1] is 'q' or 'Q') && (open == 1 || !char.IsLetterOrDigit(s[open - 2]) || s[open - 2] is 'n' or 'N');
        if (isQ && open + 1 < s.Length)
        {
            var delimiter = s[open + 1] switch
            {
                '[' => ']',
                '(' => ')',
                '{' => '}',
                '<' => '>',
                var d => d,
            };
            var end = s.IndexOf(delimiter + "'", open + 2, StringComparison.Ordinal);
            return end < 0 ? throw new FormatException("q-quote") : end + 2;
        }

        for (var i = open + 1; i < s.Length; i++)
        {
            if (s[i] != quote)
            {
                continue;
            }

            if (i + 1 < s.Length && s[i + 1] == quote)
            {
                i++;
                continue;
            }

            return i + 1;
        }

        throw new FormatException("quote");
    }
}
