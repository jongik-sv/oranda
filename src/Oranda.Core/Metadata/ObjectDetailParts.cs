using System.Diagnostics;
using System.Globalization;

namespace Oranda.Core.Metadata;

/// <summary>
/// 객체 상세의 구역 종류(DL-1). 탭 하나를 읽는 단위다. <see cref="Table"/> 은 <see cref="IMetadataProvider.GetTableAsync"/> 한 번으로 만드는
/// 설명·열·제약·인덱스·저장 묶음, <see cref="Whole"/> 은 상세 전체(구역별로 나눠 읽지 않는 공급자·Java 객체).
/// </summary>
public enum DetailPart
{
    Whole,
    General,
    Table,
    Triggers,
    ReferencedBy,
    Partitions,
    Stats,
    Size,
    Privileges,
    Java,
    Dependencies,
}

/// <summary>상세 탭 머리 하나. <see cref="Section"/> 이 있으면 이미 읽은 것(열·인덱스처럼 캐시로 바로 만든 구역), 없으면 그 탭을 고를 때 <see cref="Part"/> 를 읽는다.</summary>
public sealed record DetailTab(string Title, DetailPart Part, DetailSection? Section = null);

/// <summary>상세 탭 목록(<see cref="IMetadataProvider.GetDetailOutlineAsync"/>). 순서는 <see cref="IMetadataProvider.GetObjectDetailAsync"/> 의 구역 순서와 같다.</summary>
public sealed record ObjectDetailOutline(DbObjectRef Target, IReadOnlyList<DetailTab> Tabs)
{
    /// <summary>이 객체를 만든 소스 객체(<see cref="ObjectDetail.SourceObject"/> 와 같다). 없으면 null.</summary>
    public DbObjectRef? SourceObject { get; init; }

    /// <summary>다 읽은 상세를 탭 목록으로(모든 탭이 이미 읽힘).</summary>
    public static ObjectDetailOutline FromDetail(ObjectDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new ObjectDetailOutline(detail.Target, [.. detail.Sections.Select(s => new DetailTab(s.Title, DetailPart.Whole, s))])
        {
            SourceObject = detail.SourceObject,
        };
    }
}

/// <summary>Oracle·Tibero 가 함께 쓰는 상세 구역 차례·제목·시간 기록(DL-1).</summary>
public static class ObjectDetailParts
{
    /// <summary>열·제약·인덱스를 가진 객체 유형(<see cref="IMetadataProvider.GetTableAsync"/> 로 읽는다).</summary>
    public static bool IsTableLike(string objectType) => objectType is "TABLE" or "VIEW" or "MATERIALIZED VIEW";

    /// <summary>구역 하나의 탭 제목(<see cref="DetailPart.Table"/>·<see cref="DetailPart.Whole"/>·<see cref="DetailPart.Java"/> 는 여러 구역이라 null).</summary>
    public static string? TitleOf(DetailPart part) => part switch
    {
        DetailPart.General => "일반",
        DetailPart.Triggers => "트리거",
        DetailPart.ReferencedBy => "참조됨",
        DetailPart.Partitions => "파티션",
        DetailPart.Stats => "통계",
        DetailPart.Size => "크기",
        DetailPart.Privileges => "권한",
        DetailPart.Dependencies => "의존",
        _ => null,
    };

    /// <summary>
    /// 표 묶음(<see cref="DetailPart.Table"/>) 뒤에 오는 구역 차례(유형별). 상세 전체 읽기와 탭 목록이 같은 차례를 쓴다.
    /// <paramref name="partitioned"/> 가 false 면 파티션 구역은 뺀다(null = 모름: 넣는다).
    /// </summary>
    public static IReadOnlyList<DetailPart> TailParts(string objectType, bool? partitioned = null)
    {
        ArgumentNullException.ThrowIfNull(objectType);
        var parts = new List<DetailPart>();
        if (objectType is "TABLE" or "VIEW")
        {
            parts.Add(DetailPart.Triggers); // 뷰는 INSTEAD OF 트리거
        }

        if (objectType == "TABLE")
        {
            parts.Add(DetailPart.ReferencedBy);
            if (partitioned != false)
            {
                parts.Add(DetailPart.Partitions);
            }

            parts.Add(DetailPart.Stats);
        }

        if (objectType is "TABLE" or "MATERIALIZED VIEW" or "INDEX")
        {
            parts.Add(DetailPart.Size);
        }

        parts.Add(DetailPart.Privileges);
        parts.Add(DetailPart.Dependencies);
        return parts;
    }

    /// <summary>
    /// 질의 없이 만드는 탭 목록: 「일반」, 이미 읽은 표 구역(<paramref name="tableSections"/>), 나머지 구역 머리. 파티션 탭은 <paramref name="partitioned"/> 일 때만.
    /// </summary>
    public static ObjectDetailOutline Outline(DbObjectRef obj, IReadOnlyList<DetailSection> tableSections, bool partitioned)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(tableSections);
        var tabs = new List<DetailTab> { new("일반", DetailPart.General) };
        tabs.AddRange(tableSections.Select(s => new DetailTab(s.Title, DetailPart.Table, s)));
        tabs.AddRange(TailParts(obj.ObjectType, partitioned).Select(p => new DetailTab(TitleOf(p)!, p)));
        return new ObjectDetailOutline(obj, tabs);
    }

    /// <summary>구역 하나를 읽고 걸린 시간·구역 수를 Trace 한 줄로 남긴다(실패도 이유와 함께 남기고 그대로 던진다).</summary>
    public static async Task<IReadOnlyList<DetailSection>> TimedAsync(string dbKey, DbObjectRef obj, DetailPart part, Func<Task<IReadOnlyList<DetailSection>>> read)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(read);
        var timer = Stopwatch.StartNew();
        try
        {
            var sections = await read().ConfigureAwait(false);
            Trace.TraceInformation(string.Create(CultureInfo.InvariantCulture,
                $"객체 상세 구역 읽기 ({dbKey}, {obj.Owner}.{obj.Name} {obj.ObjectType}, {part}): 구역 {sections.Count}개 {timer.Elapsed.TotalSeconds:0.00}초"));
            return sections;
        }
        catch (Exception ex)
        {
            Trace.TraceInformation(string.Create(CultureInfo.InvariantCulture,
                $"객체 상세 구역 읽기 ({dbKey}, {obj.Owner}.{obj.Name} {obj.ObjectType}, {part}): 실패 {timer.Elapsed.TotalSeconds:0.00}초({ex.GetType().Name})"));
            throw;
        }
    }
}
