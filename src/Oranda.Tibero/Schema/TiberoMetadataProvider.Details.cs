using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>객체 상세(Oracle 과 같은 구역 제목·열·순서)·이름 풀이 한 단계. DDL 추출은 뒤 T3 단계 몫이라 아직 막아 둔다.</summary>
internal sealed partial class TiberoMetadataProvider
{
    /// <summary><see cref="GetDdlAsync"/> 가 던지는 이유(화면은 예외 메시지를 DDL 칸·상태 줄에 보인다).</summary>
    internal const string DdlNotSupported = "Tibero DDL 추출은 아직 지원하지 않습니다(T3 DDL 단계).";

    /// <summary>열·제약·인덱스를 가진 객체 유형(<see cref="GetTableAsync"/> 로 읽는다).</summary>
    private static readonly HashSet<string> TableLike = new(StringComparer.Ordinal) { "TABLE", "VIEW", "MATERIALIZED VIEW" };

    /// <summary>
    /// 아직 지원하지 않는다: <see cref="NotSupportedException"/>. 부르는 쪽(객체 상세 DDL 탭·속성 창·DDL 일괄 추출·열 순서 바꾸기·AI 뷰 참조 풀이)은
    /// 모두 예외를 잡아 메시지를 보이거나 그 항목만 실패로 적는다. Tibero 의 DBMS_METADATA 는 TABLE 만 받아(카탈로그 <c>meta.ddl</c> 머리말) 사전 뷰로 만드는 일이 따로 필요하다.
    /// </summary>
    public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(o);
        return Task.FromException<string>(new NotSupportedException(DdlNotSupported));
    }

    public async Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);

        var type = obj.ObjectType;
        var sections = new List<DetailSection>();
        var objectBinds = ObjectBinds(obj.Owner, obj.Name, type);
        var tableBinds = TiberoCatalogQuery.TableBinds(obj);
        var features = s.Server.Features;

        var general = await RunAsync(s, "meta.detail_general", objectBinds, ct).ConfigureAwait(false);
        if (general.Count > 0)
        {
            var g = general[0];
            sections.Add(new DetailSection("일반", ["항목", "값"],
            [
                ["유형", Str(g[0])],
                ["상태", Str(g[1])],
                ["생성", g[2]],
                ["마지막 DDL", g[3]],
                // Tibero 에는 editionable 속성이 없다(카탈로그는 NULL): 다른 미지원 칸처럼 이유를 보인다.
                ["편집 가능(editionable)", features.Release12Columns ? Str(g[4]) : features.UnavailableReason],
                ["객체 ID", Str(g[5])],
            ]));
        }

        if (TableLike.Contains(type))
        {
            var table = await GetTableAsync(s, obj, ct).ConfigureAwait(false);
            AddTableSections(sections, table, features);
        }

        if (type is "TABLE" or "VIEW")
        {
            // 뷰는 INSTEAD OF 트리거가 같은 쿼리로 나온다.
            var triggers = await TryRunAsync(s, "meta.detail_triggers", tableBinds, ct).ConfigureAwait(false);
            if (triggers is { Count: > 0 })
            {
                sections.Add(new DetailSection("트리거", ["이름", "유형", "이벤트", "상태", "WHEN 조건", "소유자"],
                    [.. triggers.Select(r => (IReadOnlyList<object?>)[Str(r[0]), Str(r[1]), Str(r[2]), Str(r[3]), Str(r[4]), Str(r[5])])]));
            }
        }

        if (string.Equals(type, "TABLE", StringComparison.Ordinal))
        {
            var referencedBy = await TryRunAsync(s, "meta.detail_referenced_by", tableBinds, ct).ConfigureAwait(false);
            if (referencedBy is { Count: > 0 })
            {
                sections.Add(new DetailSection("참조됨", ["소유자", "테이블", "외래키", "열", "참조 키", "삭제 규칙", "상태"],
                    [.. referencedBy.Select(r => (IReadOnlyList<object?>)[Str(r[0]), Str(r[1]), Str(r[2]), Str(r[3]), Str(r[4]), Str(r[5]), Str(r[6])])]));
            }

            var partitions = await RunAsync(s, "meta.detail_partitions", tableBinds, ct).ConfigureAwait(false);
            if (partitions.Count > 0)
            {
                sections.Add(new DetailSection("파티션",
                    ["위치", "이름", "방식", "하위 방식", "경계값", "테이블스페이스", "행 수", "마지막 분석"],
                    [.. partitions.Select(r => (IReadOnlyList<object?>)[Int(r[0]), Str(r[1]), Str(r[2]), Str(r[3]), Str(r[4])?.Trim(), Str(r[5]), Long(r[6]), r[7]])]));
            }

            var stats = await RunAsync(s, "meta.detail_stats", tableBinds, ct).ConfigureAwait(false);
            if (stats.Count > 0)
            {
                var r = stats[0];
                sections.Add(new DetailSection("통계", ["항목", "값"],
                [
                    ["행 수", Long(r[0])],
                    ["블록 수", Long(r[1])],
                    ["평균 행 길이", Long(r[2])],
                    ["마지막 분석", r[3]],
                    ["샘플 크기", Long(r[4])],
                    ["파티션 테이블", Str(r[5])],
                    ["임시 테이블", Str(r[6])],
                    ["압축", Str(r[7])],
                    ["테이블스페이스", Str(r[8])],
                ]));
            }
        }

        if (type is "TABLE" or "MATERIALIZED VIEW" or "INDEX")
        {
            // dba 권한이 있으면 dba_segments, 없으면 자기 스키마일 때만 user_segments. 못 읽으면 크기 절을 빼고 계속한다.
            var sizes = await TryRunAsync(s, s.Server.Capabilities.CanQueryDictionary ? "meta.detail_size" : "meta.detail_size_user", tableBinds, ct)
                .ConfigureAwait(false);
            if (sizes is { Count: > 0 })
            {
                var total = sizes.Sum(r => Long(r[2]) ?? 0);
                sections.Add(new DetailSection("크기", ["세그먼트", "종류", "바이트", "익스텐트"],
                [
                    .. sizes.Select(r => (IReadOnlyList<object?>)[Str(r[0]), Str(r[1]), Long(r[2]), Long(r[3])]),
                    ["합계", null, total, null],
                ]));
            }
        }

        var privileges = await RunAsync(s, s.Server.Capabilities.CanQueryDictionary ? "meta.detail_privileges" : "meta.detail_privileges_user", tableBinds, ct)
            .ConfigureAwait(false);
        if (privileges.Count > 0)
        {
            sections.Add(new DetailSection("권한", ["받는 사람", "권한", "재부여 가능", "부여자"],
                [.. privileges.Select(r => (IReadOnlyList<object?>)[Str(r[0]), Str(r[1]), Str(r[2]), Str(r[3])])]));
        }

        var deps = await RunAsync(s, "meta.detail_dependencies", objectBinds, ct).ConfigureAwait(false);
        if (deps.Count > 0)
        {
            sections.Add(new DetailSection("의존", ["방향", "소유자", "이름", "유형"],
                [.. deps.Select(r => (IReadOnlyList<object?>)[
                    string.Equals(Str(r[0]), "USES", StringComparison.Ordinal) ? "참조함" : "참조됨", Str(r[1]), Str(r[2]), Str(r[3])])]));
        }

        return new ObjectDetail(obj, sections);
    }

    /// <summary>
    /// 이름 풀이 한 단계(<c>meta.name_step</c>): 표·뷰면 <see cref="NameStep.TableOrView"/>, 시노님이면 가리키는 곳. 없거나 서버 오류(끊김 제외)면 null.
    /// Tibero 의 시노님 뷰에는 DB 링크 열이 없어 원격 시노님은 링크 없이 풀린다(카탈로그 머리말).
    /// </summary>
    public async Task<NameStep?> GetNameStepAsync(ISession s, string owner, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(name);
        var rows = await TryRunAsync(s, "meta.name_step", new Dictionary<string, object?>(StringComparer.Ordinal) { ["owner"] = owner, ["name"] = name }, ct)
            .ConfigureAwait(false);
        if (rows is not { Count: > 0 })
        {
            return null;
        }

        var row = rows[0];
        return string.Equals(Str(row[0]), "SYNONYM", StringComparison.Ordinal)
            ? NameStep.Synonym(Str(row[1]), Str(row[2]) ?? name, Str(row[3]) is { Length: > 0 } link ? link : null)
            : NameStep.TableOrView;
    }

    /// <summary>표 상세 구역(설명·열·제약·인덱스). Oracle 과 같은 제목·열·순서다. Tibero 는 <c>Release12Columns</c> 가 꺼져 있어 identity·invisible·도메인 칸을 숨긴다.</summary>
    internal static void AddTableSections(List<DetailSection> sections, TableInfo table, ServerFeatures features)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(features);
        if (!string.IsNullOrEmpty(table.Comment))
        {
            sections.Add(new DetailSection("설명", ["항목", "값"], [["테이블 주석", table.Comment]]));
        }

        sections.Add(features.Release12Columns
            ? new DetailSection("열",
                ["#", "이름", "형식", "길이", "정밀도", "소수", "NULL 허용", "기본값", "가상", "Identity", "보이지 않음", "도메인", "주석"],
                [.. ColumnOrdering.ByColumnId(table.Columns).Select(c => (IReadOnlyList<object?>)
                [
                    c.Position, c.Name, c.DataType, c.Length, c.Precision, c.Scale, c.IsNullable ? "Y" : "N", c.DefaultValue,
                    c.IsVirtual ? "Y" : null, c.IsIdentity ? "Y" : null, c.IsInvisible ? "Y" : null, c.Domain, c.Comment,
                ])])
            : new DetailSection("열",
                ["#", "이름", "형식", "길이", "정밀도", "소수", "NULL 허용", "기본값", "가상", "주석"],
                [.. ColumnOrdering.ByColumnId(table.Columns).Select(c => (IReadOnlyList<object?>)
                [
                    c.Position, c.Name, c.DataType, c.Length, c.Precision, c.Scale, c.IsNullable ? "Y" : "N", c.DefaultValue,
                    c.IsVirtual ? "Y" : null, c.Comment,
                ])]));

        if (table.Constraints.Count > 0)
        {
            sections.Add(new DetailSection("제약", ["이름", "종류", "열", "참조", "조건", "사용"],
                [.. table.Constraints.Select(c => (IReadOnlyList<object?>)
                [
                    c.Name,
                    c.Kind switch
                    {
                        ConstraintKind.PrimaryKey => "PRIMARY KEY",
                        ConstraintKind.Unique => "UNIQUE",
                        ConstraintKind.ForeignKey => "FOREIGN KEY",
                        _ => "CHECK",
                    },
                    string.Join(", ", c.Columns),
                    c.ReferencedTable is { } r ? $"{r.Owner}.{r.Name}" : null,
                    c.Condition,
                    c.IsEnabled ? "ENABLED" : "DISABLED",
                ])]));
        }

        if (table.Indexes.Count > 0)
        {
            sections.Add(new DetailSection("인덱스", ["이름", "유일", "종류", "열"],
                [.. table.Indexes.Select(i => (IReadOnlyList<object?>)[i.Name, i.IsUnique ? "UNIQUE" : "NONUNIQUE", i.IndexType, string.Join(", ", i.Columns)])]));
        }
    }

    /// <summary>객체 하나를 고르는 바인드(<c>:owner</c>, <c>:name</c>, <c>:type</c>).</summary>
    private static Dictionary<string, object?> ObjectBinds(string owner, string name, string type) =>
        new(StringComparer.Ordinal) { ["owner"] = owner, ["name"] = name, ["type"] = type };
}
