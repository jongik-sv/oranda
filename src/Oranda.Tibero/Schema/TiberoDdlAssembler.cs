using System.Text.RegularExpressions;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 사전 뷰 행 → 생성 DDL 글 조립(세션 없는 정적 부품, T3 D2b). Tibero 의 <c>DBMS_METADATA.GET_DDL</c> 은 TABLE 만 받고 <c>GET_DEPENDENT_DDL</c> 도 없어
/// (카탈로그 <c>meta.ddl</c> 머리말) 뷰·시퀀스·시노님·인덱스와 표의 종속 객체(인덱스·제약·주석·권한·트리거)는 사전 뷰로 만든다.
/// 문장 모양은 Oracle DBMS_METADATA 의 결과를 따른다(공백은 한 칸으로 줄였고 저장소·세그먼트 속성은 넣지 않는다, EDITIONABLE 은 Tibero 에 없다).
/// <list type="bullet">
/// <item>SQL 문장은 <c>terminator</c> 면 끝에 <c>;</c> 를 붙인다. PL/SQL 객체(트리거)는 새 줄 <c>/</c>(<see cref="TiberoDdlText.WithTerminator"/>).</item>
/// <item>여러 문장은 줄바꿈 하나로 잇는다. 표 DDL 뒤에 종속 문장을 붙이므로 앞부분은 종속 객체 없는 표 DDL 과 같은 글이다
/// (<c>ColumnReorderScripts.DependentPart</c> 가 앞부분을 떼어 낸다).</item>
/// <item>종속 문장은 Oracle GET_DEPENDENT_DDL 처럼 그것만으로 완전하다: 표 DDL 글에 무엇이 들었는지 보지 않는다(열 순서 바꾸기의 CTAS 길은 제약 없는 복사본에
/// 종속 부분만 다시 실행한다).</item>
/// <item>만들 수 없는 종속 객체는 그 자리에 <c>-- </c> 주석 줄로 이유를 남긴다(표 DDL 전체를 실패로 만들지 않는다).</item>
/// </list>
/// </summary>
internal static partial class TiberoDdlAssembler
{
    /// <summary>인덱스 열 하나.</summary>
    internal readonly record struct IndexColumn(string? Name, bool Descending, string? Expression);

    /// <summary>
    /// 인덱스 하나(<c>meta.ddl_index</c>·<c>meta.ddl_table_indexes</c> 행 묶음). <see cref="Partitioned"/> 는 <c>*_OBJECTS</c> 에 인덱스 파티션 행이 있는지다.
    /// </summary>
    internal sealed record DdlIndex(string Owner, string Name, string TableOwner, string TableName, string? Uniqueness, string? IndexType, bool Partitioned,
                                    IReadOnlyList<IndexColumn> Columns)
    {
        public bool IsUnique => string.Equals(Uniqueness, "UNIQUE", StringComparison.Ordinal);
    }

    /// <summary>제약 하나(<c>meta.ddl_constraints</c> 행 묶음). <see cref="Type"/> = P·U·C·R. 외래키면 참조 열이 <see cref="Columns"/> 와 위치로 짝을 이룬다.</summary>
    internal sealed record DdlConstraint(string Name, string Type, bool Enabled, string? Condition, IReadOnlyList<string> Columns,
                                         string? RefOwner, string? RefTable, IReadOnlyList<string?> RefColumns, string? DeleteRule);

    /// <summary>SQL 문장 끝 표시(<c>;</c>).</summary>
    internal static string Statement(string text, bool terminator)
    {
        ArgumentNullException.ThrowIfNull(text);
        return terminator ? text + ";" : text;
    }

    /// <summary>
    /// <c>CREATE OR REPLACE FORCE VIEW "O"."N" ("C1", …) AS</c> 줄바꿈 뒤 질의 글(Oracle 모양, EDITIONABLE 빼고). 열이 없으면 괄호를 빼고, 질의 글은 앞뒤 공백만 뗀다.
    /// </summary>
    internal static string View(string owner, string name, IReadOnlyList<string> columns, string text, bool terminator)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(text);
        var list = columns.Count == 0 ? string.Empty : " (" + string.Join(", ", columns.Select(Quote)) + ")";
        return Statement($"CREATE OR REPLACE FORCE VIEW {TiberoDdlText.Qualified(owner, name)}{list} AS \n  {text.Trim()}", terminator);
    }

    /// <summary>
    /// <c>meta.ddl_sequence</c> 행(min_value, max_value, increment_by, cycle_flag, order_flag, cache_size, last_number — 숫자는 글) →
    /// <c>CREATE SEQUENCE "O"."N" MINVALUE … MAXVALUE … INCREMENT BY … START WITH 마지막값 CACHE n|NOCACHE ORDER|NOORDER CYCLE|NOCYCLE</c>(Oracle 순서).
    /// 값이 NULL 인 절은 뺀다.
    /// </summary>
    internal static string Sequence(string owner, string name, object?[] row, bool terminator)
    {
        ArgumentNullException.ThrowIfNull(row);
        var parts = new List<string> { "CREATE SEQUENCE " + TiberoDdlText.Qualified(owner, name) };
        AddClause(parts, "MINVALUE", Text(row[0]));
        AddClause(parts, "MAXVALUE", Text(row[1]));
        AddClause(parts, "INCREMENT BY", Text(row[2]));
        AddClause(parts, "START WITH", Text(row[6]));
        var cache = Text(row[5]);
        parts.Add(cache is null || cache == "0" ? "NOCACHE" : "CACHE " + cache);
        parts.Add(Yes(row[4]) ? "ORDER" : "NOORDER");
        parts.Add(Yes(row[3]) ? "CYCLE" : "NOCYCLE");
        return Statement(string.Join(' ', parts), terminator);
    }

    /// <summary>
    /// <c>CREATE OR REPLACE SYNONYM "O"."N" FOR "TO"."TN"</c>, 소유자가 PUBLIC 이면 <c>CREATE OR REPLACE PUBLIC SYNONYM "N" FOR …</c>(Oracle 모양).
    /// Tibero <c>*_SYNONYMS</c> 에는 DB 링크 열이 없어 <c>@링크</c> 는 붙지 않는다(카탈로그 <c>meta.name_step</c>). 대상 소유자가 없으면 이름만.
    /// </summary>
    internal static string Synonym(string owner, string name, string? targetOwner, string targetName, bool terminator)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(targetName);
        var target = string.IsNullOrEmpty(targetOwner) ? Quote(targetName) : TiberoDdlText.Qualified(targetOwner, targetName);
        var head = string.Equals(owner, "PUBLIC", StringComparison.Ordinal)
            ? "CREATE OR REPLACE PUBLIC SYNONYM " + Quote(name)
            : "CREATE OR REPLACE SYNONYM " + TiberoDdlText.Qualified(owner, name);
        return Statement(head + " FOR " + target, terminator);
    }

    /// <summary>
    /// <c>meta.ddl_index</c>·<c>meta.ddl_table_indexes</c> 행(owner, index_name, table_owner, table_name, uniqueness, index_type, partitioned,
    /// column_position, column_name, descend, column_expression) → 인덱스(행 순서대로 (소유자, 이름) 으로 묶는다).
    /// </summary>
    internal static List<DdlIndex> Indexes(IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows.Where(r => Text(r[0]) is not null && Text(r[1]) is not null)
            .GroupBy(r => (Owner: Text(r[0])!, Name: Text(r[1])!))
            .Select(g =>
            {
                var first = g.First();
                return new DdlIndex(g.Key.Owner, g.Key.Name, Text(first[2]) ?? g.Key.Owner, Text(first[3]) ?? string.Empty, Text(first[4]), Text(first[5]), Yes(first[6]),
                    [.. g.Where(r => Text(r[8]) is not null || Text(r[10]) is not null)
                        .Select(r => new IndexColumn(Text(r[8]), string.Equals(Text(r[9]), "DESC", StringComparison.OrdinalIgnoreCase), Text(r[10])))]);
            })];
    }

    /// <summary>
    /// 인덱스 종류 글(<c>INDEX_TYPE</c>, 예 <c>FUNCTION-BASED NORMAL</c>·<c>IOT - TOP</c>)에 낱말 <paramref name="word"/> 가 있는가(대소문자 무시, 글자 아닌 것으로 끊는다 —
    /// <c>GLOBAL</c> 안의 <c>LOB</c> 같은 부분 일치를 막는다).
    /// </summary>
    internal static bool HasTypeWord(string? indexType, string word)
    {
        ArgumentException.ThrowIfNullOrEmpty(word);
        return (indexType ?? string.Empty).Split((char[])[' ', '-', '/', '_', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Contains(word, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 인덱스 DDL 을 만들 수 없는 이유(만들 수 있으면 null): 도메인·LOB·클러스터·IOT 인덱스, 파티션 인덱스(LOCAL 인지 사전에서 알 수 없어 전역 인덱스로 잘못 만들지 않는다),
    /// 열을 읽지 못함, 함수 기반 열(<c>SYS_NC…</c>)인데 식이 없음.
    /// </summary>
    internal static string? IndexRefusal(DdlIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var type = index.IndexType ?? string.Empty;
        foreach (var kind in (ReadOnlySpan<string>)["DOMAIN", "LOB", "CLUSTER", "IOT"])
        {
            if (HasTypeWord(type, kind))
            {
                return $"인덱스 {index.Owner}.{index.Name} 는 종류가 {type} 라 DDL 을 만들지 않는다(일반·비트맵·함수 기반 인덱스만 만든다).";
            }
        }

        if (index.Partitioned)
        {
            return $"인덱스 {index.Owner}.{index.Name} 는 파티션 인덱스다: LOCAL·파티션 정의를 사전에서 읽지 않아 DDL 을 만들지 않는다(전역 인덱스로 잘못 만들지 않게).";
        }

        if (index.Columns.Count == 0)
        {
            return $"인덱스 {index.Owner}.{index.Name} 의 열을 사전(*_IND_COLUMNS)에서 읽지 못해 DDL 을 만들 수 없다.";
        }

        return index.Columns.Any(c => c.Expression is null && (c.Name is null || c.Name.StartsWith("SYS_NC", StringComparison.Ordinal)))
            ? $"인덱스 {index.Owner}.{index.Name} 의 함수 기반 열 식을 사전(*_IND_EXPRESSIONS)에서 읽지 못해 DDL 을 만들 수 없다."
            : null;
    }

    /// <summary>
    /// <c>CREATE [UNIQUE|BITMAP] INDEX "O"."N" ON "TO"."T" ("C1" [DESC], 식 …)[ REVERSE]</c>(Oracle 모양, 저장소 절 없음).
    /// 식이 있는 열은 식 글을 그대로, 없는 열은 따옴표 이름. 먼저 <see cref="IndexRefusal"/> 로 거른다.
    /// </summary>
    internal static string Index(DdlIndex index, bool terminator)
    {
        ArgumentNullException.ThrowIfNull(index);
        var type = index.IndexType ?? string.Empty;
        var kind = index.IsUnique ? "UNIQUE " : HasTypeWord(type, "BITMAP") ? "BITMAP " : string.Empty;
        var list = string.Join(", ", index.Columns.Select(c => (c.Expression?.Trim() ?? Quote(c.Name!)) + (c.Descending ? " DESC" : string.Empty)));
        var reverse = type.EndsWith("/REV", StringComparison.OrdinalIgnoreCase) ? " REVERSE" : string.Empty;
        return Statement(
            $"CREATE {kind}INDEX {TiberoDdlText.Qualified(index.Owner, index.Name)} ON {TiberoDdlText.Qualified(index.TableOwner, index.TableName)} ({list}){reverse}",
            terminator);
    }

    /// <summary>
    /// 표의 기본키·유일 키가 만든 인덱스인가(종속 DDL 에서 뺀다 — <c>ADD CONSTRAINT</c> 가 인덱스를 만든다. Oracle 은 이 인덱스도 내고 제약에 USING INDEX 를 달지만,
    /// Tibero 에서 ADD CONSTRAINT 가 있는 인덱스를 다시 쓰는지는 재지 않았다). 사전 정보만 본다: 인덱스 이름이 제약 이름과 같거나,
    /// 유일 인덱스이고 열 순서가 제약 열 순서와 같으면 그렇다.
    /// </summary>
    internal static bool IsConstraintIndex(DdlIndex index, IReadOnlyList<DdlConstraint> constraints)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(constraints);
        var columns = index.Columns.Select(c => c.Name ?? string.Empty).ToList();
        return constraints.Any(c => c.Type is "P" or "U"
            && (string.Equals(c.Name, index.Name, StringComparison.Ordinal) || (index.IsUnique && c.Columns.SequenceEqual(columns, StringComparer.Ordinal))));
    }

    /// <summary>
    /// <c>meta.ddl_constraints</c> 행(constraint_name, constraint_type, status, search_condition, position, column_name, r_owner, r_table_name,
    /// r_column_name, delete_rule) → 제약(행 순서대로 이름으로 묶는다. 열은 position 순 — SQL 이 정렬한다).
    /// </summary>
    internal static List<DdlConstraint> Constraints(IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows.Where(r => Text(r[0]) is not null).GroupBy(r => Text(r[0])!, StringComparer.Ordinal).Select(g =>
        {
            var first = g.First();
            var withColumn = g.Where(r => Text(r[5]) is not null).ToList();
            return new DdlConstraint(g.Key, Text(first[1]) ?? string.Empty, !string.Equals(Text(first[2]), "DISABLED", StringComparison.Ordinal),
                TiberoSessionQuery.Str(first[3]), [.. withColumn.Select(r => Text(r[5])!)], Text(first[6]), Text(first[7]),
                [.. withColumn.Select(r => Text(r[8]))], Text(first[9]));
        })];
    }

    /// <summary>
    /// 진짜 NOT NULL 검사인가: 검사 제약이고 조건 글 전체가 <c>"열" IS NOT NULL</c> 또는 <c>열 IS NOT NULL</c> 이며, 그 열이 제약의 유일한 열이다
    /// (제약 열을 읽지 못했으면 조건 글만 본다). <c>A IS NOT NULL OR B IS NOT NULL</c> 같은 검사는 남는다.
    /// </summary>
    internal static bool IsNotNullCheck(DdlConstraint constraint)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        if (!string.Equals(constraint.Type, "C", StringComparison.Ordinal) || constraint.Condition is null || constraint.Columns.Count > 1)
        {
            return false;
        }

        var match = NotNullCondition().Match(constraint.Condition);
        if (!match.Success)
        {
            return false;
        }

        var column = match.Groups["q"].Success ? match.Groups["q"].Value.Replace("\"\"", "\"", StringComparison.Ordinal) : match.Groups["b"].Value.ToUpperInvariant();
        return constraint.Columns.Count == 0 || string.Equals(constraint.Columns[0], column, StringComparison.Ordinal);
    }

    /// <summary>
    /// 제약 → <c>ALTER TABLE "O"."T" ADD CONSTRAINT "N" …</c>(Oracle 모양·순서: 기본키·유일 키·검사가 먼저(CONSTRAINT), 외래키가 뒤(REF_CONSTRAINT)).
    /// <list type="bullet">
    /// <item><c>PRIMARY KEY (…)</c>·<c>UNIQUE (…)</c>·<c>CHECK (조건)</c>·<c>FOREIGN KEY (…) REFERENCES "RO"."RT" (…) [ON DELETE CASCADE|SET NULL]</c>, 끝에 <c>ENABLE|DISABLE</c>.</item>
    /// <item>NOT NULL 검사(<see cref="IsNotNullCheck"/>)는 표의 열 정의에 있으므로 뺀다. 이름 없는(시스템 이름) 검사도 그 이름으로 만든다.</item>
    /// <item>NOVALIDATE·DEFERRABLE·INITIALLY DEFERRED 는 Tibero 사전 열(VALIDATED·DEFERRABLE·DEFERRED)이 실측표에 없어 붙이지 않는다.</item>
    /// <item>참조 표·열을 읽지 못한 외래키는 주석 줄로 남긴다.</item>
    /// </list>
    /// </summary>
    internal static List<string> ConstraintStatements(string owner, string table, IReadOnlyList<DdlConstraint> constraints, bool terminator)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        var target = TiberoDdlText.Qualified(owner, table);
        var result = new List<string>();
        foreach (var c in constraints.Where(c => c.Type is "P" or "U" or "C" && !IsNotNullCheck(c)))
        {
            var body = c.Type switch
            {
                "P" => "PRIMARY KEY (" + ColumnList(c.Columns) + ")",
                "U" => "UNIQUE (" + ColumnList(c.Columns) + ")",
                _ => "CHECK (" + (c.Condition ?? string.Empty).Trim() + ")",
            };
            result.Add(Statement($"ALTER TABLE {target} ADD CONSTRAINT {Quote(c.Name)} {body} {State(c)}", terminator));
        }

        foreach (var c in constraints.Where(c => c.Type == "R"))
        {
            if (c.RefTable is null || c.Columns.Count == 0 || c.RefColumns.Count != c.Columns.Count || c.RefColumns.Any(r => r is null))
            {
                result.Add($"-- 외래키 {c.Name} 의 참조 표·열을 사전에서 읽지 못해 DDL 을 만들지 못했다.");
                continue;
            }

            var refTarget = c.RefOwner is null ? Quote(c.RefTable) : TiberoDdlText.Qualified(c.RefOwner, c.RefTable);
            var onDelete = c.DeleteRule switch
            {
                "CASCADE" => " ON DELETE CASCADE",
                "SET NULL" => " ON DELETE SET NULL",
                _ => string.Empty,
            };
            result.Add(Statement(
                $"ALTER TABLE {target} ADD CONSTRAINT {Quote(c.Name)} FOREIGN KEY ({ColumnList(c.Columns)}) REFERENCES {refTarget} ({ColumnList(c.RefColumns!)}){onDelete} {State(c)}",
                terminator));
        }

        return result;
    }

    /// <summary><c>COMMENT ON TABLE "O"."T" IS '…'</c> 뒤 열마다 <c>COMMENT ON COLUMN "O"."T"."C" IS '…'</c>(Oracle 모양, 주석 없는 것은 뺀다).</summary>
    internal static List<string> Comments(string owner, string table, string? tableComment, IReadOnlyList<(string Column, string? Comment)> columns, bool terminator)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var result = new List<string>();
        var target = TiberoDdlText.Qualified(owner, table);
        if (!string.IsNullOrEmpty(tableComment))
        {
            result.Add(Statement($"COMMENT ON TABLE {target} IS {Literal(tableComment)}", terminator));
        }

        foreach (var (column, comment) in columns.Where(c => !string.IsNullOrEmpty(c.Comment)))
        {
            result.Add(Statement($"COMMENT ON COLUMN {target}.{Quote(column)} IS {Literal(comment!)}", terminator));
        }

        return result;
    }

    /// <summary>
    /// 객체 권한 행(grantee, privilege, grantable, grantor — <c>meta.detail_privileges(_user)</c>) → <c>GRANT 권한 ON "O"."T" TO "받는 사람" [WITH GRANT OPTION]</c>.
    /// 같은 (받는 사람, 권한)은 처음 나온 자리에 한 번만, 부여자가 여럿이면 하나라도 GRANTABLE = YES 면 <c>WITH GRANT OPTION</c>. PUBLIC 은 따옴표 없이 쓴다.
    /// </summary>
    internal static List<string> Grants(string owner, string table, IReadOnlyList<object?[]> rows, bool terminator)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var target = TiberoDdlText.Qualified(owner, table);
        return [.. rows.Where(r => Text(r[0]) is not null && Text(r[1]) is not null)
            .GroupBy(r => (Grantee: Text(r[0])!, Privilege: Text(r[1])!))
            .Select(g =>
            {
                var to = string.Equals(g.Key.Grantee, "PUBLIC", StringComparison.Ordinal) ? "PUBLIC" : Quote(g.Key.Grantee);
                var option = g.Any(r => Text(r[2]) is "YES" or "Y") ? " WITH GRANT OPTION" : string.Empty;
                return Statement($"GRANT {g.Key.Privilege} ON {target} TO {to}{option}", terminator);
            })];
    }

    /// <summary>
    /// 트리거 DDL(생성 문장, 끝 표시 없음) 뒤에 사용 상태 문장 <c>ALTER TRIGGER "O"."N" ENABLE|DISABLE</c> 을 붙인다 — Oracle GET_DDL('TRIGGER') 모양
    /// (끝 표시를 켜면 <c>… END;</c> 새 줄 <c>/</c> 새 줄 <c>ALTER TRIGGER … ENABLE;</c>, 끄면 <c>… END;</c> 새 줄 <c>ALTER TRIGGER … ENABLE</c>).
    /// 상태를 모르면(null) 상태 문장 없이 생성 문장만.
    /// </summary>
    internal static string Trigger(string createDdl, string owner, string name, string? status, bool terminator)
    {
        ArgumentNullException.ThrowIfNull(createDdl);
        var ddl = TiberoDdlText.WithTerminator(createDdl, terminator);
        if (status is null)
        {
            return ddl;
        }

        var state = string.Equals(status, "DISABLED", StringComparison.Ordinal)
            ? TiberoTriggerDdl.DisableStatement(owner, name)
            : $"ALTER TRIGGER {TiberoDdlText.Qualified(owner, name)} ENABLE";
        return ddl + "\n" + Statement(state, terminator);
    }

    /// <summary>표 DDL 뒤에 종속 문장을 줄바꿈으로 잇는다(없으면 표 DDL 그대로).</summary>
    internal static string WithDependents(string tableDdl, IReadOnlyList<string> statements)
    {
        ArgumentNullException.ThrowIfNull(tableDdl);
        ArgumentNullException.ThrowIfNull(statements);
        return statements.Count == 0 ? tableDdl : tableDdl + "\n" + string.Join("\n", statements);
    }

    /// <summary>따옴표 식별자(안의 따옴표는 두 번).</summary>
    internal static string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>작은따옴표 글 리터럴(안의 작은따옴표는 두 번).</summary>
    internal static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>조건 글 전체가 열 하나의 <c>IS NOT NULL</c>(따옴표 이름 q 또는 맨 이름 b).</summary>
    [GeneratedRegex(@"^\s*(?:""(?<q>(?:[^""]|"""")+)""|(?<b>[A-Za-z][A-Za-z0-9_$#]*))\s+IS\s+NOT\s+NULL\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotNullCondition();

    private static string State(DdlConstraint c) => c.Enabled ? "ENABLE" : "DISABLE";

    private static string ColumnList(IEnumerable<string> columns) => string.Join(", ", columns.Select(Quote));

    private static void AddClause(List<string> parts, string keyword, string? value)
    {
        if (value is not null)
        {
            parts.Add(keyword + " " + value);
        }
    }

    private static string? Text(object? v) => TiberoSessionQuery.Str(v) is { } s ? s.Trim() is { Length: > 0 } t ? t : null : null;

    private static bool Yes(object? v) => Text(v) is "Y" or "YES";
}
