using System.Globalization;
using Oranda.Core.Connection;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>앱 확인 수준(✔ / ⚠ / ✖). ✖ 가 하나라도 있으면 「편집기에 채우기」를 끈다.</summary>
public enum AdviceCheckLevel
{
    Ok,
    Warn,
    Stop,
}

/// <summary>
/// 앱 확인 하나(설계 4.1). 문구는 App 이 <see cref="TextKey"/> 로 찾고 <see cref="Args"/> 를 <c>{0}</c>… 에 넣는다.
/// </summary>
/// <param name="Code"><see cref="AdviceCheckCodes"/> 값</param>
/// <param name="Subject">걸린 열·인덱스·제약 이름(없으면 null)</param>
/// <param name="Args">문구에 넣을 값(비율·건수 등, 문화 무관 글). 뜻은 <see cref="AdviceCheckCodes"/> 의 code 마다 적었다</param>
public sealed record AdviceCheck(AdviceCheckLevel Level, string Code, string? Subject = null, IReadOnlyList<string>? Args = null)
{
    /// <summary>문구 키 머리.</summary>
    public const string TextKeyPrefix = "TableEdit.partition.ai.check.";

    /// <summary>문구 키(<c>TableEdit.partition.ai.check.key-missing</c>).</summary>
    public string TextKey => TextKeyPrefix + Code;
}

/// <summary>
/// 앱 확인 code(설계 4.1 표 + 스키마 밖 값·되돌림 실패·없는 인덱스). 각 code 의 수준, <see cref="AdviceCheck.Subject"/>, <see cref="AdviceCheck.Args"/> 뜻.
/// 숫자 Args 는 문화 무관 글(천 단위 쉼표 없음, 소수점 <c>.</c>).
/// </summary>
public static class AdviceCheckCodes
{
    /// <summary>✖ 키 열이 표에 없음·가상 열, 또는 키 열이 비었음. Subject = 열 이름(비었으면 null). Args 없음.</summary>
    public const string KeyMissing = "key-missing";

    /// <summary>
    /// ✖ 방식과 키 형식이 안 맞음. Subject = 열 이름. Args[0] = 형식 이름(<c>CLOB</c>) 또는 날짜형 형식(<c>YYYYMM</c>, 날마다를 못 나눌 때),
    /// INTERVAL 인데 키 열이 둘 이상이면 Subject = null, Args[0] = 키 열 수.
    /// </summary>
    public const string KeyType = "key-type";

    /// <summary>⚠ 문자열·숫자 날짜형 키 + 기간 배치 → INTERVAL 없이 경계 줄 + MAXVALUE, 월 추가 스크립트 필요. Subject = 열 이름. Args[0] = 날짜형 형식(<c>YYYYMMDD</c>).</summary>
    public const string IntervalString = "interval-string";

    /// <summary>✖ INTERVAL 후보인데 키 열에 NULL 행이 있음(ORA-14300). Subject = 열 이름. Args[0] = NULL 건수.</summary>
    public const string IntervalNull = "interval-null";

    /// <summary>⚠ INTERVAL 없는 범위인데 NULL 행 있음 → MAXVALUE 파티션으로 감. Subject = 열 이름. Args[0] = NULL 건수.</summary>
    public const string RangeNull = "range-null";

    /// <summary>✖(95% 미만)/⚠(100% 미만) 문자열·숫자 날짜 키의 형식 맞는 비율. Subject = 열 이름. Args[0] = 비율 %(<c>97.0</c>), Args[1] = 표본 행 수.</summary>
    public const string DateLikeRatio = "datelike-ratio";

    /// <summary>⚠ PK·UK 에 키 열이 다 들어 있지 않음 → 그 인덱스는 GLOBAL, DROP PARTITION 때 UPDATE INDEXES. Subject = 제약 이름. Args[0] = 제약 열(쉼표 목록).</summary>
    public const string PkGlobal = "pk-global";

    /// <summary>✔ LOCAL 로 바꿀 인덱스의 선두 열 = 키(접두 LOCAL). Subject = 인덱스 이름. Args 없음.</summary>
    public const string LocalOk = "local-ok";

    /// <summary>✖ 고유 인덱스를 LOCAL 로 하는데 키 열(하위 키 포함)을 다 포함하지 않음(ORA-14039). Subject = 인덱스 이름. Args[0] = 빠진 키 열(쉼표 목록).</summary>
    public const string LocalUnique = "local-unique";

    /// <summary>
    /// ⚠ HASH 개수가 2의 거듭제곱이 아님 / ✖ 개수가 없거나 1 미만. Subject = 그 HASH 의 키 열(쉼표 목록, 하위 파티션이면 하위 키).
    /// Args[0] = 개수(없으면 빈 글).
    /// </summary>
    public const string HashPow2 = "hash-pow2";

    /// <summary>⚠ LIST 묶음이 분포 상위 값(과 나머지)을 다 덮지 않고 DEFAULT 도 없음. Subject = 키 열. Args[0] = 안 덮인 상위 값 수, Args[1] = 안 덮인 건수(나머지 포함, 표본 기준).</summary>
    public const string ListCover = "list-cover";

    /// <summary>
    /// ✖ 범위 경계가 비었음·정렬 안 됨·중복(MAXVALUE 가 끝이 아님 포함), LIST 값이 두 번 나옴, DEFAULT 묶음이 둘 이상, LIST 묶음이 없음.
    /// Subject = null. Args[0] = 걸린 경계·값(<c>DEFAULT</c>, 비었으면 빈 글).
    /// </summary>
    public const string BoundsOrder = "bounds-order";

    /// <summary>
    /// ✖ 방언이 파티션·바꾸기·그 방식·INTERVAL 을 못 함: Args[0] = <c>partitioning</c>|<c>conversion</c>|<c>method</c>|<c>interval</c>|<c>subpartition</c>, Args[1] = 방언의 이유 문자열 키.
    /// ⚠ 한 문장 온라인 바꾸기가 안 돼 재구성 스크립트로 감: Args[0] = <c>rebuild</c>. Subject = null.
    /// </summary>
    public const string Dialect = "dialect";

    /// <summary>⚠ 행 수 &lt; 100만 또는 블록 &lt; 1만 → 이득 작음. Args[0] = 행 수, Args[1] = 블록 수(모르면 빈 글).</summary>
    public const string SmallTable = "small-table";

    /// <summary>⚠ 통계 마지막 수집이 모은 때보다 30일 넘게 전. Args[0] = 지난 날 수. 통계를 한 번도 모으지 않았으면 Args = 빈 목록.</summary>
    public const string StatsOld = "stats-old";

    /// <summary>✖ 모르는 방식·간격·배치·하위 방식 글(스키마 밖 값), 또는 방식과 맞지 않는 배치. Args[0] = 받은 글, Args[1] = <c>method</c>|<c>interval</c>|<c>layout</c>|<c>subpartition</c>.</summary>
    public const string MethodUnknown = "method-unknown";

    /// <summary>✖ 금고로 되돌릴 수 없는 자리표시자(설계 4.2 LIST). Subject = 열 이름. Args[0] = 자리표시자(<c>V9</c>).</summary>
    public const string ValueUnknown = "value-unknown";

    /// <summary>✖ LOCAL 로 하라는 인덱스가 표에 없음. Subject = 인덱스 이름. Args 없음.</summary>
    public const string IndexMissing = "index-missing";
}

/// <summary>
/// AI 후보를 근거·방언으로 결정론 확인(설계 4.1). 결과 순서 = 스키마 밖 값 → 표 4.1 순서(value-unknown 은 list-cover 뒤, index-missing 은 local-unique 앞).
/// 방언 능력은 <see cref="ITableEditDialect.UnsupportedReasonKey"/>·<see cref="ITableEditDialect.UnsupportedPartitionReasonKey"/>·
/// <see cref="ITableEditDialect.SupportsOnlinePartitionConversion"/> 로 본다. 하위 파티션 능력 속성은 아직 없어 바꾸기(<see cref="PartitionOperation.Convert"/>) 를 하위 방식으로 물어 대신한다.
/// </summary>
public static class PartitionAdviceChecker
{
    /// <summary>날짜형 키를 ✖ 로 막는 비율(미만).</summary>
    public const double DateLikeStopRatio = 0.95;

    /// <summary>통계가 오래됐다고 보는 날 수(초과).</summary>
    public const int StatsOldDays = 30;

    private enum KeyKind
    {
        Date,
        Number,
        Text,
        Lob,
        Other,
    }

    /// <summary>
    /// 후보 하나의 확인 목록. <paramref name="vault"/> = 프롬프트를 만든 금고(값을 가렸으면 LIST 자리표시자를 되돌려 list-cover·value-unknown 을 본다).
    /// null 이면 LIST 값을 받은 글 그대로 본다.
    /// </summary>
    public static IReadOnlyList<AdviceCheck> Check(PartitionCandidate candidate, PartitionEvidence evidence, ITableEditDialect dialect, ServerInfo? server, EvidenceValueVault? vault = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(dialect);

        var checks = new List<AdviceCheck>();
        var keys = candidate.KeyColumns ?? [];
        var method = AdviceMethod.ToMethod(candidate.Method);
        var layoutKind = Lower(candidate.Layout?.Kind);
        var interval = Lower(candidate.Interval);
        var sub = candidate.Subpartition;
        var subMethod = sub is null ? null : AdviceMethod.ToMethod(sub.Method);
        var subKeys = sub?.KeyColumns ?? [];

        // 스키마 밖 값
        if (method is null)
        {
            checks.Add(Stop(AdviceCheckCodes.MethodUnknown, null, candidate.Method ?? string.Empty, "method"));
        }

        var temporalInterval = interval is AdviceInterval.Month or AdviceInterval.Day or AdviceInterval.Year;
        var numberInterval = IsNumberInterval(interval);
        var noInterval = interval is null or "" or AdviceInterval.None;
        if (method == PartitionMethod.Range && !temporalInterval && !numberInterval && !noInterval)
        {
            checks.Add(Stop(AdviceCheckCodes.MethodUnknown, null, candidate.Interval!, "interval"));
        }

        if (method is { } m && !LayoutFits(m, layoutKind))
        {
            checks.Add(Stop(AdviceCheckCodes.MethodUnknown, null, candidate.Layout?.Kind ?? string.Empty, "layout"));
        }

        if (sub is not null && subMethod is not (PartitionMethod.List or PartitionMethod.Hash))
        {
            checks.Add(Stop(AdviceCheckCodes.MethodUnknown, null, sub.Method ?? string.Empty, "subpartition"));
        }

        // key-missing
        if (keys.Count == 0)
        {
            checks.Add(Stop(AdviceCheckCodes.KeyMissing, null));
        }

        foreach (var name in keys.Concat(subKeys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (evidence.Column(name) is not { IsVirtual: false })
            {
                checks.Add(Stop(AdviceCheckCodes.KeyMissing, name));
            }
        }

        var range = method == PartitionMethod.Range;
        var temporalLayout = layoutKind is AdviceLayoutKind.Monthly or AdviceLayoutKind.Yearly or AdviceLayoutKind.Daily;
        var temporal = range && (temporalInterval || temporalLayout);
        var columns = keys.Select(evidence.Column).Where(c => c is { IsVirtual: false }).Select(c => c!).ToList();

        // key-type 과 실제로 INTERVAL 을 쓰는지
        var effectiveInterval = false;
        foreach (var c in columns.Concat(subKeys.Select(evidence.Column).Where(c => c is { IsVirtual: false }).Select(c => c!)))
        {
            if (Classify(c.DataType) == KeyKind.Lob)
            {
                checks.Add(Stop(AdviceCheckCodes.KeyType, c.Name, c.DataType));
            }
        }

        if (range && (temporalInterval || numberInterval) && keys.Count > 1)
        {
            checks.Add(Stop(AdviceCheckCodes.KeyType, null, Inv($"{keys.Count}")));
        }
        else if (range && columns.Count == 1 && Classify(columns[0].DataType) != KeyKind.Lob)
        {
            var c = columns[0];
            var kind = Classify(c.DataType);
            var dateLike = kind is KeyKind.Text or KeyKind.Number ? c.DateLike : null;
            if (numberInterval)
            {
                if (kind != KeyKind.Number)
                {
                    checks.Add(Stop(AdviceCheckCodes.KeyType, c.Name, c.DataType));
                }
            }
            else if (temporal && kind != KeyKind.Date)
            {
                if (dateLike is null)
                {
                    checks.Add(Stop(AdviceCheckCodes.KeyType, c.Name, c.DataType));
                }
                else
                {
                    if (layoutKind == AdviceLayoutKind.Daily && dateLike.Format == DateLikeFormat.Yyyymm)
                    {
                        checks.Add(Stop(AdviceCheckCodes.KeyType, c.Name, FormatName(dateLike.Format)));
                    }

                    // interval-string
                    checks.Add(Warn(AdviceCheckCodes.IntervalString, c.Name, FormatName(dateLike.Format)));
                }
            }

            effectiveInterval = (temporalInterval && kind == KeyKind.Date) || (numberInterval && kind == KeyKind.Number);
        }

        // interval-null / range-null
        if (range)
        {
            foreach (var c in columns)
            {
                var nulls = NullRows(c, evidence);
                if (nulls > 0)
                {
                    checks.Add(effectiveInterval
                        ? Stop(AdviceCheckCodes.IntervalNull, c.Name, Inv($"{nulls}"))
                        : Warn(AdviceCheckCodes.RangeNull, c.Name, Inv($"{nulls}")));
                }
            }
        }

        // datelike-ratio
        if (temporal && !numberInterval && columns.Count == 1 && Classify(columns[0].DataType) is KeyKind.Text or KeyKind.Number && columns[0].DateLike is { } like && like.MatchRatio < 1.0)
        {
            var level = like.MatchRatio < DateLikeStopRatio ? AdviceCheckLevel.Stop : AdviceCheckLevel.Warn;
            checks.Add(new AdviceCheck(level, AdviceCheckCodes.DateLikeRatio, columns[0].Name, [Inv($"{like.MatchRatio * 100:0.0}"), Inv($"{like.SampleSize}")]));
        }

        // pk-global
        var local = (candidate.LocalIndexes ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var localIndexes = local.Select(n => evidence.Indexes.FirstOrDefault(i => Same(i.Name, n))).Where(i => i is not null).Select(i => i!).ToList();
        if (keys.Count > 0)
        {
            foreach (var key in evidence.Keys.Where(k => k.Kind is EvidenceKeyKind.Primary or EvidenceKeyKind.Unique))
            {
                if (ContainsAll(key.Columns, keys) || localIndexes.Any(i => SameSet(i.Columns, key.Columns)))
                {
                    continue;
                }

                checks.Add(Warn(AdviceCheckCodes.PkGlobal, key.Name, string.Join(", ", key.Columns)));
            }
        }

        // local-ok / index-missing / local-unique
        var allKeys = keys.Concat(subKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var name in local)
        {
            var index = evidence.Indexes.FirstOrDefault(i => Same(i.Name, name));
            if (index is null)
            {
                checks.Add(Stop(AdviceCheckCodes.IndexMissing, name));
                continue;
            }

            var missing = allKeys.Where(k => !index.Columns.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            if (index.Unique && missing.Count > 0)
            {
                checks.Add(Stop(AdviceCheckCodes.LocalUnique, index.Name, string.Join(", ", missing)));
            }
            else if (keys.Count > 0 && index.Columns.Count >= keys.Count && index.Columns.Take(keys.Count).SequenceEqual(keys, StringComparer.OrdinalIgnoreCase))
            {
                checks.Add(new AdviceCheck(AdviceCheckLevel.Ok, AdviceCheckCodes.LocalOk, index.Name));
            }
        }

        // hash-pow2
        if (method == PartitionMethod.Hash)
        {
            HashCount(checks, candidate.Layout?.HashCount, keys);
        }

        if (subMethod == PartitionMethod.Hash)
        {
            HashCount(checks, sub!.HashCount, subKeys);
        }

        // list-cover / value-unknown
        if (method == PartitionMethod.List && keys.Count == 1)
        {
            ListValues(checks, keys[0], candidate.Layout?.Groups ?? [], evidence, vault);
        }

        if (subMethod == PartitionMethod.List && subKeys.Count == 1)
        {
            ListValues(checks, subKeys[0], sub!.Groups ?? [], evidence, vault);
        }

        // bounds-order
        if (range && layoutKind == AdviceLayoutKind.NumberRanges)
        {
            BoundsOrder(checks, candidate.Layout?.Bounds ?? []);
        }

        if (method == PartitionMethod.List)
        {
            ListOrder(checks, keys.Count > 0 ? keys[0] : null, candidate.Layout?.Groups ?? [], vault);
        }

        if (subMethod == PartitionMethod.List)
        {
            ListOrder(checks, subKeys.Count > 0 ? subKeys[0] : null, sub!.Groups ?? [], vault);
        }

        // dialect
        Dialect(checks, dialect, server, method, effectiveInterval, subMethod);

        // small-table / stats-old
        if ((evidence.NumRows is { } rows && rows < PartitionAdvicePrompt.SmallTableRows) || (evidence.Blocks is { } blocks && blocks < PartitionAdvicePrompt.SmallTableBlocks))
        {
            checks.Add(Warn(AdviceCheckCodes.SmallTable, null, evidence.NumRows is { } r ? Inv($"{r}") : string.Empty, evidence.Blocks is { } b ? Inv($"{b}") : string.Empty));
        }

        if (evidence.LastAnalyzed is not { } analyzed)
        {
            checks.Add(new AdviceCheck(AdviceCheckLevel.Warn, AdviceCheckCodes.StatsOld, null, []));
        }
        else if ((evidence.CollectedAt - analyzed).TotalDays > StatsOldDays)
        {
            checks.Add(Warn(AdviceCheckCodes.StatsOld, null, Inv($"{(int)Math.Floor((evidence.CollectedAt - analyzed).TotalDays)}")));
        }

        return checks;
    }

    /// <summary>✖ 가 없으면 참(채우기 단추를 켬).</summary>
    public static bool CanFill(IEnumerable<AdviceCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return !checks.Any(c => c.Level == AdviceCheckLevel.Stop);
    }

    private static void HashCount(List<AdviceCheck> checks, int? count, IReadOnlyList<string> keys)
    {
        var subject = keys.Count == 0 ? null : string.Join(", ", keys);
        if (count is not > 0)
        {
            checks.Add(Stop(AdviceCheckCodes.HashPow2, subject, count is { } n ? Inv($"{n}") : string.Empty));
        }
        else if ((count.Value & (count.Value - 1)) != 0)
        {
            checks.Add(Warn(AdviceCheckCodes.HashPow2, subject, Inv($"{count.Value}")));
        }
    }

    // 묶음이 분포 상위 값을 덮나(list-cover), 자리표시자를 되돌릴 수 있나(value-unknown).
    private static void ListValues(List<AdviceCheck> checks, string column, IReadOnlyList<ListGroup> groups, PartitionEvidence evidence, EvidenceValueVault? vault)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in groups.SelectMany(g => g.Values ?? []))
        {
            if (Unmask(column, value, vault) is { } real)
            {
                covered.Add(real);
            }
            else
            {
                checks.Add(Stop(AdviceCheckCodes.ValueUnknown, column, value));
            }
        }

        if (groups.Any(g => g.IsDefault))
        {
            return;
        }

        var dist = evidence.Distributions.FirstOrDefault(d => d.Kind == DistributionKind.TopValues && Same(d.Column, column));
        if (dist is null)
        {
            return;
        }

        var missed = dist.Buckets.Where(b => !covered.Contains(b.Label)).ToList();
        if (missed.Count > 0 || dist.RestCount > 0)
        {
            var rows = missed.Sum(b => b.Count) + dist.RestCount;
            checks.Add(Warn(AdviceCheckCodes.ListCover, column, Inv($"{missed.Count}"), Inv($"{rows}")));
        }
    }

    private static void ListOrder(List<AdviceCheck> checks, string? column, IReadOnlyList<ListGroup> groups, EvidenceValueVault? vault)
    {
        if (groups.Count == 0)
        {
            checks.Add(Stop(AdviceCheckCodes.BoundsOrder, null, string.Empty));
            return;
        }

        if (groups.Count(g => g.IsDefault) > 1)
        {
            checks.Add(Stop(AdviceCheckCodes.BoundsOrder, null, "DEFAULT"));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in groups.SelectMany(g => g.Values ?? []))
        {
            var real = column is null ? value : Unmask(column, value, vault) ?? value;
            if (!seen.Add(real))
            {
                checks.Add(Stop(AdviceCheckCodes.BoundsOrder, null, value));
            }
        }
    }

    private static void BoundsOrder(List<AdviceCheck> checks, IReadOnlyList<string> bounds)
    {
        if (bounds.Count == 0)
        {
            checks.Add(Stop(AdviceCheckCodes.BoundsOrder, null, string.Empty));
            return;
        }

        var trimmed = bounds.Select(b => (b ?? string.Empty).Trim()).ToList();
        for (var i = 0; i < trimmed.Count - 1; i++)
        {
            if (IsMaxValue(trimmed[i]))
            {
                checks.Add(Stop(AdviceCheckCodes.BoundsOrder, null, trimmed[i]));
                return;
            }
        }

        var values = trimmed.Where(b => !IsMaxValue(b)).ToList();
        var numbers = values.Select(v => decimal.TryParse(v.Trim('\''), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null).ToList();
        var numeric = numbers.All(n => n is not null);
        for (var i = 1; i < values.Count; i++)
        {
            var order = numeric ? numbers[i - 1]!.Value.CompareTo(numbers[i]!.Value) : string.CompareOrdinal(values[i - 1], values[i]);
            if (order >= 0)
            {
                checks.Add(Stop(AdviceCheckCodes.BoundsOrder, null, values[i]));
                return;
            }
        }
    }

    private static void Dialect(List<AdviceCheck> checks, ITableEditDialect dialect, ServerInfo? server, PartitionMethod? method, bool interval, PartitionMethod? subMethod)
    {
        var count = checks.Count;
        if (dialect.UnsupportedReasonKey(TableEditFeature.Partitioning, server) is { } partitioning)
        {
            checks.Add(Stop(AdviceCheckCodes.Dialect, null, "partitioning", partitioning));
        }

        if (dialect.UnsupportedReasonKey(TableEditFeature.PartitionConversion, server) is { } conversion)
        {
            checks.Add(Stop(AdviceCheckCodes.Dialect, null, "conversion", conversion));
        }

        if (method is { } m && dialect.UnsupportedPartitionReasonKey(PartitionOperation.Convert, m, server) is { } methodKey)
        {
            checks.Add(Stop(AdviceCheckCodes.Dialect, null, "method", methodKey));
        }

        if (interval && dialect.UnsupportedPartitionReasonKey(PartitionOperation.SetInterval, PartitionMethod.Range, server) is { } intervalKey)
        {
            checks.Add(Stop(AdviceCheckCodes.Dialect, null, "interval", intervalKey));
        }

        if (subMethod is { } s && s != method && dialect.UnsupportedPartitionReasonKey(PartitionOperation.Convert, s, server) is { } subKey)
        {
            checks.Add(Stop(AdviceCheckCodes.Dialect, null, "subpartition", subKey));
        }

        if (checks.Count == count && !dialect.SupportsOnlinePartitionConversion(server))
        {
            checks.Add(Warn(AdviceCheckCodes.Dialect, null, "rebuild"));
        }
    }

    private static bool LayoutFits(PartitionMethod method, string? kind) => method switch
    {
        PartitionMethod.Range => kind is AdviceLayoutKind.Monthly or AdviceLayoutKind.Yearly or AdviceLayoutKind.Daily or AdviceLayoutKind.NumberRanges,
        PartitionMethod.List => kind == AdviceLayoutKind.Values,
        _ => kind == AdviceLayoutKind.Hash,
    };

    private static bool IsNumberInterval(string? interval) =>
        interval is not null && interval.StartsWith(AdviceInterval.NumberPrefix, StringComparison.Ordinal)
        && decimal.TryParse(interval[AdviceInterval.NumberPrefix.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var step) && step > 0;

    // 형식 이름 → 키 종류. 괄호(길이·정밀도)와 뒤 말(WITH TIME ZONE 등)은 앞 낱말로 가른다.
    private static KeyKind Classify(string dataType)
    {
        var t = (dataType ?? string.Empty).Trim().ToUpperInvariant();
        var paren = t.IndexOf('(', StringComparison.Ordinal);
        var head = (paren >= 0 ? t[..paren] : t).Trim();
        if (head == "DATE" || head.StartsWith("TIMESTAMP", StringComparison.Ordinal))
        {
            return KeyKind.Date;
        }

        return head switch
        {
            "NUMBER" or "FLOAT" or "INTEGER" or "INT" or "SMALLINT" or "DECIMAL" or "NUMERIC" or "BINARY_FLOAT" or "BINARY_DOUBLE" => KeyKind.Number,
            "VARCHAR2" or "VARCHAR" or "CHAR" or "NCHAR" or "NVARCHAR2" => KeyKind.Text,
            "CLOB" or "NCLOB" or "BLOB" or "BFILE" or "LONG" or "LONG RAW" or "XMLTYPE" or "JSON" => KeyKind.Lob,
            _ => KeyKind.Other,
        };
    }

    private static long NullRows(EvidenceColumn column, PartitionEvidence evidence)
    {
        var stats = column.NumNulls ?? 0;
        var sampled = evidence.Distributions.Where(d => Same(d.Column, column.Name)).Select(d => d.NullCount ?? 0).DefaultIfEmpty(0).Max();
        return Math.Max(stats, sampled);
    }

    // 금고가 자리표시자를 줬으면 되돌린다. 금고가 비었으면(값을 보냄) 받은 글 그대로. 못 되돌리는 자리표시자 = null.
    private static string? Unmask(string column, string value, EvidenceValueVault? vault)
    {
        if (vault is not { Count: > 0 })
        {
            return value;
        }

        return vault.Unmask(column, value) ?? (EvidenceValueVault.LooksLikeToken(value) ? null : value);
    }

    private static string FormatName(DateLikeFormat format) => format switch
    {
        DateLikeFormat.Yyyymmdd => "YYYYMMDD",
        DateLikeFormat.Yyyymm => "YYYYMM",
        _ => "YYYY-MM-DD",
    };

    private static bool IsMaxValue(string bound) => string.Equals(bound, "MAXVALUE", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAll(IReadOnlyList<string> haystack, IReadOnlyList<string> needles) =>
        needles.All(n => haystack.Contains(n, StringComparer.OrdinalIgnoreCase));

    private static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.Count == b.Count && ContainsAll(a, b);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Lower(string? text) => text?.Trim().ToLowerInvariant();

    private static AdviceCheck Stop(string code, string? subject, params string[] args) => new(AdviceCheckLevel.Stop, code, subject, args.Length == 0 ? null : args);

    private static AdviceCheck Warn(string code, string? subject, params string[] args) => new(AdviceCheckLevel.Warn, code, subject, args.Length == 0 ? null : args);

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
