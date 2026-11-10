using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy.CrossDbms;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 교차 복사(원본·대상 DBMS 가 다름)가 만들 표 구조(<see cref="CrossTableMap.MapTable"/>).
/// </summary>
/// <param name="Target">대상 DBMS 로 옮긴 표 구조(<see cref="TableInfo.Table"/> 은 원본 그대로 - 대상 소유자는 DDL 이 정한다).</param>
/// <param name="Notes">원본과 달라지는 것(사전 점검 확인 글·실행 결과 메시지에 들어간다). 같은 DBMS 면 비어 있다.</param>
/// <param name="OmittedConstraints">만들지 않는 제약 이름(옮기지 않는 열을 쓰는 PK·UK·FK·CHECK, LOB 로 옮기는 열을 쓰는 CHECK·FK). 원본 카탈로그로 만드는 작업 안 FK 도 이 이름은 붙이지 않는다.</param>
public sealed record MappedTable(TableInfo Target, IReadOnlyList<string> Notes, IReadOnlySet<string> OmittedConstraints)
{
    /// <summary>
    /// 원본 형식은 LOB 이 아닌데 대상에서 LOB(CLOB·NCLOB·BLOB)이 되는 열(열 이름 → 대상 형식). 대상 DBMS 는 LOB 열에 PK·UK·CHECK·FK·인덱스를 만들 수 없다(Oracle ORA-02329 …).
    /// 같은 DBMS 면 비어 있다.
    /// </summary>
    public IReadOnlyDictionary<string, string> LobMappedColumns { get; init; } = new Dictionary<string, string>();
}

/// <summary><see cref="CrossTableMap.Build"/> 의 결과: <see cref="CreateTableDdl.Build"/> 문장 + <see cref="MappedTable"/> 의 안내·빠진 제약.</summary>
public sealed record CrossCreate(IReadOnlyList<string> Statements, IReadOnlyList<string> Notes, IReadOnlySet<string> OmittedConstraints)
{
    /// <summary>적재 뒤에 보낼 PK·UK·인덱스 문장(새 표 적재를 빠르게, PF4). <see cref="CrossTableMap.Build"/> 에 <c>keysAfterLoad</c> 를 주지 않으면 비어 있다.</summary>
    public IReadOnlyList<string> AfterLoad { get; init; } = [];
}

/// <summary>
/// 교차 복사의 표 만들기·다시 만들기 DDL 입력(xdb-copy XC8, 순수 함수): 열마다 <see cref="CrossTypeMap"/> 으로 옮기고,
/// 대상에서 만들 수 없거나 뜻이 없는 것(옮기지 않는 열·가상 열·IDENTITY 절·도메인)을 뺀다.
/// 실행기와 사전 점검 미리 보기가 같은 <see cref="Build"/> 를 써서 보내는 문장과 보여 주는 문장이 같다. 같은 DBMS 는 아무것도 바꾸지 않는다.
/// CHECK 조건·DEFAULT 식은 글 그대로 둔다(대상이 받지 않으면 그 표의 CREATE 실패, D19).
/// </summary>
public static class CrossTableMap
{
    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>(StringComparer.Ordinal);

    private static readonly Regex Literal = new("'(?:[^']|'')*'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex QuotedIdentifier = new("\"((?:[^\"]|\"\")*)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex ForeignKeyColumns = new(
        "FOREIGN KEY \\((?<child>[^)]*)\\) REFERENCES\\s+(?:\"(?:[^\"]|\"\")*\"\\s*\\.\\s*)?\"(?:[^\"]|\"\")*\"\\s*\\((?<parent>[^)]*)\\)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// 교차 복사에서 이 열을 복사·생성에서 뺄 것인가: 대상 DBMS 로 옮길 형식이 없거나(<see cref="CrossTypeMap"/>), 원본 코덱이 읽지 못한다.
    /// 사전 점검·실행기·<see cref="MapTable"/> 이 모두 이 판정 하나를 쓴다. <paramref name="sourceCodec"/> 이 null 이면 형식 지도만 본다.
    /// </summary>
    public static bool IsDropped(CrossColumnMapping map, ColumnInfo column, IBulkValueCodec? sourceCodec)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(column);
        return map.IsDropped || (sourceCodec is not null && !sourceCodec.IsReadable(column.DataType));
    }

    /// <summary>
    /// 원본 표 구조를 <paramref name="target"/> DBMS 에 만들 구조로 옮긴다. 같은 DBMS 면 <paramref name="table"/> 그대로.
    /// </summary>
    /// <param name="sourceCodec">원본 코덱. 주면 읽지 못하는 열도 옮길 형식이 없는 열과 같이 뺀다(<see cref="IsDropped"/>) - 호출부가 모두 같은 것을 넘긴다.</param>
    public static MappedTable MapTable(Dbms source, Dbms target, TableInfo table, IBulkValueCodec? sourceCodec = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (source == target)
        {
            return new MappedTable(table, [], NoNames);
        }

        // 만들지 않는 열(가상 열·옮길 형식이 없는 열)과 열마다의 변화.
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var lobMapped = new Dictionary<string, string>(StringComparer.Ordinal);
        var virtualColumns = new List<string>();
        var dropped = new List<string>();
        var identity = new List<string>();
        var domains = new List<string>();
        var invisible = new List<string>();
        var converted = new List<string>();
        var columns = new List<ColumnInfo>();
        foreach (var c in table.Columns.OrderBy(c => c.Position))
        {
            if (c.IsVirtual)
            {
                removed.Add(c.Name);
                virtualColumns.Add(c.Name);
                continue;
            }

            var map = CrossTypeMap.Map(source, target, c);
            if (IsDropped(map, c, sourceCodec))
            {
                removed.Add(c.Name);
                dropped.Add($"{c.Name}({c.DataType}: {PrecheckRules.DropReasonText(map.DropReason)})");
                continue;
            }

            var column = map.Target ?? c;
            if (c.IsIdentity)
            {
                // 원본 기본값(ISEQ$$_….nextval)은 원본 IDENTITY 시퀀스를 가리키므로 일반 열에 옮기지 않는다.
                column = column with { DefaultValue = null };
                identity.Add(c.Name);
            }

            if (c.Domain is not null)
            {
                domains.Add($"{c.Name}({c.Domain})");
            }

            if (c.IsInvisible && target == Dbms.Tibero)
            {
                column = column with { IsInvisible = false }; // Tibero 에서 INVISIBLE 열을 확인하지 못했다: 보이는 열로 만든다
                invisible.Add(c.Name);
            }

            if (!string.Equals(column.DataType, c.DataType, StringComparison.OrdinalIgnoreCase))
            {
                converted.Add($"{c.Name}({c.DataType}: {PrecheckRules.NoteText(map.Note)})");
            }

            if (IsLob(column.DataType) && !IsLob(c.DataType))
            {
                lobMapped[c.Name] = column.DataType.ToUpperInvariant(); // 원본은 LOB 이 아니었는데 대상에서 LOB 이 된다: PK·UK·CHECK·FK·인덱스를 걸 수 없다.
            }

            columns.Add(column);
        }

        // 제약: 만들지 않는 열을 쓰는 것은 만들지 않는다. LOB 로 옮기는 열을 쓰는 CHECK·FK 도 만들지 않는다(PK·UK 는 사전 점검이 막는다).
        var lobNames = lobMapped.Keys.ToHashSet(StringComparer.Ordinal);
        var omitted = new HashSet<string>(StringComparer.Ordinal);
        var omittedByColumn = new List<string>();
        var omittedByLob = new List<string>();
        var constraints = new List<ConstraintInfo>();
        foreach (var k in table.Constraints)
        {
            if (removed.Count > 0 && Uses(k, removed))
            {
                omitted.Add(k.Name);
                omittedByColumn.Add(k.Name);
                continue;
            }

            if (lobNames.Count > 0 && (k.Kind is ConstraintKind.Check or ConstraintKind.ForeignKey) && Uses(k, lobNames))
            {
                omitted.Add(k.Name);
                omittedByLob.Add(k.Name);
                continue;
            }

            constraints.Add(k);
        }

        // 인덱스: 만들지 않는 열·LOB 로 옮기는 열을 쓰는 것만 뺀다. 그 밖의 종류 판정(일반 인덱스만 만든다)은 CreateTableDdl 이 같은 DBMS 와 똑같이 한다
        // (카탈로그 TableInfo 에 함수 기반 인덱스의 식이 없어 만들 수 없다). 제약이 만든 인덱스는 제약 쪽에서 정해진다.
        var constraintNames = table.Constraints.Select(k => k.Name).ToHashSet(StringComparer.Ordinal);
        var indexes = new List<IndexInfo>();
        var skippedIndexes = new List<string>();
        var lobIndexes = new List<string>();
        foreach (var index in table.Indexes)
        {
            var usesRemoved = index.Columns.Any(removed.Contains);
            var usesLob = index.Columns.Any(lobNames.Contains);
            if (!usesRemoved && !usesLob)
            {
                indexes.Add(index);
            }
            else if (!constraintNames.Contains(index.Name))
            {
                (usesRemoved ? skippedIndexes : lobIndexes).Add(index.Name);
            }
        }

        var notes = new List<string>();
        AddNote(notes, virtualColumns, "가상 열 {0} 은 만들지 않습니다(값을 넣지 않는 열)");
        AddNote(notes, dropped, "{0} 열은 " + target + " 로 옮길 수 없어 만들지 않습니다");
        AddNote(notes, identity, "IDENTITY 열 {0} 은 일반 열로 만듭니다(IDENTITY 절을 옮기지 않습니다)");
        AddNote(notes, domains, "도메인 열 {0} 은 도메인 없이 기본 형식으로 만듭니다");
        AddNote(notes, invisible, "INVISIBLE 열 {0} 은 보이는 열로 만듭니다");
        AddNote(notes, converted, "형식을 바꿔 만드는 열 {0}");
        AddNote(notes, [.. omittedByColumn.Order(StringComparer.Ordinal)], "만들지 않는 열을 쓰는 제약 {0} 은 만들지 않습니다");
        AddNote(notes, skippedIndexes, "만들지 않는 열을 쓰는 인덱스 {0} 은 만들지 않습니다");
        AddNote(notes, [.. omittedByLob.Order(StringComparer.Ordinal)], "LOB 로 옮기는 열을 쓰는 CHECK·FK 제약 {0} 은 만들지 않습니다(LOB 열에는 걸 수 없음)");
        AddNote(notes, lobIndexes, "LOB 로 옮기는 열을 쓰는 인덱스 {0} 은 만들지 않습니다(LOB 열에는 걸 수 없음)");
        if (source == Dbms.Tibero && table.Constraints.Any(k => k.Kind == ConstraintKind.ForeignKey))
        {
            // Tibero 카탈로그는 FK 의 DEFERRABLE 여부를 알려 주지 않는다(복사가 읽을 수 없는 원본 쪽 한계).
            notes.Add("Tibero 원본 FK 는 지연(DEFERRABLE) 여부를 카탈로그로 알 수 없어 지연 FK 도 즉시 검사 FK 로 만듭니다");
        }

        return new MappedTable(table with { Columns = columns, Constraints = constraints, Indexes = indexes }, notes, omitted)
        {
            LobMappedColumns = lobMapped,
        };
    }

    /// <summary>
    /// 만들 표의 문장(<see cref="CreateTableDdl.Build"/> 의 순서). 같은 DBMS 는 <see cref="CreateTableDdl.Build"/> 그대로이고,
    /// 다르면 <see cref="MapTable"/> 로 옮긴 구조로 만든다. 실행기와 사전 점검 미리 보기가 함께 쓴다.
    /// </summary>
    /// <param name="keysAfterLoad">PK·UK·인덱스를 CREATE TABLE 에서 떼어 <see cref="CrossCreate.AfterLoad"/> 로(새 표는 인덱스 유지 없이 넣는다, PF4)</param>
    public static CrossCreate Build(Dbms source, Dbms target, TableInfo table, string targetOwner, IReadOnlySet<string> tablesInJob,
                                    Func<string, bool>? isServerName = null, IBulkValueCodec? sourceCodec = null, bool keysAfterLoad = false)
    {
        var mapped = MapTable(source, target, table, sourceCodec);
        var (now, afterLoad) = CreateTableDdl.Build(mapped.Target, targetOwner, tablesInJob, isServerName, keysAfterLoad);
        return new CrossCreate(now, mapped.Notes, mapped.OmittedConstraints) { AfterLoad = afterLoad };
    }

    // ---- 작업 안 FK ----

    /// <summary>
    /// 작업 안 FK 하나를 대상에 만들 수 없는 이유(만들 수 있으면 null). 자식 열을 만들지 않거나 LOB 로 옮기는 경우, 부모의 참조 열을 만들지 않는 경우다
    /// (그 열을 쓰는 부모의 PK·UK 도 만들지 않으므로 참조할 키가 없어 ORA-02270 이 난다). 실행기(원본 카탈로그 문장)와 사전 점검(<see cref="ConstraintInfo"/>)이 같은 판정을 쓴다.
    /// </summary>
    /// <param name="parent">부모 표를 옮긴 구조. 작업 안에 없거나 모르면 null(부모 쪽은 보지 않는다).</param>
    public static string? ForeignKeySkipReason(MappedTable child, IReadOnlyList<string> childColumns, MappedTable? parent, IReadOnlyList<string> parentColumns)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(childColumns);
        ArgumentNullException.ThrowIfNull(parentColumns);
        var kept = child.Target.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        if (childColumns.FirstOrDefault(c => !kept.Contains(c)) is { } missing)
        {
            return $"자식 열 {missing} 을 만들지 않음";
        }

        if (childColumns.FirstOrDefault(child.LobMappedColumns.ContainsKey) is { } lob)
        {
            return $"자식 열 {lob} 이(가) LOB 로 옮겨져 FK 를 걸 수 없음";
        }

        if (parent is null || parentColumns.Count == 0)
        {
            return null;
        }

        var parentKept = parent.Target.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        if (parentColumns.FirstOrDefault(c => !parentKept.Contains(c)) is { } parentMissing)
        {
            return $"부모 열 {parentMissing} 을 만들지 않음";
        }

        return null;
    }

    /// <summary>사전 점검: 원본 카탈로그의 FK 가운데 만들 수 없는 것과 그 이유. 같은 DBMS 면 비어 있다.</summary>
    /// <param name="parentSource">FK 의 부모 표(원본 구조). 작업 안에 없으면 null.</param>
    public static IReadOnlyList<(ConstraintInfo Fk, string Reason)> SkippedForeignKeys(Dbms source, Dbms target, TableInfo child, IBulkValueCodec? sourceCodec,
                                                                                    IEnumerable<ConstraintInfo> foreignKeys, Func<DbObjectRef, TableInfo?> parentSource)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(foreignKeys);
        ArgumentNullException.ThrowIfNull(parentSource);
        if (source == target)
        {
            return [];
        }

        var childMap = MapTable(source, target, child, sourceCodec);
        var result = new List<(ConstraintInfo, string)>();
        foreach (var fk in foreignKeys.Where(k => k.Kind == ConstraintKind.ForeignKey))
        {
            var parentTable = fk.ReferencedTable is { } p ? parentSource(p) : null;
            var parentMap = parentTable is null ? null : MapTable(source, target, parentTable, sourceCodec);
            var reason = ForeignKeySkipReason(childMap, fk.Columns, parentMap, ParentKeyColumns(fk, parentTable))
                         ?? (childMap.OmittedConstraints.Contains(fk.Name) ? "제약을 만들지 않음" : null);
            if (reason is not null)
            {
                result.Add((fk, reason));
            }
        }

        return result;
    }

    /// <summary>
    /// 실행기: 원본 카탈로그로 만든 작업 안 FK 문장(<c>SourceForeignKeysAsync</c>)에서 만들 수 없는 것을 뺀다. 열은 문장에서 읽는다(서버가 지은 이름이라 이름이 없는 FK 도 같게 본다).
    /// 문장에서 열을 읽지 못하면 그대로 둔다. 빼는 FK 의 글은 <see cref="FormatSkipped"/> 로 만들어 사전 점검과 같은 모양이다. 같은 DBMS 면 그대로다.
    /// </summary>
    public static (IReadOnlyList<(string Parent, string Ddl, string? Name)> Kept, IReadOnlyList<string> Skipped) FilterForeignKeys(
        Dbms source, Dbms target, TableInfo child, IBulkValueCodec? sourceCodec, IReadOnlyList<(string Parent, string Ddl, string? Name)> foreignKeys,
        Func<string, TableInfo?> parentSource)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(foreignKeys);
        ArgumentNullException.ThrowIfNull(parentSource);
        if (source == target || foreignKeys.Count == 0)
        {
            return (foreignKeys, []);
        }

        var childMap = MapTable(source, target, child, sourceCodec);
        var kept = new List<(string Parent, string Ddl, string? Name)>();
        var skipped = new List<string>();
        foreach (var fk in foreignKeys)
        {
            var match = ForeignKeyColumns.Match(fk.Ddl);
            if (!match.Success)
            {
                kept.Add(fk);
                continue;
            }

            var childColumns = Identifiers(match.Groups["child"].Value);
            var parentColumns = Identifiers(match.Groups["parent"].Value);
            var parentTable = parentSource(fk.Parent);
            var parentMap = parentTable is null ? null : MapTable(source, target, parentTable, sourceCodec);
            var reason = ForeignKeySkipReason(childMap, childColumns, parentMap, parentColumns)
                         ?? (fk.Name is not null && childMap.OmittedConstraints.Contains(fk.Name) ? "제약을 만들지 않음" : null);
            if (reason is null)
            {
                kept.Add(fk);
                continue;
            }

            var name = fk.Name
                       ?? child.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.ForeignKey
                                                                && string.Equals(c.ReferencedTable?.Name, fk.Parent, StringComparison.Ordinal)
                                                                && c.Columns.SequenceEqual(childColumns, StringComparer.Ordinal))?.Name
                       ?? "(이름 없음)";
            skipped.Add(FormatSkipped(name, fk.Parent, reason));
        }

        return (kept, skipped);
    }

    /// <summary>만들지 않는 작업 안 FK 한 개를 알리는 글: <c>이름(→부모: 이유)</c>.</summary>
    public static string FormatSkipped(string name, string parent, string reason) => $"{name}(→{parent}: {reason})";

    /// <summary>FK 가 참조하는 부모 키 열: 부모의 그 PK·UK(<see cref="ConstraintInfo.ReferencedConstraint"/>), 모르면 부모의 PK(REFERENCES 에 열이 없으면 PK 를 참조한다). 부모를 모르면 빈 목록.</summary>
    private static IReadOnlyList<string> ParentKeyColumns(ConstraintInfo fk, TableInfo? parent)
    {
        if (parent is null)
        {
            return [];
        }

        var key = fk.ReferencedConstraint is { } name
            ? parent.Constraints.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
            : null;
        key ??= parent.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.PrimaryKey);
        return key?.Columns ?? [];
    }

    private static List<string> Identifiers(string list)
        => [.. QuotedIdentifier.Matches(list).Select(m => m.Groups[1].Value.Replace("\"\"", "\"", StringComparison.Ordinal))];

    // ---- 도우미 ----

    private static bool IsLob(string dataType) => CrossTypeMap.Normalize(dataType) is "CLOB" or "NCLOB" or "BLOB";

    private static void AddNote(List<string> notes, List<string> items, string format)
    {
        if (items.Count > 0)
        {
            notes.Add(string.Format(CultureInfo.InvariantCulture, format, string.Join(", ", items)));
        }
    }

    /// <summary>
    /// 제약이 <paramref name="names"/> 의 열을 쓰는가. 제약 열 목록이 있으면 그것으로 보고, 비었을 때만(조건 열 목록을 못 읽은 서버) CHECK 조건 글을 본다(<see cref="Mentions"/>).
    /// </summary>
    private static bool Uses(ConstraintInfo k, IReadOnlySet<string> names)
        => k.Columns.Count > 0
            ? k.Columns.Any(names.Contains)
            : k.Kind == ConstraintKind.Check && Mentions(k.Condition, names);

    /// <summary>
    /// CHECK 조건 글에 열 이름이 낱말로 나오는가. 문자열 리터럴('…')은 먼저 지우고, 따옴표 이름은 대소문자를 구별해 같을 때만,
    /// 따옴표 없는 이름은 대소문자 없이 낱말 경계로 본다. 조건 열 목록을 못 읽은 서버를 위한 보조 판정이다.
    /// </summary>
    private static bool Mentions(string? condition, IReadOnlySet<string> names)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return false;
        }

        var text = Literal.Replace(condition, " ");
        if (QuotedIdentifier.Matches(text).Any(m => names.Contains(m.Groups[1].Value.Replace("\"\"", "\"", StringComparison.Ordinal))))
        {
            return true;
        }

        var bare = QuotedIdentifier.Replace(text, " ");
        return names.Any(name => Regex.IsMatch(bare, @"(?<![\w$#])" + Regex.Escape(name) + @"(?![\w$#])",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)));
    }
}
