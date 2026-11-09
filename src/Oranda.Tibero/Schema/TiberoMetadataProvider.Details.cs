using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Tibero.Results;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>객체 상세(Oracle 과 같은 구역 제목·열·순서)·이름 풀이 한 단계·DDL 추출(표·PSM 객체, 나머지 유형은 뒤 단계).</summary>
internal sealed partial class TiberoMetadataProvider
{
    /// <summary>
    /// 이 유형의 DDL 은 아직 만들지 않는다는 이유(화면은 예외 메시지를 DDL 칸·상태 줄에 보인다). 뷰·시퀀스·시노님·인덱스·구체화 뷰 등은 사전 뷰로 만드는 일이
    /// 따로 필요하다(T3 DDL 2단계, 카탈로그 <c>meta.ddl</c> 머리말: DBMS_METADATA 는 TABLE 만 받는다).
    /// </summary>
    internal static string DdlNotSupported(string objectType) =>
        $"Tibero 는 {objectType} 유형의 DDL 추출을 아직 지원하지 않습니다(지원: TABLE, PROCEDURE, FUNCTION, PACKAGE, PACKAGE BODY, TYPE, TYPE BODY, TRIGGER).";

    /// <summary>열·제약·인덱스를 가진 객체 유형(<see cref="GetTableAsync"/> 로 읽는다).</summary>
    private static readonly HashSet<string> TableLike = new(StringComparer.Ordinal) { "TABLE", "VIEW", "MATERIALIZED VIEW" };

    /// <summary>소스 글(<c>*_source</c>)로 DDL 을 만드는 유형(DBMS_METADATA 가 받지 못하는 PSM 객체들). 값은 소스가 시작하는 낱말이기도 하다.</summary>
    private static readonly HashSet<string> SourceDdlTypes = new(StringComparer.Ordinal)
    {
        "PROCEDURE", "FUNCTION", "PACKAGE", "PACKAGE BODY", "TYPE", "TYPE BODY", "TRIGGER",
    };

    /// <summary>
    /// 표 DDL 을 읽는 길(<c>meta.ddl</c> 을 이름 바인드로 보내 OUT CLOB <c>ddl</c> 을 받는다). 기본은 서버에 묻는다.
    /// <see cref="CatalogRunner"/> 는 읽기 질의(리더)만 받아 OUT 매개변수를 못 읽으므로 따로 둔다 — 시험은 이것을 바꿔 끼운다.
    /// </summary>
    internal Func<ISession, IReadOnlyDictionary<string, object?>, CancellationToken, Task<string?>> ReadTableDdl { get; init; } = ReadTableDdlFromServerAsync;

    /// <summary>
    /// 객체 DDL(Oracle <c>DBMS_METADATA.GET_DDL</c> 대응). 부르는 쪽(객체 상세 DDL 탭·속성 창·DDL 일괄 추출·열 순서 바꾸기·AI 뷰 참조 풀이)은 모두 예외를 잡아
    /// 메시지를 보이거나 그 항목만 실패로 적는다.
    /// <list type="bullet">
    /// <item>TABLE: 카탈로그 <c>meta.ddl</c>(DBMS_METADATA). 끝의 <c>;</c> 는 <see cref="DdlOptions.SqlTerminator"/> 로 맞춘다(카탈로그가 붙이거나 뗀다).</item>
    /// <item>PROCEDURE·FUNCTION·PACKAGE(명세만)·PACKAGE BODY·TYPE(명세만)·TYPE BODY·TRIGGER: <c>meta.source</c> 글로 만든
    /// <c>CREATE OR REPLACE … "OWNER"."NAME" …</c>. 명세와 본문은 Oracle 처럼 따로다(합치지 않는다). <see cref="DdlOptions.SqlTerminator"/> 면 새 줄에 <c>/</c> 를 붙인다(PL/SQL 관례).</item>
    /// <item>그 밖의 유형: <see cref="NotSupportedException"/>.</item>
    /// <item>없거나 볼 수 없는 객체(줄·결과 없음): <see cref="InvalidOperationException"/>(<see cref="GetSourceAsync"/> 와 같은 모양). 없는 TABLE 은 서버 오류(ODP-15104)가 그대로 올라간다.</item>
    /// </list>
    /// 옵션: <see cref="DdlOptions.SqlTerminator"/> 만 적용한다. <see cref="DdlOptions.IncludeStorage"/>·<see cref="DdlOptions.IncludeSegmentAttributes"/>·
    /// <see cref="DdlOptions.IncludeTablespace"/> 는 Tibero 의 DBMS_METADATA 에 SET_TRANSFORM_PARAM 이 없어 적용할 수 없다(서버가 만든 속성이 그대로 들어간다).
    /// <see cref="DdlOptions.IncludeDependentObjects"/> 는 GET_DEPENDENT_DDL 이 없어 표의 인덱스·제약·주석·권한·트리거 DDL 이 붙지 않는다(뒤 단계에서 사전 뷰로 만든다).
    /// </summary>
    public async Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(o);

        var type = obj.ObjectType.ToUpperInvariant();
        if (type == "TABLE")
        {
            return await GetTableDdlAsync(s, obj, o, ct).ConfigureAwait(false);
        }

        if (SourceDdlTypes.Contains(type))
        {
            return await GetSourceDdlAsync(s, obj, type, o, ct).ConfigureAwait(false);
        }

        throw new NotSupportedException(DdlNotSupported(obj.ObjectType));
    }

    /// <summary>
    /// <c>meta.ddl</c> 이 받는 바인드: <c>p_type</c>(Tibero PSM 에서 <c>:type</c> 은 예약어라 이름이 다르다), <c>name</c>, <c>owner</c>, <c>terminator</c>,
    /// <c>storage</c>, <c>segment_attributes</c>, <c>tablespace</c>, <c>dependents</c>(0|1). 뒤 네 개는 카탈로그가 받기만 하고 무시한다.
    /// </summary>
    internal static Dictionary<string, object?> DdlBinds(DbObjectRef obj, DdlOptions o)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(o);
        return new(StringComparer.Ordinal)
        {
            ["p_type"] = "TABLE",
            ["name"] = obj.Name,
            ["owner"] = obj.Owner,
            ["terminator"] = o.SqlTerminator ? 1 : 0,
            ["storage"] = o.IncludeStorage ? 1 : 0,
            ["segment_attributes"] = o.IncludeSegmentAttributes ? 1 : 0,
            ["tablespace"] = o.IncludeTablespace ? 1 : 0,
            ["dependents"] = o.IncludeDependentObjects ? 1 : 0,
        };
    }

    private async Task<string> GetTableDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
    {
        var text = await ReadTableDdl(s, DdlBinds(obj, o), ct).ConfigureAwait(false);
        var ddl = (text ?? string.Empty).Trim('\r', '\n', ' ', '\t'); // Oracle 과 같이 앞뒤 공백·줄바꿈을 뗀다
        return ddl.Length == 0 ? throw DdlNotFound(obj) : ddl;
    }

    private async Task<string> GetSourceDdlAsync(ISession s, DbObjectRef obj, string type, DdlOptions o, CancellationToken ct)
    {
        var rows = await RunAsync(s, "meta.source", ObjectBinds(obj.Owner, obj.Name, type), ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw DdlNotFound(obj);
        }

        var ddl = TiberoDdlText.FromSource(type, obj.Owner, obj.Name, [.. rows.Select(r => Str(r[0]))])
            ?? throw new InvalidOperationException($"소스가 {type} 로 시작하지 않아 DDL 을 만들 수 없다: {obj.Owner}.{obj.Name} ({type})");
        return TiberoDdlText.WithTerminator(ddl, o.SqlTerminator);
    }

    private static InvalidOperationException DdlNotFound(DbObjectRef obj) =>
        new($"DDL 을 찾을 수 없다: {obj.Owner}.{obj.Name} ({obj.ObjectType})");

    /// <summary>
    /// <c>meta.ddl</c>(PSM 블록)을 보내 OUT CLOB 을 받는다. 세션 명령 잠금 안에서 보내고(D-041), 드라이버는 동기로 도니 스레드 풀에서 돌린다.
    /// 끊김이면 세션을 Broken 으로 돌리고 그대로 던진다.
    /// </summary>
    private static async Task<string?> ReadTableDdlFromServerAsync(ISession s, IReadOnlyDictionary<string, object?> binds, CancellationToken ct)
    {
        var session = s as TiberoSession ?? throw new ArgumentException("TiberoSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var driver = session.Driver ?? throw new InvalidOperationException("세션에 Tibero 드라이버 어댑터가 없어 OUT 값을 읽을 수 없습니다.");
        var sql = TiberoCatalogQuery.Sql(session, "meta.ddl");

        await session.CommandGate.EnterAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                await using var command = session.CreateCommand(sql);
                foreach (var (name, value) in binds)
                {
                    TiberoSessionQuery.Bind(command, name, value);
                }

                var bind = new BindValue(BindDataType.Clob, null, BindDirection.Out);
                var ddl = TiberoBindBuilder.Create(driver, command, "ddl", bind);
                command.Parameters.Add(ddl);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return TiberoValues.ReadOut(driver, ddl, bind) as string; // CLOB 은 전체를 읽어 글로 준다
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (session.ReportFailure(ex))
        {
            throw;
        }
        finally
        {
            session.CommandGate.Release();
        }
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
