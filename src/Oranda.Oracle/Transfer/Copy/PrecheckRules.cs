using System.Globalization;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// 사전 점검의 순수 판정(DB 를 부르지 않는다). 열 목록·키 열·필요 권한을 고르는 함수는 실행기(T9)도 같이 쓴다:
/// 점검이 본 열과 실행기가 넣는 열이 달라지지 않게 한곳에서 정한다.
/// </summary>
internal static class PrecheckRules
{
    // ---- 열 목록(점검과 실행기가 공유) ----

    /// <summary>대량 실행 경로가 다루는 형식인가. 실행기가 쓰는 <see cref="BulkBinds.ToDbType"/> 와 같은 목록이다(설계 8.3).</summary>
    public static bool IsSupportedType(string dataType)
    {
        try
        {
            _ = BulkBinds.ToDbType(dataType);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 복사할 열(<see cref="CopyTablePlan.Columns"/>). 원본 열 순서, 숨김 열 포함.
    /// 가상 열(양쪽)·대상에 없는 열·<see cref="TableRule.SkipColumns"/>·지원하지 않는 형식 열은 뺀다(메모 D10).
    /// </summary>
    public static IReadOnlyList<string> SelectColumns(TableInfo source, TableInfo target, TableRule rule)
    {
        var targetColumns = target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var skip = rule.SkipColumns.ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var column in source.Columns)
        {
            if (column.IsVirtual || skip.Contains(column.Name) || !targetColumns.TryGetValue(column.Name, out var t) || t.IsVirtual)
            {
                continue;
            }

            if (IsSupportedType(column.DataType) && IsSupportedType(t.DataType))
            {
                result.Add(column.Name);
            }
        }

        return result;
    }

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

    /// <summary>원본과 대상의 열 차이. 항목의 <see cref="CheckItem.Table"/> 은 <paramref name="key"/>(규칙 키, 없으면 <c>rule.Table</c>)다.</summary>
    public static IReadOnlyList<CheckItem> CompareColumns(TableInfo source, TableInfo target, TableRule rule, string? key = null)
    {
        var table = key ?? rule.Table;
        var items = new List<CheckItem>();
        var skip = rule.SkipColumns.ToHashSet(StringComparer.Ordinal);
        var targetColumns = target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);

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

            if (!IsSupportedType(s.DataType) || !IsSupportedType(t.DataType))
            {
                var unsupported = IsSupportedType(s.DataType) ? t.DataType : s.DataType;
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

            if (NarrowerReason(s, t) is { } reason)
            {
                items.Add(new CheckItem(table, "NarrowerType", CheckLevel.Confirm, $"{table}.{s.Name}: {reason}") { Column = s.Name });
            }
        }

        // 대상에만 있는 열 중 값이 꼭 필요한 열. 원본에서 읽지 못하는 열(가상·건너뜀)도 같다.
        foreach (var t in target.Columns)
        {
            if (t.IsVirtual || t.IsIdentity || t.IsNullable || !string.IsNullOrWhiteSpace(t.DefaultValue))
            {
                continue;
            }

            var read = source.Columns.Any(s => string.Equals(s.Name, t.Name, StringComparison.Ordinal) && !s.IsVirtual && !skip.Contains(s.Name));
            if (!read)
            {
                items.Add(new CheckItem(table, "TargetRequiredColumnMissing", CheckLevel.Error,
                    $"{table}.{t.Name}: 대상에서 NOT NULL 이고 기본값이 없는데 원본에서 값을 가져올 수 없습니다.") { Column = t.Name });
            }
        }

        return items;
    }

    // ---- 마스킹(설계 8.5) ----

    /// <summary>
    /// 마스킹 규칙 점검. PK·UK 열과 부모 키 규칙의 연결 열에는 마스킹을 걸 수 없다(<c>MaskOnKey</c>).
    /// 가리는 열이 있으면 목록을 <c>MaskedColumns</c> 통과 항목으로 보여 준다. 규칙이 원본에 없는 열이나 열 형식에 못 쓰는 종류면 오류다.
    /// </summary>
    public static IReadOnlyList<CheckItem> CheckMasks(TableRule rule, TableInfo source, IReadOnlyList<string> keyColumns, string? key = null)
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

            // 복사하지 않는 열은 가릴 값도 없다.
            if (column.IsVirtual || skip.Contains(mask.Column))
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
                                          && owners.Contains(CopyOwners.Target(job, r), StringComparer.OrdinalIgnoreCase)).ToList();
        var deleting = rules.Count(r => r.Action is TargetAction.DeleteAllInsert or TargetAction.DeleteRangeInsert or TargetAction.Recreate);
        var n = rules.Count.ToString(CultureInfo.InvariantCulture);
        var what = deleting == rules.Count
            ? $"표 {n}개를 지우고 넣습니다"
            : $"표 {n}개에 씁니다(그중 {deleting.ToString(CultureInfo.InvariantCulture)}개는 지우고 넣습니다)";
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

    private static string? NarrowerReason(ColumnInfo s, ColumnInfo t)
    {
        var sf = FamilyOf(s.DataType);
        var tf = FamilyOf(t.DataType);
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
