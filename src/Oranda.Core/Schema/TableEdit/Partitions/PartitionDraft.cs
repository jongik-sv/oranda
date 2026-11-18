using Oranda.Core.Connection;
using Oranda.Core.Metadata;

namespace Oranda.Core.Schema.TableEdit.Partitions;

/// <summary>파티션 방식(설계 18.4). INTERVAL 은 범위의 자동 추가라 <see cref="PartitionScheme.Interval"/> 로 따로 둔다.</summary>
public enum PartitionMethod
{
    Range,
    List,
    Hash,
}

/// <summary>파티션 문장 종류(방언 지원 판정·검사 입력). 값을 더할 때는 끝에 덧붙인다.</summary>
public enum PartitionOperation
{
    /// <summary><c>ADD PARTITION</c>(범위는 마지막 경계 뒤만).</summary>
    Add,

    /// <summary><c>DROP PARTITION … UPDATE INDEXES</c>(데이터 사라짐).</summary>
    Drop,

    /// <summary><c>RENAME PARTITION a TO b</c>.</summary>
    Rename,

    /// <summary><c>SPLIT PARTITION p AT (v) | VALUES (…) INTO (…) UPDATE INDEXES</c>.</summary>
    Split,

    /// <summary><c>MERGE PARTITIONS a, b INTO PARTITION c UPDATE INDEXES</c>.</summary>
    Merge,

    /// <summary><c>TRUNCATE PARTITION p UPDATE INDEXES</c>(데이터 사라짐).</summary>
    Truncate,

    /// <summary><c>SET INTERVAL (식)</c>·<c>SET INTERVAL ()</c>.</summary>
    SetInterval,

    /// <summary>해시 파티션 하나 줄이기(<c>COALESCE PARTITION</c>, 해시는 DROP 이 없다).</summary>
    Coalesce,

    /// <summary>일반 → 파티션 테이블(<c>MODIFY PARTITION BY … ONLINE</c>, TE-41).</summary>
    Convert,

    /// <summary>
    /// 바꾸기 때 하위 파티션 템플릿(PA-11). 방언 판정의 방식 인자 = <b>하위</b> 파티션 방식(LIST·HASH).
    /// </summary>
    Subpartition,
}

/// <summary>
/// 파티션 방식·키 열·자동 추가(INTERVAL)·하위 파티션. 키 열은 사전 표기(열 이름).
/// 하위 파티션은 둘로 나뉜다: 서버에서 읽은 것(<see cref="SubpartitionMethod"/>·<see cref="SubpartitionKeyColumns"/>, 보기만, TE-42)과
/// 일반 → 파티션 바꾸기 때 새로 만들 템플릿(<see cref="SubpartitionTemplate"/>, PA-11).
/// </summary>
public sealed record PartitionScheme(PartitionMethod Method, IReadOnlyList<string> KeyColumns)
{
    /// <summary>범위의 자동 추가 식(<c>NUMTOYMINTERVAL(1, 'MONTH')</c> 등). 없으면 null.</summary>
    public string? Interval { get; init; }

    /// <summary>하위 파티션 방식(사전 표기, 보기만). 없으면 null.</summary>
    public string? SubpartitionMethod { get; init; }

    /// <summary>하위 파티션 키 열(보기만).</summary>
    public IReadOnlyList<string> SubpartitionKeyColumns { get; init; } = [];

    /// <summary>바꾸기 때 만들 하위 파티션 템플릿(PA-11). 없으면 null. 이미 파티션된 테이블에 넣으면 차이 계산이 막는다.</summary>
    public SubpartitionTemplate? SubpartitionTemplate { get; init; }
}

/// <summary>
/// 하위 파티션 템플릿(PA-11, 바꾸기 때만). LIST = <see cref="Rows"/>(이름·값 목록), HASH = <see cref="HashCount"/>.
/// 문장: <c>SUBPARTITION BY LIST (c) SUBPARTITION TEMPLATE (SUBPARTITION n VALUES (…), …)</c> / <c>SUBPARTITION BY HASH (c) SUBPARTITIONS n</c>.
/// </summary>
public sealed record SubpartitionTemplate(PartitionMethod Method, IReadOnlyList<string> KeyColumns)
{
    /// <summary>LIST 템플릿 줄(편집 순서).</summary>
    public IReadOnlyList<SubpartitionTemplateRow> Rows { get; init; } = [];

    /// <summary>HASH 하위 파티션 개수(1 이상).</summary>
    public int HashCount { get; init; }
}

/// <summary>LIST 하위 파티션 템플릿 한 줄. <paramref name="Values"/> = <c>VALUES (…)</c> 의 안(<c>'A', 'B'</c>·<c>DEFAULT</c>).</summary>
public sealed record SubpartitionTemplateRow(string Name, string? Values);

/// <summary>
/// 파티션 한 줄(설계 18.4 표: 이름·상한값/값 목록·테이블스페이스·행 수). <see cref="Bound"/> 는 괄호 안 글
/// (범위 = <c>LESS THAN (…)</c> 의 안, 목록 = <c>VALUES (…)</c> 의 안, 해시 = null). 원본 줄은 사전 표기를 그대로 들고 있다.
/// </summary>
/// <param name="RowKey">줄 키(원본 = <c>p1</c>…, 새 줄 = <see cref="DraftKeys.New"/>)</param>
public sealed record PartitionDraft(string RowKey, string Name)
{
    public string? Bound { get; init; }

    /// <summary>테이블스페이스(새 줄만 문장에 씀, null = 테이블 기본).</summary>
    public string? Tablespace { get; init; }

    /// <summary>통계 행 수(보기·⚠ 문구). 모르면 null.</summary>
    public long? RowCount { get; init; }

    /// <summary>지울 줄(<c>DROP PARTITION</c>, 해시는 <c>COALESCE</c>).</summary>
    public bool IsDropped { get; init; }

    /// <summary>비울 줄(<c>TRUNCATE PARTITION</c>).</summary>
    public bool IsTruncated { get; init; }

    /// <summary>「합치기」로 이 줄을 합쳐 넣은 줄의 키(<c>MERGE PARTITIONS 이 줄, 그 줄 INTO PARTITION 그 줄의 이름</c>). 아니면 null.</summary>
    public string? MergedInto { get; init; }

    /// <summary>「나누기」로 이 새 줄을 떼어 낸 원본 줄의 키. 범위에서 비우면 줄 위치로 정한다(바로 다음 원본 줄). 원본 줄은 null.</summary>
    public string? SplitFrom { get; init; }
}

/// <summary>테이블 하나의 파티션 편집 모델. <see cref="Scheme"/> 이 null 이면 파티션 테이블이 아니다.</summary>
public sealed record PartitionTableDraft(PartitionScheme? Scheme, IReadOnlyList<PartitionDraft> Partitions)
{
    public static PartitionTableDraft None { get; } = new(null, []);

    /// <summary>
    /// 바꾸기 때 LOCAL 로 만들 인덱스 이름(PA-12, 소유자 = 테이블 소유자). 한 문장 바꾸기 = <c>UPDATE INDEXES (ix LOCAL, …)</c>,
    /// 다시 만들기 스크립트 = 새 표의 <c>CREATE INDEX … LOCAL</c>. 비면 서버 기본(Oracle = 접두 인덱스는 LOCAL, 나머지 GLOBAL).
    /// </summary>
    public IReadOnlyList<string> LocalIndexes { get; init; } = [];

    /// <summary>
    /// 테이블 정보 → 원본 모델. 파티션 테이블인데 정보가 없으면(못 읽음·파티션 정보 이전의 캐시) null — 「파티션 아님」으로 보면
    /// 이미 파티션된 테이블에 일반 → 파티션 바꾸기가 나갈 수 있어 편집하지 않게 한다.
    /// </summary>
    public static PartitionTableDraft? FromTable(TableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table.IsPartitioned && table.Partitioning is null ? null : FromInfo(table.Partitioning);
    }

    /// <summary>
    /// 공급자가 읽은 파티션 정보 → 원본 모델(줄 키 <c>p1</c>…, 위치 순). 파티션 테이블이 아니면 <see cref="None"/>.
    /// 방식 글이 RANGE·LIST·HASH 가 아니면(시스템·참조 등) null 을 돌려 편집하지 않게 한다.
    /// </summary>
    public static PartitionTableDraft? FromInfo(PartitioningInfo? info)
    {
        if (info is null)
        {
            return None;
        }

        PartitionMethod? method = info.Method.Trim().ToUpperInvariant() switch
        {
            "RANGE" => PartitionMethod.Range,
            "LIST" => PartitionMethod.List,
            "HASH" => PartitionMethod.Hash,
            _ => null,
        };
        if (method is null)
        {
            return null;
        }

        var scheme = new PartitionScheme(method.Value, info.KeyColumns)
        {
            Interval = string.IsNullOrWhiteSpace(info.Interval) ? null : info.Interval,
            SubpartitionMethod = info.SubpartitionMethod is null or "NONE" ? null : info.SubpartitionMethod,
            SubpartitionKeyColumns = info.SubpartitionKeyColumns,
        };
        return new PartitionTableDraft(scheme, [.. info.Partitions.OrderBy(p => p.Position).Select((p, i) => new PartitionDraft("p" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), p.Name)
        {
            Bound = p.Bound,
            Tablespace = p.Tablespace,
            RowCount = p.NumRows,
        })]);
    }
}

/// <summary>파티션 방언 기본 규칙(Oracle). <see cref="ITableEditDialect"/> 의 기본 구현이 부른다.</summary>
public static class PartitionDefaults
{
    /// <summary>Oracle 12.2 이상이면 <c>MODIFY PARTITION BY … ONLINE</c> 한 문장으로 바꾼다(TE-41). 모르면 false.</summary>
    public static bool SupportsOnlinePartitionConversion(ServerInfo? server)
    {
        if (server is not { Dbms: Dbms.Oracle })
        {
            return false;
        }

        var major = server.MajorVersion;
        if (major != 12)
        {
            return major > 12;
        }

        var parts = server.Version.Split('.');
        return parts.Length > 1 && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minor) && minor >= 2;
    }
}
