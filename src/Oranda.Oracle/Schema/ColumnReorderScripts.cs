using System.Globalization;
using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;

namespace Oranda.Oracle.Schema;

/// <summary>열 순서 바꾸는 방식(TSK-13-01).</summary>
public enum ColumnReorderMethod
{
    /// <summary>12c 이상: 열마다 INVISIBLE → VISIBLE (메타데이터만 바뀌어 큰 표도 즉시 끝난다).</summary>
    InvisibleVisible,

    /// <summary>재생성 스크립트: Enterprise Edition 이면 DBMS_REDEFINITION, 아니면 CTAS + RENAME. 스크립트만 만든다.</summary>
    Rebuild,
}

/// <summary>
/// 열 순서 바꾸기 스크립트 만들기. 문장·텍스트만 만들고 실행하지 않는다 — 실행은 반드시 <c>IExecutionService</c>(정책 관문)로 보낸다.
/// 소유: TSK-13-01.
/// </summary>
public static class ColumnReorderScripts
{
    /// <summary>방식에 따라 막는 이유. 비어 있으면 쓸 수 있다.</summary>
    /// <param name="columns">표의 모든 열(INVISIBLE 포함)</param>
    /// <param name="atLeast12c">서버가 12c 이상인지(INVISIBLE 열은 12.1 부터)</param>
    public static IReadOnlyList<string> BlockReasons(
        ColumnReorderMethod method, IReadOnlyList<ColumnInfo> columns, ColumnReorderTraits traits, bool atLeast12c)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(traits);

        var reasons = new List<string>();
        var what = method == ColumnReorderMethod.InvisibleVisible ? "INVISIBLE 열을 쓸 수 없다" : "재생성(CTAS)할 수 없다";
        if (method == ColumnReorderMethod.InvisibleVisible && !atLeast12c)
        {
            reasons.Add("12c 이상 서버에서만 쓸 수 있다(INVISIBLE 열은 12.1 부터)");
        }

        if (traits.IsExternal)
        {
            reasons.Add("외부 표는 " + what);
        }

        if (traits.IsTemporary)
        {
            reasons.Add("임시 표는 " + what);
        }

        if (traits.IsClustered)
        {
            reasons.Add($"클러스터 표({traits.ClusterName})는 " + what);
        }

        if (method == ColumnReorderMethod.InvisibleVisible)
        {
            var invisible = columns.Where(c => c.IsInvisible).Select(c => c.Name).ToList();
            if (invisible.Count > 0)
            {
                reasons.Add($"원래 INVISIBLE 인 열이 있다({string.Join(", ", invisible)}): VISIBLE 로 바꾸면 보이게 되므로 건드리지 않는다");
            }
        }

        return reasons;
    }

    /// <summary>원래 순서와 새 순서가 처음 달라지는 위치(0부터). 같으면 -1.</summary>
    public static int FirstDifference(IReadOnlyList<string> original, IReadOnlyList<string> desired)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(desired);
        EnsurePermutation(original, desired);
        for (var i = 0; i < original.Count; i++)
        {
            if (!string.Equals(original[i], desired[i], StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 방식 ①: 처음 달라지는 위치부터 새 순서대로 열마다 <c>MODIFY (c INVISIBLE)</c> 바로 다음 <c>MODIFY (c VISIBLE)</c>.
    /// 모든 열이 동시에 숨는 순간이 없다. 바뀐 것이 없으면 빈 목록.
    /// </summary>
    /// <param name="original">보이는 열 이름(현재 위치 순)</param>
    /// <param name="desired">새 순서의 열 이름(같은 열의 순열)</param>
    public static IReadOnlyList<SqlStatement> InvisibleVisible(DbObjectRef table, IReadOnlyList<string> original, IReadOnlyList<string> desired)
    {
        ArgumentNullException.ThrowIfNull(table);
        var first = FirstDifference(original, desired);
        if (first < 0)
        {
            return [];
        }

        var target = Target(table);
        var statements = new List<SqlStatement>();
        for (var i = first; i < desired.Count; i++)
        {
            var column = SchemaStatements.Quote(desired[i]);
            statements.Add(Alter($"ALTER TABLE {target} MODIFY ({column} INVISIBLE)"));
            statements.Add(Alter($"ALTER TABLE {target} MODIFY ({column} VISIBLE)"));
        }

        return statements;
    }

    /// <summary>방식 ① 문장을 <c>;</c> 로 끝나는 텍스트로(미리보기용).</summary>
    public static string ToText(IReadOnlyList<SqlStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        return string.Join('\n', statements.Select(s => s.Text + ";"));
    }

    /// <summary>
    /// 방식 ②: 재생성 스크립트(자동으로 실행하지 않는다). 머리에 「검토 뒤 실행」 주석.
    /// </summary>
    /// <param name="columns">표의 모든 열(INVISIBLE 포함, 현재 순서)</param>
    /// <param name="desired">보이는 열의 새 순서(INVISIBLE 열은 뒤에 붙는다)</param>
    /// <param name="enterprise">Enterprise Edition 이면 DBMS_REDEFINITION, 아니면 CTAS + RENAME</param>
    /// <param name="usePrimaryKey">기본키가 있으면 true(CONS_USE_PK), 없으면 ROWID 로 재정의</param>
    /// <param name="dependentDdl">제약·인덱스·트리거·권한·주석 DDL(<c>meta.ddl</c> 의 종속 객체 부분). 없으면 빈 문자열</param>
    public static string Rebuild(
        DbObjectRef table, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<string> desired, bool enterprise, bool usePrimaryKey, string dependentDdl)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(desired);

        var byName = columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var hidden = columns.Where(c => c.IsInvisible).ToList();
        EnsurePermutation([.. columns.Where(c => !c.IsInvisible).Select(c => c.Name)], desired);

        var ordered = desired.Select(n => byName[n]).Concat(hidden).ToList();
        var copied = ordered.Where(c => !c.IsVirtual).ToList();
        var target = Target(table);
        var staging = SchemaStatements.Quote(table.Name + (enterprise ? "$INT" : "$NEW"));
        var stagingRef = SchemaStatements.Quote(table.Owner) + "." + staging;
        var selectList = string.Join(", ", copied.Select(c => SchemaStatements.Quote(c.Name)));

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"-- 열 순서 바꾸기: {table.Owner}.{table.Name}  (방식 ②: {(enterprise ? "DBMS_REDEFINITION" : "CTAS + RENAME")})");
        sb.AppendLine("-- 검토 뒤 실행: 이 스크립트는 자동으로 실행되지 않는다. 읽고, 공간·쓰기 중단 시간·참조 관계를 확인한 뒤 직접 실행한다.");
        sb.AppendLine("-- 새 열 순서: " + string.Join(", ", copied.Concat(ordered.Where(c => c.IsVirtual)).Select(c => c.Name)));
        AppendWarnings(sb, ordered, enterprise);
        sb.AppendLine();

        if (enterprise)
        {
            AppendRedefinition(sb, table, stagingRef, selectList, copied, ordered, usePrimaryKey);
        }
        else
        {
            AppendCtas(sb, table, target, stagingRef, selectList, copied, ordered);
        }

        AppendStatsComment(sb, table);
        AppendDependents(sb, dependentDdl, enterprise);
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// 표 DDL(종속 객체 없음)과 종속 객체까지 담은 DDL 에서 종속 객체 부분만 떼어 낸다.
    /// <c>meta.ddl</c> 은 표 DDL 뒤에 종속 DDL 을 이어 붙이므로 앞부분이 같으면 뒤를 돌려주고, 아니면 전체를 돌려준다.
    /// </summary>
    public static string DependentPart(string tableDdl, string fullDdl)
    {
        ArgumentNullException.ThrowIfNull(tableDdl);
        ArgumentNullException.ThrowIfNull(fullDdl);
        var head = tableDdl.Trim();
        var full = fullDdl.Trim();
        return head.Length > 0 && full.StartsWith(head, StringComparison.Ordinal) ? full[head.Length..].Trim() : full;
    }

    private static void AppendWarnings(StringBuilder sb, IReadOnlyList<ColumnInfo> ordered, bool enterprise)
    {
        var virtuals = ordered.Where(c => c.IsVirtual).Select(c => c.Name).ToList();
        if (virtuals.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"-- 주의: 가상 열({string.Join(", ", virtuals)})은 복사하지 않고 마지막에 다시 추가하므로 순서를 지키지 못한다.");
        }

        var identities = ordered.Where(c => c.IsIdentity).Select(c => c.Name).ToList();
        if (identities.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"-- 주의: Identity 열({string.Join(", ", identities)})은 {(enterprise ? "중간 표에" : "복사한 표에")} 정의가 따라오지 않는다. 방식 ① 을 권한다.");
        }

        if (ordered.Any(c => c.IsInvisible))
        {
            sb.AppendLine("-- 주의: 원래 INVISIBLE 인 열은 맨 뒤에 놓고 다시 INVISIBLE 로 바꾼다.");
        }
    }

    private static void AppendCtas(
        StringBuilder sb, DbObjectRef table, string target, string stagingRef, string selectList,
        IReadOnlyList<ColumnInfo> copied, IReadOnlyList<ColumnInfo> ordered)
    {
        var oldRef = SchemaStatements.Quote(table.Owner) + "." + SchemaStatements.Quote(table.Name + "$OLD");
        sb.AppendLine("-- 1) 새 순서로 복사한다(행 수만큼 시간과 공간이 든다. NOT NULL 은 따라오고 기본값·키·인덱스는 따라오지 않는다)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"CREATE TABLE {stagingRef} AS SELECT {selectList} FROM {target};");
        sb.AppendLine();
        AppendColumnFixes(sb, stagingRef, copied, ordered);
        sb.AppendLine("-- 2) 이름을 바꾼다(이 사이에 쓰기가 없어야 한다). 이 표를 참조하는 외래키는 원본(이름을 바꾼 표)에 그대로 남는다");
        sb.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {target} RENAME TO {SchemaStatements.Quote(table.Name + "$OLD")};");
        sb.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {stagingRef} RENAME TO {SchemaStatements.Quote(table.Name)};");
        sb.AppendLine();
        sb.AppendLine("-- 3) 행 수가 같은지 확인한다");
        sb.AppendLine(CultureInfo.InvariantCulture, $"-- SELECT (SELECT COUNT(*) FROM {oldRef}) AS old_rows, (SELECT COUNT(*) FROM {target}) AS new_rows FROM dual;");
        sb.AppendLine();
        sb.AppendLine("-- 4) 원본을 지운다(휴지통으로 간다). 다른 표가 참조하는 외래키가 있으면 실패하니 그 외래키를 먼저 풀고 4) 뒤에 다시 만든다");
        sb.AppendLine(CultureInfo.InvariantCulture, $"DROP TABLE {oldRef};");
        sb.AppendLine();
    }

    private static void AppendRedefinition(
        StringBuilder sb, DbObjectRef table, string stagingRef, string selectList,
        IReadOnlyList<ColumnInfo> copied, IReadOnlyList<ColumnInfo> ordered, bool usePrimaryKey)
    {
        var target = Target(table);
        var owner = Lit(table.Owner);
        var name = Lit(table.Name);
        var staging = Lit(table.Name + "$INT");
        var option = usePrimaryKey ? "DBMS_REDEFINITION.CONS_USE_PK" : "DBMS_REDEFINITION.CONS_USE_ROWID";

        sb.AppendLine("-- 1) 가능한지 확인한다" + (usePrimaryKey ? "" : " (기본키가 없어 ROWID 로 재정의한다)"));
        sb.AppendLine(CultureInfo.InvariantCulture, $"BEGIN DBMS_REDEFINITION.CAN_REDEF_TABLE({owner}, {name}, {option}); END;");
        sb.AppendLine("/");
        sb.AppendLine();
        sb.AppendLine("-- 2) 새 열 순서의 중간 표를 만든다(행은 복사하지 않는다)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"CREATE TABLE {stagingRef} AS SELECT {selectList} FROM {target} WHERE 1 = 0;");
        sb.AppendLine();
        AppendColumnFixes(sb, stagingRef, copied, ordered);
        sb.AppendLine("-- 3) 재정의를 시작한다(데이터를 복사한다. 원본은 계속 읽고 쓸 수 있다)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"BEGIN DBMS_REDEFINITION.START_REDEF_TABLE(uname => {owner}, orig_table => {name}, int_table => {staging}, options_flag => {option}); END;");
        sb.AppendLine("/");
        sb.AppendLine();
        sb.AppendLine("-- 4) 인덱스·트리거·제약·권한을 중간 표에 복사한다(열 주석은 복사하지 않으니 아래 의존 DDL 의 COMMENT 문을 따로 실행한다)");
        sb.AppendLine("DECLARE");
        sb.AppendLine("  l_errors PLS_INTEGER;");
        sb.AppendLine("BEGIN");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  DBMS_REDEFINITION.COPY_TABLE_DEPENDENTS(uname => {owner}, orig_table => {name}, int_table => {staging},");
        sb.AppendLine("    copy_indexes => DBMS_REDEFINITION.CONS_ORIG_PARAMS, copy_triggers => TRUE, copy_constraints => TRUE,");
        sb.AppendLine("    copy_privileges => TRUE, ignore_errors => FALSE, num_errors => l_errors);");
        sb.AppendLine("END;");
        sb.AppendLine("/");
        sb.AppendLine();
        sb.AppendLine("-- 5) 그동안 바뀐 행을 맞추고 끝낸다(끝나는 순간 두 표가 서로 바뀐다)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"BEGIN DBMS_REDEFINITION.SYNC_INTERIM_TABLE(uname => {owner}, orig_table => {name}, int_table => {staging}); END;");
        sb.AppendLine("/");
        sb.AppendLine(CultureInfo.InvariantCulture, $"BEGIN DBMS_REDEFINITION.FINISH_REDEF_TABLE(uname => {owner}, orig_table => {name}, int_table => {staging}); END;");
        sb.AppendLine("/");
        sb.AppendLine();
        sb.AppendLine("-- 6) 중간 표(이제 예전 순서)를 지운다");
        sb.AppendLine(CultureInfo.InvariantCulture, $"DROP TABLE {stagingRef};");
        sb.AppendLine();
    }

    /// <summary>CTAS 가 옮기지 못하는 기본값·가상 열·숨김 상태를 중간(새) 표에 맞춘다.</summary>
    private static void AppendColumnFixes(StringBuilder sb, string stagingRef, IReadOnlyList<ColumnInfo> copied, IReadOnlyList<ColumnInfo> ordered)
    {
        var fixes = new List<string>();
        foreach (var c in copied.Where(c => !string.IsNullOrWhiteSpace(c.DefaultValue) && !c.IsIdentity))
        {
            fixes.Add($"ALTER TABLE {stagingRef} MODIFY ({SchemaStatements.Quote(c.Name)} DEFAULT {c.DefaultValue!.Trim()});");
        }

        foreach (var c in ordered.Where(c => c.IsVirtual))
        {
            fixes.Add($"ALTER TABLE {stagingRef} ADD ({SchemaStatements.Quote(c.Name)} AS ({c.DefaultValue?.Trim()}));");
        }

        foreach (var c in ordered.Where(c => c.IsInvisible && !c.IsVirtual))
        {
            fixes.Add($"ALTER TABLE {stagingRef} MODIFY ({SchemaStatements.Quote(c.Name)} INVISIBLE);");
        }

        if (fixes.Count == 0)
        {
            return;
        }

        sb.AppendLine("-- 기본값·가상 열·숨김 상태를 맞춘다(CTAS 는 옮기지 않는다)");
        foreach (var f in fixes)
        {
            sb.AppendLine(f);
        }

        sb.AppendLine();
    }

    private static void AppendStatsComment(StringBuilder sb, DbObjectRef table)
    {
        sb.AppendLine("-- 통계: 새 표의 통계를 다시 모은다.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"-- BEGIN DBMS_STATS.GATHER_TABLE_STATS(ownname => {Lit(table.Owner)}, tabname => {Lit(table.Name)}); END;");
        sb.AppendLine("-- /");
        sb.AppendLine();
    }

    private static void AppendDependents(StringBuilder sb, string dependentDdl, bool enterprise)
    {
        if (string.IsNullOrWhiteSpace(dependentDdl))
        {
            sb.AppendLine("-- 의존 객체(제약·인덱스·트리거·권한·주석) DDL 을 읽지 못했거나 없다. 표 상세의 DDL 탭에서 「종속 객체」를 켜서 확인한다.");
            return;
        }

        if (enterprise)
        {
            sb.AppendLine("-- 참고: 원본 표의 의존 DDL(DBMS_METADATA). 4) 가 복사하지 않는 COMMENT 문만 골라 실행한다.");
            foreach (var line in dependentDdl.ReplaceLineEndings("\n").Split('\n'))
            {
                sb.AppendLine("-- " + line);
            }
        }
        else
        {
            sb.AppendLine("-- 5) 원본의 제약·인덱스·트리거·권한·주석(DBMS_METADATA). 4) 로 원본을 지운 뒤에 실행한다(이름이 겹치지 않도록).");
            sb.AppendLine(dependentDdl.Trim());
        }
    }

    private static string Target(DbObjectRef table)
        => SchemaStatements.Quote(table.Owner) + "." + SchemaStatements.Quote(table.Name);

    private static string Lit(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static SqlStatement Alter(string text) => new(text, default, StatementKind.Ddl) { Verb = "ALTER" };

    private static void EnsurePermutation(IReadOnlyList<string> original, IReadOnlyList<string> desired)
    {
        if (original.Count != desired.Count
            || !original.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(desired.OrderBy(n => n, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new ArgumentException("새 순서는 현재 열을 빠짐없이, 한 번씩만 담아야 한다.", nameof(desired));
        }
    }
}
