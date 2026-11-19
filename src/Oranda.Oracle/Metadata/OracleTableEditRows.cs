using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Metadata;
using static Oranda.Core.Catalog.Providers.CatalogValues;

namespace Oranda.Oracle.Metadata;

/// <summary>
/// Oracle <c>meta.columns</c>·<c>meta.constraints</c>·<c>meta.indexes</c>·<c>meta.table_props</c> 의 뒤에 붙인 열(테이블 편집기 TE-29)을 모델의 init 멤버로 옮긴다.
/// 앞 열은 Core <see cref="MetadataRows"/> 가 Tibero 와 같은 규칙으로 읽고, 이 클래스는 Oracle 만 더 읽는다(Core·Tibero 행 모양은 그대로).
/// 실측 근거 = <c>docs/design/oracle-table-edit-probe.md</c>.
/// </summary>
internal static partial class OracleTableEditRows
{
    // meta.columns 뒤 열(byte_length 다음): char_used, identity_generation, identity_options, default_on_null
    private const int ColCharUsed = 14;
    private const int ColIdentityGeneration = 15;
    private const int ColIdentityOptions = 16;
    private const int ColDefaultOnNull = 17;

    // meta.constraints 뒤 열(r_constraint_name 다음): delete_rule, r_column_name, index_name
    private const int ConsName = 0;
    private const int ConsType = 1;
    private const int ConsPosition = 7;
    private const int ConsDeleteRule = 9;
    private const int ConsRefColumn = 10;
    private const int ConsIndexName = 11;

    // meta.indexes: index_name, uniqueness, index_type, column_name, column_position, descend, column_expression, tablespace_name
    private const int IdxName = 0;
    private const int IdxColumn = 3;
    private const int IdxPosition = 4;
    private const int IdxDescend = 5;
    private const int IdxExpression = 6;
    private const int IdxTablespace = 7;
    private const int IdxStatus = 8;
    private const int IdxPartitioned = 9;
    private const int PropObjectTable = 13;

    /// <summary>
    /// 열 한 줄 → 열. 길이 기준은 <c>VARCHAR2</c>·<c>CHAR</c> 에만 둔다(<c>NVARCHAR2</c>·<c>NCHAR</c> 는 늘 글자 기준이라 <c>n CHAR</c> 를 쓰면 문법 오류다).
    /// 신원 열은 <c>ALL_TAB_IDENTITY_COLS</c> 의 생성 방식·옵션(<c>START WITH: n, INCREMENT BY: m, …</c>)과 <c>DEFAULT_ON_NULL</c> 로 채운다.
    /// </summary>
    internal static ColumnInfo ToColumn(object?[] r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var column = MetadataRows.ToColumn(r);
        if (r.Length <= ColDefaultOnNull)
        {
            return column;
        }

        return column with
        {
            CharSemantics = column.DataType is "VARCHAR2" or "CHAR" ? CharSemanticsOf(Str(r[ColCharUsed])) : null,
            Identity = column.IsIdentity ? IdentityOf(Str(r[ColIdentityGeneration]), Str(r[ColIdentityOptions]), Str(r[ColDefaultOnNull])) : null,
        };
    }

    /// <summary><c>CHAR_USED</c>(B/C) → 길이 기준. 그 밖은 null.</summary>
    internal static CharSemantics? CharSemanticsOf(string? charUsed) => charUsed switch
    {
        "B" => CharSemantics.Bytes,
        "C" => CharSemantics.Characters,
        _ => null,
    };

    /// <summary>신원 열 속성. 생성 방식을 모르면 null.</summary>
    internal static IdentityInfo? IdentityOf(string? generation, string? options, string? defaultOnNull)
    {
        IdentityGeneration? kind = generation switch
        {
            "ALWAYS" => IdentityGeneration.Always,
            "BY DEFAULT" => string.Equals(defaultOnNull, "YES", StringComparison.Ordinal)
                ? IdentityGeneration.ByDefaultOnNull
                : IdentityGeneration.ByDefault,
            _ => null,
        };
        if (kind is null)
        {
            return null;
        }

        return new IdentityInfo(kind.Value)
        {
            StartWith = Option(options, StartWithPattern()),
            IncrementBy = Option(options, IncrementByPattern()),
        };
    }

    /// <summary>
    /// 제약 목록에 FK 삭제 규칙(<c>DELETE_RULE</c>)과 참조 열(위치 순서)을 더한다. 행 모양이 예전(뒤 열 없음)이면 그대로 돌려준다.
    /// </summary>
    internal static IReadOnlyList<ConstraintInfo> WithConstraintExtras(IReadOnlyList<ConstraintInfo> constraints, IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0 || rows[0].Length <= ConsIndexName)
        {
            return constraints;
        }

        var byName = rows.GroupBy(r => Str(r[ConsName])!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return [.. constraints.Select(c =>
        {
            if (c.Kind != ConstraintKind.ForeignKey || !byName.TryGetValue(c.Name, out var own))
            {
                return c;
            }

            return c with
            {
                DeleteRule = DeleteRuleOf(Str(own[0][ConsDeleteRule])),
                ReferencedColumns = [.. own.Where(r => r[ConsRefColumn] is not null)
                    .OrderBy(r => Int(r[ConsPosition]) ?? 0)
                    .Select(r => Str(r[ConsRefColumn])!)],
            };
        })];
    }

    /// <summary><c>DELETE_RULE</c> → 삭제 규칙. <c>NO ACTION</c>·모름 = <see cref="DeleteRule.NoAction"/>.</summary>
    internal static DeleteRule DeleteRuleOf(string? rule) => rule switch
    {
        "CASCADE" => DeleteRule.Cascade,
        "SET NULL" => DeleteRule.SetNull,
        _ => DeleteRule.NoAction,
    };

    /// <summary>
    /// <c>meta.indexes</c> 행 → 인덱스 목록(<see cref="MetadataRows.BuildIndexes"/> 와 같은 이름·유일·종류·열 + TE-29 멤버).
    /// DESC 열은 서버가 숨은 열(<c>SYS_NC…</c>)과 식 <c>"열"</c> 로 둔다 — 식이 따옴표 이름 하나뿐이면 원래 열 이름으로 되돌리고 식은 비운다.
    /// 그 밖의 식(함수 기반)은 열 자리에 숨은 열 이름을 그대로 두고(데이터 복사의 판정이 그 이름을 쓴다) <see cref="IndexInfo.Expressions"/> 에 식을 둔다.
    /// <see cref="IndexInfo.IndexType"/> 은 사전 값 그대로다(DESC 만 있는 인덱스도 <c>FUNCTION-BASED NORMAL</c>).
    /// 제약 인덱스는 <paramref name="constraintRows"/>(<c>meta.constraints</c>)의 <c>index_name</c> 으로 짝짓는다.
    /// </summary>
    internal static List<IndexInfo> BuildIndexes(IReadOnlyList<object?[]> rows, IReadOnlyList<object?[]> constraintRows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(constraintRows);
        var basic = MetadataRows.BuildIndexes(rows);
        if (rows.Count == 0 || rows[0].Length <= IdxTablespace)
        {
            return basic;
        }

        var owners = ConstraintIndexOwners(constraintRows);
        var byName = rows.GroupBy(r => Str(r[IdxName])!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return [.. basic.Select(index =>
        {
            var own = byName[index.Name].OrderBy(r => Int(r[IdxPosition]) ?? 0).ToList();
            var columns = new List<string>(own.Count);
            var descending = new List<bool>(own.Count);
            var expressions = new List<string?>(own.Count);
            foreach (var r in own)
            {
                var expression = Str(r[IdxExpression])?.Trim();
                if (expression is not null && QuotedName().Match(expression) is { Success: true } m)
                {
                    columns.Add(m.Groups[1].Value);
                    expressions.Add(null);
                }
                else
                {
                    columns.Add(Str(r[IdxColumn])!);
                    expressions.Add(string.IsNullOrEmpty(expression) ? null : expression);
                }

                descending.Add(string.Equals(Str(r[IdxDescend]), "DESC", StringComparison.Ordinal));
            }

            return index with
            {
                Columns = columns,
                Descending = descending.Contains(true) ? descending : [],
                Expressions = expressions.Any(e => e is not null) ? expressions : [],
                Tablespace = Str(own[0][IdxTablespace]),
                ConstraintName = owners.GetValueOrDefault(index.Name),

                // 스키마 브라우저 표식: [8] status(UNUSABLE·파티션 인덱스는 N/A) [9] partitioned. 열이 없는 예전 행 모양이면 모름(false).
                IsUnusable = own[0].Length > IdxStatus && Str(own[0][IdxStatus]) == "UNUSABLE",
                IsPartitioned = own[0].Length > IdxPartitioned && Str(own[0][IdxPartitioned]) == "YES",
            };
        })];
    }

    /// <summary>
    /// <c>meta.table_props</c> 한 줄(tablespace_name, temporary, duration, iot_type, partitioned, cluster_name, external, 저장 속성 6열)을 테이블에 붙인다. 행이 없으면(뷰) 그대로.
    /// 종류 판정 순서: 객체 테이블([13] object_table) → 임시 → 외부 → IOT → 클러스터 → 일반.
    /// </summary>
    internal static TableInfo WithTableProps(TableInfo table, IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return table;
        }

        var r = rows[0];
        var kind = r.Length > PropObjectTable && Str(r[PropObjectTable]) == "YES" ? TableKind.ObjectTable
            : Str(r[1]) == "Y" ? TableKind.Temporary
            : Str(r[6]) == "YES" ? TableKind.External
            : Str(r[3]) == "IOT" ? TableKind.Iot
            : Str(r[5]) is { Length: > 0 } ? TableKind.Cluster
            : TableKind.Heap;
        return table with
        {
            Tablespace = Str(r[0]),
            Kind = kind,
            IsPartitioned = Str(r[4]) == "YES",

            // 저장 속성(T13c): [7] pct_free [8] ini_trans [9] compression [10] compress_for [11] logging [12] degree. 예전 행 모양이면 null(안 읽음).
            Storage = r.Length > 12 ? TableStorageRows.ForKind(TableStorageRows.Table(r[7], r[8], r[9], r[10], r[11], r[12]), kind) : null,
        };
    }

    /// <summary><c>meta.table_lobs</c> 줄(column_name, securefile, tablespace_name, compression, deduplication, in_row)을 <see cref="TableInfo.Lobs"/> 로.</summary>
    internal static TableInfo WithLobs(TableInfo table, IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count == 0
            ? table
            : table with { Lobs = [.. rows.Where(r => Str(r[0]) is not null).Select(r => TableStorageRows.Lob(Str(r[0])!, r[1], r[2], r[3], r[4], r[5]))] };
    }

    /// <summary>PK·UK 제약 행의 index_name → 제약 이름.</summary>
    private static Dictionary<string, string> ConstraintIndexOwners(IReadOnlyList<object?[]> constraintRows)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in constraintRows)
        {
            if (r.Length > ConsIndexName && Str(r[ConsType]) is "P" or "U" && Str(r[ConsIndexName]) is { } indexName)
            {
                owners.TryAdd(indexName, Str(r[ConsName])!);
            }
        }

        return owners;
    }

    private static long? Option(string? options, Regex pattern) =>
        options is not null && pattern.Match(options) is { Success: true } m
        && long.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    [GeneratedRegex(@"START WITH:\s*(-?\d+)")]
    private static partial Regex StartWithPattern();

    [GeneratedRegex(@"INCREMENT BY:\s*(-?\d+)")]
    private static partial Regex IncrementByPattern();

    /// <summary>따옴표 이름 하나뿐인 식(<c>"P1"</c>). Oracle 이름에는 큰따옴표가 들어갈 수 없다.</summary>
    [GeneratedRegex("^\"([^\"]+)\"$")]
    private static partial Regex QuotedName();
}
