using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Metadata;

/// <summary>객체 상세·DDL(DBMS_METADATA). 소유: TSK-07-03.</summary>
internal sealed partial class OracleMetadataProvider
{
    /// <summary>열·제약·인덱스를 가진 객체 유형(<see cref="GetTableAsync"/> 로 읽는다).</summary>
    private static readonly HashSet<string> TableLike = new(StringComparer.Ordinal) { "TABLE", "VIEW", "MATERIALIZED VIEW" };

    public async Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(o);

        var session = AsOracle(s);
        var query = Catalog.Value.Get("meta.ddl", s.Server);

        var gate = Gates.GetValue(session, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = query.Sql;
            cmd.Parameters.Add(new OracleParameter("type", OracleDbType.Varchar2) { Value = ToMetadataType(obj.ObjectType) });
            cmd.Parameters.Add(new OracleParameter("name", OracleDbType.Varchar2) { Value = obj.Name });
            cmd.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2) { Value = obj.Owner });
            cmd.Parameters.Add(new OracleParameter("terminator", OracleDbType.Int32) { Value = o.SqlTerminator ? 1 : 0 });
            cmd.Parameters.Add(new OracleParameter("storage", OracleDbType.Int32) { Value = o.IncludeStorage ? 1 : 0 });
            cmd.Parameters.Add(new OracleParameter("segment_attributes", OracleDbType.Int32) { Value = o.IncludeSegmentAttributes ? 1 : 0 });
            cmd.Parameters.Add(new OracleParameter("tablespace", OracleDbType.Int32) { Value = o.IncludeTablespace ? 1 : 0 });
            cmd.Parameters.Add(new OracleParameter("dependents", OracleDbType.Int32) { Value = o.IncludeDependentObjects ? 1 : 0 });
            var ddl = new OracleParameter("ddl", OracleDbType.Clob) { Direction = System.Data.ParameterDirection.Output };
            cmd.Parameters.Add(ddl);

            await cmd.ExecuteNonQueryAsync(ct);
            var text = ddl.Value is OracleClob clob ? clob.Value : Str(ddl.Value);
            return (text ?? string.Empty).Trim('\r', '\n', ' ', '\t');
        }
        catch (OracleException ex) when (session.ReportFailure(ex))
        {
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);

        var type = obj.ObjectType;
        var sections = new List<DetailSection>();

        void BindObject(OracleCommand c)
        {
            c.Parameters.Add("owner", obj.Owner);
            c.Parameters.Add("name", obj.Name);
            c.Parameters.Add("type", type);
        }

        void BindTable(OracleCommand c)
        {
            c.Parameters.Add("owner", obj.Owner);
            c.Parameters.Add("name", obj.Name);
        }

        var general = await QueryAsync(s, "meta.detail_general", BindObject, ct);
        if (general.Count > 0)
        {
            var g = general[0];
            sections.Add(new DetailSection("일반", ["항목", "값"],
            [
                ["유형", Str(g[0])],
                ["상태", Str(g[1])],
                ["생성", g[2]],
                ["마지막 DDL", g[3]],
                ["편집 가능(editionable)", s.Server.Features.Release12Columns ? Str(g[4]) : ServerFeatures.UnavailableText],
                ["객체 ID", Str(g[5])],
            ]));
        }

        if (TableLike.Contains(type))
        {
            var table = await GetTableAsync(s, obj, ct);
            AddTableSections(sections, table, s.Server.Features);
        }

        if (type is "TABLE" or "VIEW")
        {
            // 뷰는 INSTEAD OF 트리거가 같은 쿼리로 나온다.
            var triggers = await TryQueryAsync(s, "meta.detail_triggers", BindTable, ct);
            if (triggers is { Count: > 0 })
            {
                sections.Add(new DetailSection("트리거", ["이름", "유형", "이벤트", "상태", "WHEN 조건", "소유자"],
                    [.. triggers.Select(r => (IReadOnlyList<object?>)[Str(r[0]), Str(r[1]), Str(r[2]), Str(r[3]), Str(r[4]), Str(r[5])])]));
            }
        }

        if (string.Equals(type, "TABLE", StringComparison.Ordinal))
        {
            var referencedBy = await TryQueryAsync(s, "meta.detail_referenced_by", BindTable, ct);
            if (referencedBy is { Count: > 0 })
            {
                sections.Add(new DetailSection("참조됨", ["소유자", "테이블", "외래키", "열", "참조 키", "삭제 규칙", "상태"],
                    [.. referencedBy.Select(r => (IReadOnlyList<object?>)[Str(r[0]), Str(r[1]), Str(r[2]), Str(r[3]), Str(r[4]), Str(r[5]), Str(r[6])])]));
            }

            var partitions = await QueryAsync(s, "meta.detail_partitions", BindTable, ct);
            if (partitions.Count > 0)
            {
                sections.Add(new DetailSection("파티션",
                    ["위치", "이름", "방식", "하위 방식", "경계값", "테이블스페이스", "행 수", "마지막 분석"],
                    [.. partitions.Select(r => (IReadOnlyList<object?>)[Int(r[0]), Str(r[1]), Str(r[2]), Str(r[3]), Str(r[4])?.Trim(), Str(r[5]), Long(r[6]), r[7]])]));
            }

            var stats = await QueryAsync(s, "meta.detail_stats", BindTable, ct);
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
            var sizes = await ReadSizesAsync(s, BindTable, ct);
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

        var privileges = await QueryAsync(s, s.Server.Capabilities.CanQueryDictionary ? "meta.detail_privileges" : "meta.detail_privileges_user", BindTable, ct);
        if (privileges.Count > 0)
        {
            sections.Add(new DetailSection("권한", ["받는 사람", "권한", "재부여 가능", "부여자"],
                [.. privileges.Select(r => (IReadOnlyList<object?>)[Str(r[0]), Str(r[1]), Str(r[2]), Str(r[3])])]));
        }

        var deps = await QueryAsync(s, "meta.detail_dependencies", BindObject, ct);
        if (deps.Count > 0)
        {
            sections.Add(new DetailSection("의존", ["방향", "소유자", "이름", "유형"],
                [.. deps.Select(r => (IReadOnlyList<object?>)[
                    string.Equals(Str(r[0]), "USES", StringComparison.Ordinal) ? "참조함" : "참조됨", Str(r[1]), Str(r[2]), Str(r[3])])]));
        }

        return new ObjectDetail(obj, sections);
    }

    private static void AddTableSections(List<DetailSection> sections, TableInfo table, ServerFeatures features)
    {
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
            // 11g: identity·invisible 열·도메인이 없는 서버라 그 칸을 숨긴다(TSK-03-07).
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

    /// <summary>dba 권한이 있으면 dba_segments, 없으면 자기 스키마일 때만 user_segments. 못 읽으면 null(크기 절을 빼고 계속한다).</summary>
    private static Task<List<object?[]>?> ReadSizesAsync(ISession s, Action<OracleCommand> bind, CancellationToken ct)
        => TryQueryAsync(s, s.Server.Capabilities.CanQueryDictionary ? "meta.detail_size" : "meta.detail_size_user", bind, ct);

    /// <summary>덧붙이는 구역 하나를 읽는다. 권한·뷰 문제로 실패하면 null(그 구역만 빼고 상세는 계속한다).</summary>
    private static async Task<List<object?[]>?> TryQueryAsync(ISession s, string id, Action<OracleCommand> bind, CancellationToken ct)
    {
        try
        {
            return await QueryAsync(s, id, bind, ct);
        }
        catch (OracleException)
        {
            return null;
        }
    }

    private static long? Long(object? v) => v is null ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);

    /// <summary>DbObjectRef.ObjectType → DBMS_METADATA 객체 유형 이름.</summary>
    internal static string ToMetadataType(string objectType) => objectType.ToUpperInvariant() switch
    {
        "PACKAGE" => "PACKAGE_SPEC",
        "TYPE" => "TYPE_SPEC",
        "DATABASE LINK" => "DB_LINK",
        // 스케줄러 객체는 모두 PROCOBJ 다('SCHEDULE' 그대로 넘기면 ORA-31600).
        "JOB" or "PROGRAM" or "SCHEDULE" or "CHAIN" => "PROCOBJ",
        var t => t.Replace(' ', '_'),
    };
}
