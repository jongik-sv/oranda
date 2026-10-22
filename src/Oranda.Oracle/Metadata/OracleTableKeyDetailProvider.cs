using System.Globalization;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Metadata;

/// <summary>
/// 객체 상세에서 인덱스·제약 줄을 고를 때 그 키의 열을 읽는다(TSK-13-34, 카탈로그 <c>meta.index_columns</c>·<c>meta.constraint_columns</c>).
/// 조회는 <see cref="OracleMetadataProvider.QueryAsync"/> 를 거친다 — 세션마다 하나인 메타데이터 잠금을 같이 써서 한 연결에 명령이 겹치지 않는다.
/// </summary>
[OrandaService(typeof(ITableKeyDetailProvider))]
internal sealed partial class OracleTableKeyDetailProvider : ITableKeyDetailProvider
{
    // ORA-00942(뷰 없음·권한 없음)·ORA-01031(권한 부족)은 「권한 없음」, 나머지 오류는 그대로 던진다.
    private static bool IsDenied(OracleException ex) => ex.Number is 942 or 1031;

    public async Task<SchemaFolderResult<IndexColumnDetail>> GetIndexColumnsAsync(ISession s, DbObjectRef table, string indexName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrEmpty(indexName);
        try
        {
            var rows = await OracleMetadataProvider.QueryAsync(s, "meta.index_columns", c => Bind(c, table, "index_name", indexName), ct);
            return new SchemaFolderResult<IndexColumnDetail>([.. rows.Select((r, i) => ToIndexColumn(r, i))]);
        }
        catch (OracleException ex) when (IsDenied(ex))
        {
            return new SchemaFolderResult<IndexColumnDetail>([], Denied: true);
        }
        catch (CatalogQueryNotFoundException)
        {
            return new SchemaFolderResult<IndexColumnDetail>([]);
        }
    }

    public async Task<SchemaFolderResult<ConstraintColumnDetail>> GetConstraintColumnsAsync(ISession s, DbObjectRef table, string constraintName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrEmpty(constraintName);
        try
        {
            var rows = await OracleMetadataProvider.QueryAsync(s, "meta.constraint_columns", c => Bind(c, table, "constraint_name", constraintName), ct);
            return new SchemaFolderResult<ConstraintColumnDetail>([.. rows.Select((r, i) => new ConstraintColumnDetail(Int(r[0]) ?? i + 1, Str(r[1]) ?? string.Empty)
            {
                ReferencedOwner = Str(r[2]),
                ReferencedTable = Str(r[3]),
                ReferencedColumn = Str(r[4]),
            })]);
        }
        catch (OracleException ex) when (IsDenied(ex))
        {
            return new SchemaFolderResult<ConstraintColumnDetail>([], Denied: true);
        }
        catch (CatalogQueryNotFoundException)
        {
            return new SchemaFolderResult<ConstraintColumnDetail>([]);
        }
    }

    /// <summary>
    /// 카탈로그 한 줄(column_position, column_name, descend, column_expression) → 인덱스 열.
    /// 내림차순 일반 열은 Oracle 이 숨은 열(<c>SYS_NC…$</c>) + 식 <c>"COL"</c> 로 두므로 따옴표를 벗겨 열 이름으로 쓰고 식은 비운다.
    /// </summary>
    internal static IndexColumnDetail ToIndexColumn(object?[] r, int index)
    {
        var name = Str(r[1]) ?? string.Empty;
        var descending = string.Equals(Str(r[2]), "DESC", StringComparison.OrdinalIgnoreCase);
        var expression = Str(r[3])?.Trim();
        if (string.IsNullOrEmpty(expression))
        {
            expression = null;
        }
        else if (QuotedName().Match(expression) is { Success: true } m)
        {
            name = m.Groups[1].Value;
            expression = null;
        }

        return new IndexColumnDetail(Int(r[0]) ?? index + 1, name, expression, descending);
    }

    private static void Bind(OracleCommand c, DbObjectRef table, string keyBind, string keyName)
    {
        c.Parameters.Add("owner", table.Owner);
        c.Parameters.Add("name", table.Name);
        c.Parameters.Add(keyBind, keyName);
    }

    private static string? Str(object? v) => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);

    private static int? Int(object? v) => v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    [GeneratedRegex("^\"([^\"]+)\"$")]
    private static partial Regex QuotedName();
}
