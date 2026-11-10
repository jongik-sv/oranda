using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy.CrossDbms;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 사전 점검의 순수 판정(DB 를 부르지 않는다). 열 목록·키 열·필요 권한을 고르는 함수는 실행기(T9)도 같이 쓴다:
/// 점검이 본 열과 실행기가 넣는 열이 달라지지 않게 한곳에서 정한다.
/// 형식 지원 여부는 DBMS 의 <see cref="IBulkValueCodec.IsSupported"/>(실행기의 바인드 형식 목록과 같음, 설계 8.3)로 본다.
/// </summary>
public static class PrecheckRules
{
    // ---- 열 목록(점검과 실행기가 공유) ----

    /// <summary>
    /// 복사할 열(<see cref="CopyTablePlan.Columns"/>). 원본 열 순서, 숨김 열 포함.
    /// 가상 열(양쪽)·대상에 없는 열·<see cref="TableRule.SkipColumns"/>·지원하지 않는 형식 열(<paramref name="codec"/>)은 뺀다(메모 D10).
    /// 원본·대상이 같은 DBMS 일 때 쓴다(코덱 하나로 양쪽 형식을 본다).
    /// </summary>
    public static IReadOnlyList<string> SelectColumns(IBulkValueCodec codec, TableInfo source, TableInfo target, TableRule rule)
        => SelectColumns(codec, codec, source, target, rule);

    /// <summary>
    /// <see cref="SelectColumns(IBulkValueCodec, TableInfo, TableInfo, TableRule)"/> 와 같되 원본 열 형식은 <paramref name="sourceCodec"/>,
    /// 대상 열 형식은 <paramref name="targetCodec"/> 으로 본다(xdb-copy XC2, 원본·대상 부품 분리).
    /// </summary>
    public static IReadOnlyList<string> SelectColumns(IBulkValueCodec sourceCodec, IBulkValueCodec targetCodec, TableInfo source, TableInfo target, TableRule rule)
        => SelectColumnsCore(sourceCodec, targetCodec, source, target, rule, null);

    /// <summary>
    /// 교차 복사(원본·대상 DBMS 가 다름)의 복사할 열. 원본 열은 읽을 수 있는 형식(<see cref="IBulkValueCodec.IsReadable"/>)이고 <see cref="CrossTypeMap"/> 이
    /// 옮길 형식이 있다고 본 열만 남긴다 — 실행기(<c>CopyRunner</c>)가 SELECT·DML 에 넣는 열과 같다. 옮길 형식이 없는 열은 <see cref="CompareColumns(IBulkValueCodec, IBulkValueCodec, TableInfo, TableInfo, TableRule, string?, Dbms, Dbms, bool)"/>
    /// 가 <see cref="CopyCheckCodes.ColumnDropped"/> 로 알린다. 값은 원본 형식을 대상 DBMS 로 옮긴 형식(<see cref="CrossColumnMapping.BindType"/>, D3)으로 묶으므로
    /// 대상 코덱이 그 형식을 쓸 수 있는지만 본다(대상 열 형식과의 차이는 서버가 바꾼다). <paramref name="targetFromSource"/> 는 호출부 호환을 위해 남겼고 판정에 쓰지 않는다.
    /// </summary>
    public static IReadOnlyList<string> SelectColumns(IBulkValueCodec sourceCodec, IBulkValueCodec targetCodec, TableInfo source, TableInfo target, TableRule rule,
                                                      Dbms sourceDbms, Dbms targetDbms, bool targetFromSource = false)
        => SelectColumnsCore(sourceCodec, targetCodec, source, target, rule, sourceDbms == targetDbms ? null : (sourceDbms, targetDbms));

    private static List<string> SelectColumnsCore(IBulkValueCodec sourceCodec, IBulkValueCodec targetCodec, TableInfo source, TableInfo target, TableRule rule,
                                                (Dbms Source, Dbms Target)? cross)
    {
        ArgumentNullException.ThrowIfNull(sourceCodec);
        ArgumentNullException.ThrowIfNull(targetCodec);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rule);
        var targetColumns = target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var skip = rule.SkipColumns.ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var column in source.Columns)
        {
            if (column.IsVirtual || skip.Contains(column.Name) || !targetColumns.TryGetValue(column.Name, out var t) || t.IsVirtual)
            {
                continue;
            }

            if (cross is { } pair)
            {
                var map = CrossTypeMap.Map(pair.Source, pair.Target, column);
                // 값은 원본 형식을 대상 DBMS 로 옮긴 형식(BindType)으로 묶는다(D3): 대상 코덱이 그 형식을 쓸 수 있어야 한다. 대상 열 형식과의 차이는 서버가 바꾼다.
                if (!CrossTableMap.IsDropped(map, column, sourceCodec) && targetCodec.IsCrossWritable(map.BindType!))
                {
                    result.Add(column.Name);
                }

                continue;
            }

            if (sourceCodec.IsSupported(column.DataType) && targetCodec.IsSupported(t.DataType))
            {
                result.Add(column.Name);
            }
        }

        return result;
    }

    /// <summary>
    /// 교차 복사(<paramref name="sourceDbms"/> ≠ <paramref name="targetDbms"/>)에서 대상으로 옮길 형식이 없거나 읽을 수 없어 복사에서 빠지는 열 이름.
    /// <see cref="CompareColumns(IBulkValueCodec, IBulkValueCodec, TableInfo, TableInfo, TableRule, string?, Dbms, Dbms, bool)"/> 의
    /// <see cref="CopyCheckCodes.ColumnDropped"/> 판정과 같다(가상·건너뜀·대상에 없는 열은 뺀 열이 아니다). 같은 DBMS 면 빈 집합.
    /// </summary>
    public static HashSet<string> CrossDroppedColumns(IBulkValueCodec sourceCodec, TableInfo source, TableInfo target, TableRule rule, Dbms sourceDbms, Dbms targetDbms)
    {
        ArgumentNullException.ThrowIfNull(sourceCodec);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rule);
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (sourceDbms == targetDbms)
        {
            return result;
        }

        var targetColumns = target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var skip = rule.SkipColumns.ToHashSet(StringComparer.Ordinal);
        foreach (var column in source.Columns)
        {
            if (column.IsVirtual || skip.Contains(column.Name) || !targetColumns.TryGetValue(column.Name, out var t) || t.IsVirtual)
            {
                continue;
            }

            if (CrossTableMap.IsDropped(CrossTypeMap.Map(sourceDbms, targetDbms, column), column, sourceCodec))
            {
                result.Add(column.Name);
            }
        }

        return result;
    }

    /// <summary>
    /// Oracle 카탈로그(<c>*_TAB_COLS.VIRTUAL_COLUMN</c>)는 XMLTYPE 열을 가상 열로 표시한다(실제 저장은 숨은 SYS_NC…$ LOB 열). 그대로 두면 복사가 이 열을 「값을 넣지 않는 가상 열」로
    /// 조용히 건너뛰어 사전 점검에 아무 항목도 나지 않는다(xdb-copy XC8c). 교차 복사에서만 가상 열 표시를 지워 형식 판정이 그 열을 보게 한다(<c>ColumnDropped</c> 확인).
    /// 같은 DBMS 는 예전처럼 빼고 복사하되 <see cref="SameDbmsXmlTypeNotes"/> 가 확인 항목으로 알린다(같은 DBMS 동작 불변). XMLTYPE 가상 열이 없으면 <paramref name="table"/> 그대로.
    /// </summary>
    public static TableInfo ClearXmlTypeVirtual(TableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table.Columns.Any(IsXmlTypeVirtual)
            ? table with { Columns = [.. table.Columns.Select(c => IsXmlTypeVirtual(c) ? c with { IsVirtual = false } : c)] }
            : table;
    }

    /// <summary>
    /// 같은 DBMS 복사: 가상 열로 표시된 XMLTYPE 열은 예전처럼 빼고 복사한다. 막지 않고 열마다 <see cref="CopyCheckCodes.ColumnDropped"/> 확인 항목으로 알린다
    /// (사용자 원칙 「안 되면 그 열을 빼고 알림」). 규칙에서 건너뛰기로 고른 열은 알리지 않는다. 교차 복사는 형식 지도가 판정하므로 빈 목록.
    /// </summary>
    public static IReadOnlyList<CheckItem> SameDbmsXmlTypeNotes(TableInfo source, TableRule rule, string table, Dbms sourceDbms, Dbms targetDbms)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rule);
        if (sourceDbms != targetDbms)
        {
            return [];
        }

        var skip = rule.SkipColumns.ToHashSet(StringComparer.Ordinal);
        return [.. source.Columns.Where(c => IsXmlTypeVirtual(c) && !skip.Contains(c.Name))
            .Select(c => new CheckItem(table, CopyCheckCodes.ColumnDropped, CheckLevel.Confirm,
                $"{table}.{c.Name}: XMLTYPE 열은 복사하지 않습니다(이 열을 빼고 복사합니다).") { Column = c.Name })];
    }

    private static bool IsXmlTypeVirtual(ColumnInfo c)
        => c.IsVirtual && CrossTypeMap.Normalize(c.DataType) is "XMLTYPE" or "SYS.XMLTYPE";

    /// <summary>
    /// 「테이블 다시 만들기」 뒤의 대상 구조: 원본 구조를 대상 소유자 이름으로 둔다. IDENTITY 는 <see cref="CreateTableDdl"/> 이
    /// <c>BY DEFAULT</c> 로 만들므로 값을 넣을 수 있다(<see cref="ColumnInfo.IsIdentity"/> = 거짓, 메모 D12 와 같은 표기).
    /// 점검·실행기는 이 구조로 열·키·문장을 정한다(<see cref="CopyTablePlan.Target"/>).
    /// </summary>
    public static TableInfo AsRecreated(TableInfo source, string targetOwner) =>
        source with
        {
            Table = new DbObjectRef(targetOwner, source.Table.Name, "TABLE"),
            Columns = [.. source.Columns.Select(c => c.IsIdentity ? c with { IsIdentity = false } : c)],
        };

    /// <summary>대상 PK 열. PK 가 없거나 일부가 복사 열에 없으면 빈 목록이다(메모 D11).</summary>
    public static IReadOnlyList<string> KeyColumns(TableInfo target, IReadOnlyList<string> columns)
    {
        var pk = target.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.PrimaryKey);
        if (pk is null || pk.Columns.Count == 0)
        {
            return [];
        }

        return pk.Columns.All(c => columns.Contains(c, StringComparer.Ordinal)) ? pk.Columns : [];
    }

    /// <summary>
    /// 적재를 MERGE 문장으로 하는가: PK 가 있는 표의 「같은 범위 지우고 넣기」·「PK 기준 MERGE」(규칙 「전체」의 기본)는 MERGE 로 넣는다(설계 8.2, 범위 밖에 남은 옛 행이 PK 충돌을 내지 않게). 「비우고 넣기」는 모든 행을 지운 뒤라 INSERT 로 충분하고 UPDATE 권한이 필요 없다.
    /// 실행기(<c>CopyRunner.DmlFor</c>)의 판정과 같아야 하므로 점검의 필요 권한(UPDATE)도 이 판정으로 정한다.
    /// </summary>
    public static bool UsesMerge(TableRule rule, IReadOnlyList<string> keyColumns)
        => keyColumns.Count > 0 && rule.Action is TargetAction.DeleteRangeInsert or TargetAction.MergeByPk;

    /// <summary>대상 표에 필요한 객체 권한(SELECT 는 건수 비교용). 실제로 보낼 문장을 따른다.</summary>
    public static IReadOnlyList<string> RequiredTargetPrivileges(TableRule rule, IReadOnlyList<string> keyColumns)
    {
        var needed = new List<string> { "SELECT", "INSERT" };
        if (rule.Action is TargetAction.DeleteAllInsert or TargetAction.DeleteRangeInsert)
        {
            needed.Add("DELETE");
        }

        if (UsesMerge(rule, keyColumns))
        {
            needed.Add("UPDATE");
        }

        return needed;
    }

    // ---- 열 비교(설계 7장) ----

    /// <summary>원본과 대상의 열 차이. 항목의 <see cref="CheckItem.Table"/> 은 <paramref name="key"/>(규칙 키, 없으면 <c>rule.Table</c>)다. 코덱 하나로 양쪽 형식을 본다(같은 DBMS).</summary>
    public static IReadOnlyList<CheckItem> CompareColumns(IBulkValueCodec codec, TableInfo source, TableInfo target, TableRule rule, string? key = null)
        => CompareColumns(codec, codec, source, target, rule, key);

    /// <summary>
    /// <see cref="CompareColumns(IBulkValueCodec, TableInfo, TableInfo, TableRule, string?)"/> 와 같되 원본 열 형식은 <paramref name="sourceCodec"/>,
    /// 대상 열 형식은 <paramref name="targetCodec"/> 으로 본다(xdb-copy XC2).
    /// </summary>
    public static IReadOnlyList<CheckItem> CompareColumns(IBulkValueCodec sourceCodec, IBulkValueCodec targetCodec, TableInfo source, TableInfo target, TableRule rule,
                                                          string? key = null)
        => CompareColumnsCore(sourceCodec, targetCodec, source, target, rule, key, null, false);

    /// <summary>
    /// 교차 복사(<paramref name="sourceDbms"/> ≠ <paramref name="targetDbms"/>)의 열 차이. 같은 DBMS 면 위 함수와 같다.
    /// 교차에서는 대상으로 옮길 형식이 없거나 읽을 수 없는 열이 오류(<c>UnsupportedType</c>)가 아니라 <see cref="CopyCheckCodes.ColumnDropped"/> 확인 항목이고(그 열은 복사 열에서 빠진다,
    /// <see cref="SelectColumns(IBulkValueCodec, IBulkValueCodec, TableInfo, TableInfo, TableRule, Dbms, Dbms, bool)"/>),
    /// 형식을 바꿔 옮기는 열은 표마다 <see cref="CopyCheckCodes.TypeConverted"/> 한 항목으로 알린다.
    /// 빠지는 열이 대상에서 NOT NULL 이고 기본값이 없으면 <c>TargetRequiredColumnMissing</c> 오류는 그대로다(대상 구조가 원본에서 만들어지면(<paramref name="targetFromSource"/>) 그 열도 만들지 않으므로 아니다).
    /// </summary>
    public static IReadOnlyList<CheckItem> CompareColumns(IBulkValueCodec sourceCodec, IBulkValueCodec targetCodec, TableInfo source, TableInfo target, TableRule rule,
                                                          string? key, Dbms sourceDbms, Dbms targetDbms, bool targetFromSource = false)
        => CompareColumnsCore(sourceCodec, targetCodec, source, target, rule, key, sourceDbms == targetDbms ? null : (sourceDbms, targetDbms), targetFromSource);

    private static List<CheckItem> CompareColumnsCore(IBulkValueCodec sourceCodec, IBulkValueCodec targetCodec, TableInfo source, TableInfo target, TableRule rule,
                                                      string? key, (Dbms Source, Dbms Target)? cross, bool targetFromSource)
    {
        ArgumentNullException.ThrowIfNull(sourceCodec);
        ArgumentNullException.ThrowIfNull(targetCodec);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rule);
        var table = key ?? rule.Table;
        var items = new List<CheckItem>();
        var skip = rule.SkipColumns.ToHashSet(StringComparer.Ordinal);
        var targetColumns = target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var converted = new List<(string Column, string DataType, CrossColumnMapping Map)>();

        foreach (var s in source.Columns)
        {
            // 가상 열은 넣지 않고 조용히 건너뛴다. 사용자가 건너뛰기로 한 열도 같다.
            if (s.IsVirtual || skip.Contains(s.Name))
            {
                continue;
            }

            if (!targetColumns.TryGetValue(s.Name, out var t))
            {
                items.Add(new CheckItem(table, "ColumnNotInTarget", CheckLevel.Confirm,
                    $"{table}.{s.Name}: 대상에 없는 열이라 건너뜁니다.") { Column = s.Name });
                continue;
            }

            if (t.IsVirtual)
            {
                continue;
            }

            CrossColumnMapping? map = null;
            if (cross is { } pair)
            {
                map = CrossTypeMap.Map(pair.Source, pair.Target, s);
                if (CrossTableMap.IsDropped(map, s, sourceCodec))
                {
                    dropped.Add(s.Name);
                    items.Add(new CheckItem(table, CopyCheckCodes.ColumnDropped, CheckLevel.Confirm,
                        $"{table}.{s.Name}: {s.DataType} 열은 {pair.Target} 로 옮길 수 없어 복사에서 뺍니다({DropReasonText(map.DropReason)}).") { Column = s.Name });
                    continue;
                }
            }

            // 교차: 값은 옮긴 형식(BindType, D3)으로 묶으므로 대상 코덱이 그 형식을 쓸 수 있어야 한다(대상 열 형식은 NarrowerType 비교에만 쓴다).
            var writeType = map is null ? t.DataType : map.BindType!;

            // 교차: 원본 쪽은 위에서 읽을 수 있는 형식만 남겼다(읽기 지원 = IsReadable). 같은 DBMS: 원본 형식도 지원 형식이어야 한다.
            var sourceOk = map is not null || sourceCodec.IsSupported(s.DataType);
            // 교차 쓰기 판정은 IsCrossWritable(기본 = IsSupported, Tibero 는 TIMESTAMP WITH TIME ZONE 도 — XC5 실측). 같은 DBMS 는 IsSupported 그대로.
            var targetOk = map is null ? targetCodec.IsSupported(writeType) : targetCodec.IsCrossWritable(writeType);
            if (!sourceOk || !targetOk)
            {
                var unsupported = sourceOk ? writeType : s.DataType;
                items.Add(new CheckItem(table, "UnsupportedType", CheckLevel.Error,
                    $"{table}.{s.Name}: 복사가 지원하지 않는 형식({unsupported})입니다. 이 열을 건너뛰거나 규칙에서 빼세요.")
                {
                    Column = s.Name,
                    Choices = [CheckChoice.SkipColumn],
                });
                continue;
            }

            if (t.IsIdentity)
            {
                items.Add(new CheckItem(table, "IdentityAlways", CheckLevel.Error,
                    $"{table}.{s.Name}: 대상 열이 GENERATED ALWAYS AS IDENTITY 라 값을 넣을 수 없습니다.") { Column = s.Name });
                continue;
            }

            // DDL 에만 해당하는 변환(IDENTITY → 일반 열, RAW → BLOB, 긴 문자열 → CLOB …)은 대상 표를 원본에서 만들 때만 뜻이 있다(있는 대상 표는 이미 그 구조다).
            // 값에 닿는 변환(BINARY_* → NUMBER, 시간대 지역 → 오프셋)은 늘 알린다.
            if (map is { Kind: CrossMapKind.Converted } && (map.Note is CrossNote.BinaryAsNumber or CrossNote.TimeZoneRegionToOffset || targetFromSource))
            {
                converted.Add((s.Name, s.DataType, map));
            }

            if (NarrowerReason(s, t, map is not null) is { } reason)
            {
                items.Add(new CheckItem(table, "NarrowerType", CheckLevel.Confirm, $"{table}.{s.Name}: {reason}") { Column = s.Name });
            }
        }

        if (cross is { } dropping && dropped.Count > 0)
        {
            items.AddRange(DroppedKeyAndFilterItems(table, dropping.Target, source, target, rule, dropped));
        }

        if (converted.Count > 0 && cross is { } converting)
        {
            var valueLevel = converted.Any(c => c.Map.Note is CrossNote.BinaryAsNumber or CrossNote.TimeZoneRegionToOffset);
            items.Add(new CheckItem(table, CopyCheckCodes.TypeConverted, valueLevel ? CheckLevel.Confirm : CheckLevel.Pass,
                $"{table}: {converting.Target} 로 옮기며 형식을 바꾸는 열 — "
                + string.Join(", ", converted.Select(c => $"{c.Column}({c.DataType}: {NoteText(c.Map.Note)})")) + "."));
        }

        // 대상에만 있는 열 중 값이 꼭 필요한 열. 원본에서 읽지 못하는 열(가상·건너뜀)도 같다.
        foreach (var t in target.Columns)
        {
            if (t.IsVirtual || t.IsIdentity || t.IsNullable || !string.IsNullOrWhiteSpace(t.DefaultValue))
            {
                continue;
            }

            // 교차: 옮길 수 없어 뺀 열은 값을 가져올 수 없다. 단 대상 구조를 원본에서 만들면 그 열은 만들지 않으므로 필요한 열이 아니다.
            if (targetFromSource && dropped.Contains(t.Name))
            {
                continue;
            }

            var read = !dropped.Contains(t.Name)
                       && source.Columns.Any(s => string.Equals(s.Name, t.Name, StringComparison.Ordinal) && !s.IsVirtual && !skip.Contains(s.Name));
            if (!read)
            {
                items.Add(new CheckItem(table, "TargetRequiredColumnMissing", CheckLevel.Error,
                    $"{table}.{t.Name}: 대상에서 NOT NULL 이고 기본값이 없는데 원본에서 값을 가져올 수 없습니다.") { Column = t.Name });
            }
        }

        return items;
    }

    // 키(원본·대상 PK)·기간 조건·부모 키 연결 열이 복사에서 빠지면 오류(KeyColumnDropped): 키가 없어져 행을 구별할 수 없고,
    // 조건 열은 SELECT·DELETE 가 걸러내는데 넣는 행에는 NULL 이 들어가 다시 실행할 때 행이 겹친다. 열마다 한 항목.
    private static List<CheckItem> DroppedKeyAndFilterItems(string table, Dbms targetDbms, TableInfo source, TableInfo target, TableRule rule, HashSet<string> dropped)
    {
        var keyColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in new[] { source, target })
        {
            if (info.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.PrimaryKey) is { } pk)
            {
                keyColumns.UnionWith(pk.Columns);
            }
        }

        var items = new List<CheckItem>();
        foreach (var column in dropped.Order(StringComparer.Ordinal))
        {
            var roles = new List<string>();
            var reasons = new List<string>();
            if (keyColumns.Contains(column))
            {
                roles.Add("키");
                reasons.Add("행을 구별할 키가 없어집니다.");
            }

            if (rule.PeriodColumns.Any(p => string.Equals(p.Column, column, StringComparison.Ordinal)))
            {
                roles.Add("기간 조건");
                reasons.Add("기간 조건에 쓰는 열인데 넣는 행에서는 NULL 이 되어 다시 실행하면 행이 겹칩니다.");
            }

            if (rule.Parent is { } parent && parent.ChildColumns.Contains(column, StringComparer.Ordinal))
            {
                roles.Add("부모 키 연결");
                reasons.Add("부모 키 연결 조건에 쓰는 열인데 넣는 행에서는 NULL 이 되어 다시 실행하면 행이 겹칩니다.");
            }

            if (roles.Count == 0)
            {
                continue;
            }

            items.Add(new CheckItem(table, CopyCheckCodes.KeyColumnDropped, CheckLevel.Error,
                $"{table}.{column}: {string.Join("·", roles)} 열을 {targetDbms} 로 옮길 형식이 없어 복사할 수 없습니다. {string.Join(" ", reasons)} 이 테이블을 규칙에서 빼세요.") { Column = column });
        }

        return items;
    }

    // ---- 교차 복사 안내 ----

    /// <summary>
    /// 교차 복사로 Tibero 에 넣는 표의 BLOB 안내(<see cref="CopyCheckCodes.BlobEmptyToNull"/>, 통과 항목): 길이 0 인 BLOB 값은 Tibero 에서 NULL 이 된다.
    /// 복사 열(<paramref name="columns"/>) 가운데 BLOB 으로 옮기는 열이 없거나 교차가 아니거나 대상이 Tibero 가 아니면 null.
    /// </summary>
    public static CheckItem? CrossBlobNote(Dbms sourceDbms, Dbms targetDbms, TableInfo source, IReadOnlyList<string> columns, string table)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(columns);
        if (sourceDbms == targetDbms || targetDbms != Dbms.Tibero)
        {
            return null;
        }

        var blobs = source.Columns
            .Where(c => columns.Contains(c.Name, StringComparer.Ordinal) && CrossTypeMap.Map(sourceDbms, targetDbms, c).BindType == "BLOB")
            .Select(c => c.Name)
            .ToList();
        return blobs.Count == 0
            ? null
            : new CheckItem(table, CopyCheckCodes.BlobEmptyToNull, CheckLevel.Pass,
                $"{table}: BLOB 열 {string.Join(", ", blobs)} — 길이가 0 인 BLOB 값은 Tibero 에서 NULL 이 됩니다.");
    }

    /// <summary>글자당 최대 바이트(UTF-8 기준). 서버 문자셋을 읽지 않으므로 늘 이 값으로 어림한다(보수적으로 큰 쪽).</summary>
    internal const int MaxBytesPerChar = 4;

    /// <summary>Oracle CHAR 의 바이트 한도(CHAR 단위로 만들어도 넘지 못한다).</summary>
    internal const int OracleCharMaxBytes = 2000;

    /// <summary>
    /// 교차 복사로 Oracle 에 넣을 때, 문자 단위(CHAR) 길이 열이 대상의 바이트 한도를 넘을 수 있다는 확인 항목(<see cref="CopyCheckCodes.CrossStringBytes"/>, 표마다 하나).
    /// 대상 VARCHAR2 는 CHAR 단위로 만들어도 4000바이트를 넘지 못한다(MAX_STRING_SIZE 를 읽지 않고 STANDARD 로 본다): 복사 열(<paramref name="columns"/>) 가운데
    /// 원본·대상이 모두 VARCHAR2(한도 4000)·CHAR(한도 <see cref="OracleCharMaxBytes"/>) 이고 (둘 중 짧은 길이 × <see cref="MaxBytesPerChar"/>) &gt; 한도인 열이 있으면 낸다. 넘는 값은 그 행이 실패한다(ORA-12899).
    /// 표를 새로 만드는 경우(대상 열 = 원본 열)와 기존 대상 표 둘 다 본다. 교차가 아니거나 대상이 Oracle 이 아니면 null.
    /// </summary>
    public static CheckItem? CrossStringByteLimitNote(Dbms sourceDbms, Dbms targetDbms, TableInfo source, TableInfo target, IReadOnlyList<string> columns, string table)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(columns);
        if (sourceDbms == targetDbms || targetDbms != Dbms.Oracle)
        {
            return null;
        }

        var targetColumns = target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var over = source.Columns
            .Where(c => !c.IsVirtual
                        && columns.Contains(c.Name, StringComparer.Ordinal)
                        && ByteLimit(CrossTypeMap.Normalize(c.DataType)) is { } limit
                        && CrossTypeMap.Map(sourceDbms, targetDbms, c).BindType == CrossTypeMap.Normalize(c.DataType)
                        && c.Length is { } length
                        && targetColumns.TryGetValue(c.Name, out var t)
                        && CrossTypeMap.Normalize(t.DataType) == CrossTypeMap.Normalize(c.DataType)
                        && (long)Math.Min(length, t.Length ?? length) * MaxBytesPerChar > limit)
            .Select(c => c.Name)
            .ToList();
        return over.Count == 0
            ? null
            : new CheckItem(table, CopyCheckCodes.CrossStringBytes, CheckLevel.Confirm,
                $"{table}: 원본 문자 단위 열 {string.Join(", ", over)} 이(가) 대상 Oracle 바이트 한도(VARCHAR2 {CrossTypeMap.OracleVarcharMaxLength}바이트·CHAR {OracleCharMaxBytes}바이트)를 넘을 수 있습니다"
                + $"(글자당 최대 {MaxBytesPerChar}바이트로 계산, 대상 MAX_STRING_SIZE 는 STANDARD 로 봄). 넘는 값은 그 행이 실패합니다(ORA-12899).");

        // Oracle 의 바이트 한도: VARCHAR2 4000(STANDARD), CHAR 2000. 그 밖의 형식은 보지 않는다.
        static int? ByteLimit(string type) => type switch
        {
            "VARCHAR2" => CrossTypeMap.OracleVarcharMaxLength,
            "CHAR" => OracleCharMaxBytes,
            _ => null,
        };
    }

    /// <summary>
    /// 교차 복사로 Oracle CHAR 열을 Tibero 에 넣을 때의 안내(<see cref="CopyCheckCodes.CharPadDiffers"/>, 통과 항목, 표마다 하나).
    /// Oracle 은 CHAR 를 바이트로 채우고 Tibero CHAR(n CHAR) 는 글자 수로 채워, 여러 바이트 글자가 든 값은 뒤 공백 수가 달라진다.
    /// 복사 열(<paramref name="columns"/>) 가운데 원본·대상이 모두 CHAR 인 열이 있을 때만 낸다. 교차가 아니거나 방향이 Oracle → Tibero 가 아니면 null.
    /// </summary>
    public static CheckItem? CrossCharPadNote(Dbms sourceDbms, Dbms targetDbms, TableInfo source, TableInfo target, IReadOnlyList<string> columns, string table)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(columns);
        if (sourceDbms != Dbms.Oracle || targetDbms != Dbms.Tibero)
        {
            return null;
        }

        var targetColumns = target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var chars = source.Columns
            .Where(c => !c.IsVirtual
                        && columns.Contains(c.Name, StringComparer.Ordinal)
                        && CrossTypeMap.Normalize(c.DataType) == "CHAR"
                        && targetColumns.TryGetValue(c.Name, out var t)
                        && CrossTypeMap.Normalize(t.DataType) == "CHAR")
            .Select(c => c.Name)
            .ToList();
        return chars.Count == 0
            ? null
            : new CheckItem(table, CopyCheckCodes.CharPadDiffers, CheckLevel.Pass,
                $"{table}: CHAR(n CHAR) 열 {string.Join(", ", chars)} 은(는) Tibero 에서 글자 수로 공백을 채워 뒤 공백 수가 달라질 수 있습니다.");
    }

    // ---- 기본값의 시퀀스 ----

    private static readonly Regex SequenceNextVal = new(
        "(?:(?<own>\"(?:[^\"]|\"\")+\"|[A-Za-z][\\w$#]*)\\s*\\.\\s*)?(?<seq>\"(?:[^\"]|\"\")+\"|[A-Za-z][\\w$#]*)\\s*\\.\\s*(?:\"NEXTVAL\"|NEXTVAL)(?![\\w$#\"])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>
    /// 기본값 식이 <c>NEXTVAL</c> 로 가리키는 시퀀스(나온 순서, 중복 없음). 형태는 <c>SEQ.NEXTVAL</c>·<c>"SEQ".NEXTVAL</c>·<c>OWN.SEQ.NEXTVAL</c>·<c>"OWN"."SEQ"."NEXTVAL"</c>.
    /// 따옴표 없는 이름은 대문자로, 따옴표 이름은 글자 그대로 돌려준다. 소유자 접두가 없으면 <c>Owner</c> 는 null.
    /// </summary>
    public static IReadOnlyList<(string? Owner, string Name)> SequenceReferences(string? defaultValue)
    {
        var result = new List<(string? Owner, string Name)>();
        if (string.IsNullOrWhiteSpace(defaultValue))
        {
            return result;
        }

        foreach (Match m in SequenceNextVal.Matches(defaultValue))
        {
            var reference = (m.Groups["own"].Success ? IdentifierText(m.Groups["own"].Value) : null, IdentifierText(m.Groups["seq"].Value));
            if (!result.Contains(reference))
            {
                result.Add(reference);
            }
        }

        return result;
    }

    private static string IdentifierText(string identifier)
        => identifier.Length >= 2 && identifier[0] == '"' && identifier[^1] == '"'
            ? identifier[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : identifier.ToUpperInvariant();

    /// <summary>열의 기본값을 뺀 표 구조(<paramref name="columns"/> 에 든 열 이름만). 빼는 열이 없으면 <paramref name="table"/> 그대로.</summary>
    public static TableInfo WithoutDefaults(TableInfo table, IReadOnlySet<string> columns)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        return columns.Count == 0 || !table.Columns.Any(c => columns.Contains(c.Name))
            ? table
            : table with { Columns = [.. table.Columns.Select(c => columns.Contains(c.Name) ? c with { DefaultValue = null } : c)] };
    }

    /// <summary>
    /// 교차 복사로 Tibero 에 넣을 때의 문자셋 안내(<see cref="CopyCheckCodes.CrossCharset"/>, 확인 항목, 작업 전체에 하나).
    /// 문자 열(CHAR·VARCHAR2·CLOB·LONG)을 하나라도 복사할 때만 낸다. 대상 문자셋을 조회하지 않고 늘 같은 글을 낸다.
    /// </summary>
    public static CheckItem? CrossCharsetNote(Dbms sourceDbms, Dbms targetDbms, IEnumerable<CopyTablePlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (sourceDbms == targetDbms || targetDbms != Dbms.Tibero)
        {
            return null;
        }

        var hasText = plans.Any(p => p.Source.Columns.Any(c => p.Columns.Contains(c.Name, StringComparer.Ordinal)
                                                              && CrossTypeMap.Normalize(c.DataType) is "VARCHAR2" or "CHAR" or "CLOB" or "LONG"));
        return hasText
            ? new CheckItem(null, CopyCheckCodes.CrossCharset, CheckLevel.Confirm,
                "문자 열을 Tibero 로 옮깁니다. 대상 문자셋 밖 글자는 실패하거나 바뀔 수 있습니다. 대상 DB 의 문자셋이 원본보다 좁은지 확인하세요.")
            : null;
    }

    internal static string DropReasonText(CrossDropReason reason) => reason switch
    {
        CrossDropReason.LongSource => "LONG·LONG RAW 는 대량 읽기를 지원하지 않음",
        CrossDropReason.RowId => "ROWID 는 다른 DB 에서 뜻이 없음",
        CrossDropReason.LocalTimeZone => "TIMESTAMP WITH LOCAL TIME ZONE 은 옮기지 않음",
        _ => "대량 읽기를 지원하지 않는 형식",
    };

    internal static string NoteText(CrossNote note) => note switch
    {
        CrossNote.BinaryAsNumber => "NUMBER 로 옮김, NaN·무한대 값은 그 행이 실패함",
        CrossNote.TimeZoneRegionToOffset => "시간대 지역 이름은 그 시각의 UTC 오프셋으로 바뀜",
        CrossNote.RawToBlob => "2000바이트가 넘는 RAW 는 BLOB 으로 옮김",
        CrossNote.LongStringToClob => "4000바이트가 넘는 VARCHAR2 는 CLOB 으로 옮김",
        CrossNote.LongNationalStringToNClob => "대상 한도를 넘는 NVARCHAR2·NCHAR 는 NCLOB 으로 옮김",
        CrossNote.LongToClob => "CLOB 으로 옮김",
        CrossNote.LongRawToBlob => "BLOB 으로 옮김",
        CrossNote.IdentityToPlain => "IDENTITY 가 일반 열이 됨",
        _ => "형식을 바꿔 옮김",
    };

    // ---- 마스킹(설계 8.5) ----

    /// <summary>
    /// 마스킹 규칙 점검. PK·UK 열과 부모 키 규칙의 연결 열에는 마스킹을 걸 수 없다(<c>MaskOnKey</c>).
    /// 가리는 열이 있으면 목록을 <c>MaskedColumns</c> 통과 항목으로 보여 준다. 규칙이 원본에 없는 열이나 열 형식에 못 쓰는 종류면 오류다.
    /// </summary>
    public static IReadOnlyList<CheckItem> CheckMasks(TableRule rule, TableInfo source, IReadOnlyList<string> keyColumns, string? key = null,
                                                      IReadOnlySet<string>? dropped = null)
    {
        if (rule.Masks.Count == 0)
        {
            return [];
        }

        var table = key ?? rule.Table;
        var keys = new HashSet<string>(keyColumns, StringComparer.Ordinal);
        foreach (var c in source.Constraints.Where(c => c.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique))
        {
            keys.UnionWith(c.Columns);
        }

        if (rule.Parent is { } parent)
        {
            keys.UnionWith(parent.ChildColumns);
        }

        var columns = source.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var skip = rule.SkipColumns.ToHashSet(StringComparer.Ordinal);
        var items = new List<CheckItem>();
        var applied = new List<string>();
        foreach (var mask in rule.Masks)
        {
            if (!columns.TryGetValue(mask.Column, out var column))
            {
                items.Add(new CheckItem(table, "MaskedColumns", CheckLevel.Error,
                    $"{table}.{mask.Column}: 마스킹 규칙의 열이 원본에 없습니다.") { Column = mask.Column });
                continue;
            }

            // 복사하지 않는 열(가상·건너뜀·교차 복사에서 빠지는 열)은 가릴 값도 없다.
            if (column.IsVirtual || skip.Contains(mask.Column) || dropped?.Contains(mask.Column) == true)
            {
                continue;
            }

            if (keys.Contains(mask.Column))
            {
                items.Add(new CheckItem(table, "MaskOnKey", CheckLevel.Error,
                    $"{table}.{mask.Column}: 기본키·유일키 열이나 부모 키 연결 열은 가릴 수 없습니다.") { Column = mask.Column });
                continue;
            }

            if (!MaskFunctions.AllowedKinds(column.DataType).Contains(mask.Kind))
            {
                items.Add(new CheckItem(table, "MaskedColumns", CheckLevel.Error,
                    $"{table}.{mask.Column}: {column.DataType} 열에는 {KindName(mask)} 규칙을 쓸 수 없습니다.") { Column = mask.Column });
                continue;
            }

            applied.Add($"{mask.Column}({KindName(mask)})");
        }

        if (applied.Count > 0)
        {
            items.Add(new CheckItem(table, "MaskedColumns", CheckLevel.Pass, $"{table}: 가리는 열 {string.Join(", ", applied)}"));
        }

        return items;
    }

    /// <summary>
    /// 부모 키 규칙이 기대는 부모 쪽 연결 열에 마스킹이 걸렸는지. 자식 조건은 부모의 연결 열 값으로 만들어지므로 가리면 대상 쪽 범위가 어긋난다.
    /// </summary>
    public static IReadOnlyList<CheckItem> CheckParentLinkMasks(CopyJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var items = new List<CheckItem>();
        foreach (var child in job.Tables.Where(r => r.Kind == RuleKind.ParentKey && r.Parent is not null))
        {
            var link = child.Parent!;
            var parent = CopyOwners.Parent(job, child);
            if (parent is null)
            {
                continue;
            }

            var parentKey = CopyOwners.Key(job, parent);
            foreach (var mask in parent.Masks.Where(m => link.ParentColumns.Contains(m.Column, StringComparer.Ordinal)))
            {
                items.Add(new CheckItem(parentKey, "MaskOnKey", CheckLevel.Error,
                    $"{parentKey}.{mask.Column}: {CopyOwners.Key(job, child)} 의 부모 키 연결 열이라 가릴 수 없습니다.") { Column = mask.Column });
            }
        }

        return items;
    }

    // ---- 기간 ----

    /// <summary>
    /// 기간 규칙이 있는데 계산한 범위(시작 포함·끝 제외)가 비었으면 오류 항목 하나(<see cref="CopyCheckCodes.EmptyPeriod"/>), 아니면 null.
    /// 예: 직접 지정 2026-11-01 ~ 2026-11-01 「미만」.
    /// </summary>
    public static CheckItem? EmptyPeriod(CopyJob job, ResolvedPeriod period)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(period);
        if (period is not { From: { } from, To: { } to } || from < to
            || !job.Tables.Any(r => r.Kind == RuleKind.Period))
        {
            return null;
        }

        var hint = job.Period is { Kind: PeriodKind.Custom, EndInclusive: false } && from == to
            ? " 끝 날짜를 하루 늦추거나 「끝 날짜 포함」을 고르세요."
            : " 끝 날짜를 시작보다 늦게 고르세요.";
        return new CheckItem(null, CopyCheckCodes.EmptyPeriod, CheckLevel.Error,
            string.Create(CultureInfo.InvariantCulture,
                $"기간 {from:yyyy-MM-dd} 이상 {to:yyyy-MM-dd} 미만에 드는 날이 없어 기간 규칙 테이블은 한 행도 복사하지 않습니다.{hint}"));
    }

    // ---- 계정(묶음 B) ----

    /// <summary>
    /// 대상 접속 계정(<paramref name="sessionUser"/>)이 아닌 계정의 표에 쓰는 규칙이 있으면 확인 항목 하나(<see cref="CopyCheckCodes.OtherOwnerTargets"/>), 없으면 null.
    /// 계정·표 수와 그중 지우는 표 수를 적는다(실행 확인 대화상자와 같은 셈).
    /// </summary>
    public static CheckItem? OtherOwnerTargets(CopyJob job, string sessionUser)
    {
        ArgumentNullException.ThrowIfNull(job);
        var owners = CopyOwners.OtherTargetOwners(job, sessionUser);
        if (owners.Count == 0)
        {
            return null;
        }

        var rules = job.Tables.Where(r => r.Kind != RuleKind.Exclude
                                          && owners.Contains(CopyOwners.Target(job, r), CopyOwners.OwnerComparer)).ToList();
        var deleting = rules.Count(r => r.Action is TargetAction.DeleteAllInsert or TargetAction.DeleteRangeInsert or TargetAction.Recreate);
        var n = rules.Count.ToString(CultureInfo.InvariantCulture);
        var what = deleting == rules.Count
            ? $"테이블 {n}개를 지우고 넣습니다"
            : $"테이블 {n}개에 씁니다(그중 {deleting.ToString(CultureInfo.InvariantCulture)}개는 지우고 넣습니다)";
        return new CheckItem(null, CopyCheckCodes.OtherOwnerTargets, CheckLevel.Confirm,
            $"대상 접속 계정({sessionUser})이 아닌 계정 {string.Join(", ", owners)} 의 {what}: {string.Join(", ", rules.Select(r => CopyOwners.TargetName(job, r)))}.");
    }

    // ---- 시퀀스 ----

    /// <summary>
    /// 시퀀스를 앞당길 때 소비할 <c>NEXTVAL</c> 개수(0 이상). <paramref name="next"/> = 다음에 나올 값, <paramref name="max"/> = 열의 최대값.
    /// 값은 내리지 않고, 내림차순 시퀀스(<paramref name="increment"/> 0 이하)는 보정하지 않는다.
    /// </summary>
    public static long SequenceGap(long next, long max, long increment)
    {
        if (increment <= 0 || next > max)
        {
            return 0;
        }

        try
        {
            return checked(((max - next) / increment) + 1);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    // ---- 내부 ----

    private static string KindName(MaskRule mask) => mask.Kind switch
    {
        MaskKind.Fixed => "고정값",
        MaskKind.KeepFirst => $"앞 {mask.Count.ToString(CultureInfo.InvariantCulture)}글자",
        MaskKind.KeepLast => $"뒤 {mask.Count.ToString(CultureInfo.InvariantCulture)}글자",
        MaskKind.Hash => "해시",
        _ => "NULL",
    };

    private enum Family { Text, Clob, Number, DateTime, IntervalDs, IntervalYm, Raw, Blob, Other }

    private static string? NarrowerReason(ColumnInfo s, ColumnInfo t, bool crossDbms = false)
    {
        var sf = FamilyOf(crossDbms ? CrossTypeMap.Normalize(s.DataType) : s.DataType);
        var tf = FamilyOf(crossDbms ? CrossTypeMap.Normalize(t.DataType) : t.DataType);
        var from = s.DataType;
        var to = t.DataType;

        if (sf == Family.Clob && tf == Family.Text)
        {
            return $"LOB({from})를 문자열({to}) 열에 넣어 값이 잘리거나 실패할 수 있습니다.";
        }

        if (sf != tf && !(sf == Family.Text && tf == Family.Clob))
        {
            return $"형식이 달라({from} → {to}) 변환에 실패하는 행이 생길 수 있습니다.";
        }

        switch (sf)
        {
            case Family.Text or Family.Raw when s.Length is { } sl && t.Length is { } tl && tl < sl:
                return $"대상 길이({tl.ToString(CultureInfo.InvariantCulture)})가 원본({sl.ToString(CultureInfo.InvariantCulture)})보다 짧아 값이 잘리거나 실패할 수 있습니다.";
            case Family.Number when BaseName(from) == "NUMBER" && BaseName(to) == "NUMBER" && NumberNarrower(s, t):
                return $"대상 숫자 정밀도({Precision(t)})가 원본({Precision(s)})보다 작아 실패하는 행이 생길 수 있습니다.";
            default:
                return null;
        }
    }

    // 정밀도가 없으면 제한 없음이다. 정수 자릿수(정밀도 - 소수 자릿수)와 소수 자릿수 어느 쪽이든 줄면 좁아진 것이다.
    private static bool NumberNarrower(ColumnInfo s, ColumnInfo t)
    {
        var scaleNarrower = t.Scale is { } ts && ts < (s.Scale ?? 0);
        if (t.Precision is not { } tp)
        {
            return scaleNarrower;
        }

        if (s.Precision is not { } sp)
        {
            return true;
        }

        return tp - (t.Scale ?? 0) < sp - (s.Scale ?? 0) || scaleNarrower;
    }

    private static string Precision(ColumnInfo c)
        => c.Precision is null
            ? "NUMBER"
            : $"NUMBER({c.Precision.Value.ToString(CultureInfo.InvariantCulture)},{(c.Scale ?? 0).ToString(CultureInfo.InvariantCulture)})";

    private static Family FamilyOf(string dataType)
    {
        var name = BaseName(dataType);
        return name switch
        {
            "VARCHAR2" or "VARCHAR" or "CHAR" or "NVARCHAR2" or "NCHAR" => Family.Text,
            "CLOB" or "NCLOB" => Family.Clob,
            "NUMBER" or "FLOAT" or "BINARY_FLOAT" or "BINARY_DOUBLE" => Family.Number,
            "RAW" => Family.Raw,
            "BLOB" => Family.Blob,
            "DATE" => Family.DateTime,
            _ when name.StartsWith("TIMESTAMP", StringComparison.Ordinal) => Family.DateTime,
            _ when name.StartsWith("INTERVAL DAY", StringComparison.Ordinal) => Family.IntervalDs,
            _ when name.StartsWith("INTERVAL YEAR", StringComparison.Ordinal) => Family.IntervalYm,
            _ => Family.Other,
        };
    }

    // 괄호 안(정밀도·길이)을 지우고 공백을 하나로 줄인 대문자 형식 이름.
    private static string BaseName(string dataType)
    {
        var sb = new System.Text.StringBuilder(dataType.Length);
        var depth = 0;
        foreach (var ch in dataType)
        {
            switch (ch)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    break;
                default:
                    if (depth == 0)
                    {
                        sb.Append(char.ToUpperInvariant(ch));
                    }

                    break;
            }
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
