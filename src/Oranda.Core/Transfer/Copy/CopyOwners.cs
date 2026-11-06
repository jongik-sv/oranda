namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 규칙마다의 원본·대상 계정(묶음 B). 작업의 <see cref="CopyJob.SourceOwner"/>·<see cref="CopyJob.TargetOwner"/> 는 「기본 계정」이다:
/// 규칙의 계정이 비면 그 값을 쓰고, 표 이름만 입력했을 때 먼저 찾는 계정이며, 대상 계정 기본값(<see cref="MapTarget"/>)의 기준이다.
/// <para>
/// 규칙 키(<see cref="Key(CopyJob, TableRule)"/>)는 작업 안에서 규칙 하나를 가리킨다. 기본 원본 계정의 표는 이름만(<c>ORDERS</c>),
/// 다른 계정의 표는 <c>계정.표</c>(<c>APP2.ORDERS</c>)다. 점·큰따옴표 든 이름은 그 마디만 따옴표로 감싼다(<c>"X.Y"</c>, <c>X."A.B"</c>).
/// 그래서 계정 하나짜리 작업(예전 작업)의 키는 (점 든 이름이 아니면) 예전 표 이름과 같다.
/// 점검 항목·건수 어림·트리거 끄기 결정·진행·표 결과·실패 행·실행 기록의 표 이름은 모두 이 키다.
/// </para>
/// </summary>
public static class CopyOwners
{
    /// <summary>규칙의 원본 계정. 비었으면 작업 기본 원본 계정.</summary>
    public static string Source(CopyJob job, TableRule rule)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(rule);
        return string.IsNullOrEmpty(rule.SourceOwner) ? job.SourceOwner : rule.SourceOwner;
    }

    /// <summary>
    /// 규칙의 대상 계정. 비었으면 그 규칙 원본 계정의 대상 기본값(<see cref="MapTarget"/>): 기본 원본 계정의 표면 작업 기본 대상 계정,
    /// 다른 계정의 표면 원본 계정 이름 그대로. 작업 밖 부모(<see cref="ParentTarget"/>)와 같은 규칙이다.
    /// </summary>
    public static string Target(CopyJob job, TableRule rule)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(rule);
        return string.IsNullOrEmpty(rule.TargetOwner) ? MapTarget(job, Source(job, rule)) : rule.TargetOwner;
    }

    /// <summary>
    /// 새로 더하는 표의 대상 계정 기본값(결정 B-1 a): 원본 계정이 작업 기본 원본 계정과 같으면 작업 기본 대상 계정,
    /// 아니면(시노님으로 다른 계정의 표가 나온 때 등) 원본 계정 이름 그대로.
    /// </summary>
    public static string MapTarget(CopyJob job, string sourceOwner)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(sourceOwner);
        return string.Equals(sourceOwner, job.SourceOwner, StringComparison.Ordinal) ? job.TargetOwner : sourceOwner;
    }

    /// <summary>
    /// 규칙 키. 기본 원본 계정의 표는 이름만, 다른 계정이면 <c>계정.표</c>.
    /// 계정이나 표 이름에 점·큰따옴표가 들어 있으면 그 부분만 큰따옴표로 감싼다(안의 큰따옴표는 두 번):
    /// 기본 계정의 표 <c>X.Y</c> 는 <c>"X.Y"</c>, 계정 X 의 표 Y 는 <c>X.Y</c>, 계정 X 의 표 <c>A.B</c> 는 <c>X."A.B"</c> 라 서로 겹치지 않는다.
    /// 보통 이름(점·따옴표 없음)의 키는 예전과 같다. 나누기는 <see cref="SplitKey"/>.
    /// </summary>
    public static string Key(CopyJob job, TableRule rule) => Key(job, Source(job, rule), rule.Table);

    /// <inheritdoc cref="Key(CopyJob, TableRule)"/>
    public static string Key(CopyJob job, string sourceOwner, string table)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(table);
        return string.IsNullOrEmpty(sourceOwner) || string.Equals(sourceOwner, job.SourceOwner, StringComparison.Ordinal)
            ? KeyPart(table)
            : KeyPart(sourceOwner) + "." + KeyPart(table);
    }

    /// <summary>
    /// 규칙 키를 계정·표로 나눈다(<see cref="Key(CopyJob, TableRule)"/> 의 거꾸로). 계정이 없는 키(기본 원본 계정의 표)면 Owner 가 null.
    /// 따옴표가 짝이 맞지 않는 등 키 꼴이 아니면 null.
    /// </summary>
    public static (string? Owner, string Table)? SplitKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var parts = new List<string>();
        var i = 0;
        while (true)
        {
            if (ReadKeyPart(key, ref i) is not { } part)
            {
                return null;
            }

            parts.Add(part);
            if (i == key.Length)
            {
                break;
            }

            if (key[i] != '.' || parts.Count == 2)
            {
                return null;
            }

            i++;
        }

        return parts.Count == 1 ? (null, parts[0]) : (parts[0], parts[1]);
    }

    /// <summary>
    /// 계정·표를 겹치지 않는 문자열 하나로(<c>계정.표</c>, 점·큰따옴표 든 마디는 따옴표로 감싼다). 계정이 늘 붙는다는 점만 규칙 키와 다르다.
    /// FK 부모 찾기·같은 표 비교처럼 계정·표 쌍을 사전 키로 쓸 때 쓴다(표시용 이름은 <see cref="SourceName"/>·<see cref="TargetName"/>).
    /// </summary>
    public static string QualifiedKey(string owner, string table)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(table);
        return KeyPart(owner) + "." + KeyPart(table);
    }

    /// <summary>키의 한 마디: 점·큰따옴표가 있으면 큰따옴표로 감싼다.</summary>
    internal static string KeyPart(string name)
        => name.Contains('.', StringComparison.Ordinal) || name.Contains('"', StringComparison.Ordinal)
            ? "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : name;

    // i 에서 한 마디를 읽는다. 따옴표 마디면 닫는 따옴표 뒤, 아니면 다음 점(또는 끝)까지. 빈 마디·잘못된 따옴표면 null.
    private static string? ReadKeyPart(string key, ref int i)
    {
        if (i < key.Length && key[i] == '"')
        {
            var sb = new System.Text.StringBuilder();
            for (var j = i + 1; j < key.Length; j++)
            {
                if (key[j] != '"')
                {
                    sb.Append(key[j]);
                    continue;
                }

                if (j + 1 < key.Length && key[j + 1] == '"')
                {
                    sb.Append('"');
                    j++;
                    continue;
                }

                i = j + 1;
                return sb.Length == 0 ? null : sb.ToString();
            }

            return null;
        }

        var end = key.IndexOf('.', i);
        if (end < 0)
        {
            end = key.Length;
        }

        var plain = key[i..end];
        if (plain.Length == 0 || plain.Contains('"', StringComparison.Ordinal))
        {
            return null;
        }

        i = end;
        return plain;
    }

    /// <summary>원본 표 전체 이름(<c>계정.표</c>, 표시용).</summary>
    public static string SourceName(CopyJob job, TableRule rule) => Source(job, rule) + "." + rule.Table;

    /// <summary>대상 표 전체 이름(<c>계정.표</c>). 대상 키가 같은 규칙이 둘이면 오류다(<see cref="Problems"/>).</summary>
    public static string TargetName(CopyJob job, TableRule rule) => Target(job, rule) + "." + rule.Table;

    /// <summary>키로 규칙을 찾는다. 없으면 null.</summary>
    public static TableRule? Find(CopyJob job, string key)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.Tables.FirstOrDefault(r => string.Equals(Key(job, r), key, StringComparison.Ordinal));
    }

    /// <summary>대상 계정·표로 규칙을 찾는다(제외 규칙 포함). 없으면 null.</summary>
    public static TableRule? FindByTarget(CopyJob job, string targetOwner, string table)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.Tables.FirstOrDefault(r => string.Equals(r.Table, table, StringComparison.Ordinal)
                                              && string.Equals(Target(job, r), targetOwner, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>원본 계정·표로 규칙을 찾는다(제외 규칙 포함). 없으면 null.</summary>
    public static TableRule? FindBySource(CopyJob job, string sourceOwner, string table)
        => Find(job, Key(job, sourceOwner, table));

    /// <summary>부모 키 규칙의 부모 원본 계정. 비었으면 자식 표의 원본 계정.</summary>
    public static string ParentSource(CopyJob job, TableRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var link = rule.Parent ?? throw new InvalidOperationException($"{rule.Table}: 부모 키 규칙에 부모 연결이 없다.");
        return string.IsNullOrEmpty(link.ParentOwner) ? Source(job, rule) : link.ParentOwner;
    }

    /// <summary>부모 표의 규칙 키(부모가 작업 밖이어도 같은 꼴로 만든다).</summary>
    public static string ParentKey(CopyJob job, TableRule rule) => Key(job, ParentSource(job, rule), rule.Parent!.ParentTable);

    /// <summary>작업 안의 부모 규칙(제외 규칙 포함). 작업 밖이면 null.</summary>
    public static TableRule? Parent(CopyJob job, TableRule rule) => rule.Parent is null ? null : Find(job, ParentKey(job, rule));

    /// <summary>
    /// 부모 표의 대상 계정: 작업 안이면(제외 규칙 포함) 그 규칙의 대상 계정, 밖이면 <see cref="MapTarget"/>.
    /// 부모 규칙의 대상 계정이 비었으면 <see cref="Target"/> 도 <see cref="MapTarget"/> 이라, 계정을 적지 않은 부모 규칙(제외 규칙)을 지우든 남기든 같은 계정이 나온다.
    /// </summary>
    public static string ParentTarget(CopyJob job, TableRule rule)
        => Parent(job, rule) is { } parent ? Target(job, parent) : MapTarget(job, ParentSource(job, rule));

    /// <summary>시퀀스 계정. 비었으면 그 표 규칙의 대상 계정, 표 규칙이 없으면 작업 기본 대상 계정.</summary>
    public static string SequenceOwner(CopyJob job, SequenceRule sequence)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(sequence);
        if (!string.IsNullOrEmpty(sequence.Owner))
        {
            return sequence.Owner;
        }

        return Find(job, sequence.Table) is { } rule ? Target(job, rule) : job.TargetOwner;
    }

    /// <summary>
    /// 규칙 사이의 모순(DB 를 보지 않고 알 수 있는 것). 문장 하나씩, 없으면 빈 목록.
    /// 원본 키가 같은 규칙 둘(같은 표를 두 번), 대상 계정·표가 같은 규칙 둘(한 표를 두 규칙이 지우고 넣음)은 오류다. 제외 규칙도 센다.
    /// </summary>
    public static IReadOnlyList<string> Problems(CopyJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var problems = new List<string>();
        foreach (var g in job.Tables.GroupBy(r => Key(job, r), StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"{g.Key}: 같은 원본 표의 규칙이 {g.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)}개 있습니다. 하나만 남기세요.");
        }

        foreach (var g in job.Tables.GroupBy(r => QualifiedKey(Target(job, r), r.Table), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"대상 표 {TargetName(job, g.First())} 에 쓰는 규칙이 여럿입니다({string.Join(", ", g.Select(r => Key(job, r)))}). 대상 계정을 바꾸거나 하나만 남기세요.");
        }

        return problems;
    }

    /// <summary>
    /// 원본과 대상이 같은 DB 일 때 대상 정리가 원본 행을 지우는 규칙: 쓰는 대상 표(제외 아닌 규칙)가 읽는 원본 표
    /// (제외 아닌 규칙의 원본 표, 부모 키 규칙이 읽는 부모 원본 표)와 같은 계정·표인 것. 같은 DB 인지는 부른 쪽이 정한다.
    /// 결과는 (규칙 키, 겹치는 표 <c>계정.표</c>). 계정 이름은 대소문자를 가리지 않고 비교한다(막는 쪽).
    /// </summary>
    public static IReadOnlyList<(string Key, string Table)> TargetsReadAsSource(CopyJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var active = job.Tables.Where(r => r.Kind != RuleKind.Exclude).ToList();
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in active)
        {
            read.Add(QualifiedKey(Source(job, rule), rule.Table));
            if (rule.Kind == RuleKind.ParentKey && rule.Parent is { } link)
            {
                read.Add(QualifiedKey(ParentSource(job, rule), link.ParentTable));
            }
        }

        return [.. active.Where(r => read.Contains(QualifiedKey(Target(job, r), r.Table))).Select(r => (Key(job, r), TargetName(job, r)))];
    }

    /// <summary>제외 아닌 규칙의 대상 계정 가운데 <paramref name="sessionUser"/>(대상 접속 계정)가 아닌 것(작업 순서, 겹침 없음).</summary>
    public static IReadOnlyList<string> OtherTargetOwners(CopyJob job, string sessionUser)
    {
        ArgumentNullException.ThrowIfNull(job);
        return [.. job.Tables.Where(r => r.Kind != RuleKind.Exclude).Select(r => Target(job, r))
            .Where(o => !string.Equals(o, sessionUser, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
