using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer;

namespace Oranda.App.Features.TableSpec;

/// <summary>
/// 표 정보(<see cref="IMetadataProvider.GetTableAsync"/>)를 테이블 정의서 문서 모델로 바꾼다(TSK-13-06).
/// 외래키가 참조하는 부모 열은 부모 표의 키 제약에서 위치 순서로 찾고, 읽지 못하면 표 이름만 적는다.
/// </summary>
internal static class TableSpecBuilder
{
    public static async Task<TableSpecDocument> BuildAsync(IMetadataProvider meta, ISession session, string title, IReadOnlyList<DbObjectRef> tables,
        IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var parents = new Dictionary<DbObjectRef, TableInfo?>();
        var result = new List<TableSpecTable>();
        for (var i = 0; i < tables.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var info = await meta.GetTableAsync(session, tables[i], ct);
            result.Add(await ConvertAsync(meta, session, info, parents, ct));
            progress?.Report((i + 1, tables.Count));
        }

        return new TableSpecDocument(title, DateTimeOffset.Now, result);
    }

    private static async Task<TableSpecTable> ConvertAsync(IMetadataProvider meta, ISession session, TableInfo info,
        Dictionary<DbObjectRef, TableInfo?> parents, CancellationToken ct)
    {
        var pk = info.Constraints.Where(c => c.Kind == ConstraintKind.PrimaryKey).SelectMany(c => c.Columns).ToHashSet(StringComparer.Ordinal);
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fk in info.Constraints.Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is not null))
        {
            var parentColumns = await ParentColumnsAsync(meta, session, fk, parents, ct);
            for (var i = 0; i < fk.Columns.Count; i++)
            {
                var parent = fk.ReferencedTable!.Name + (i < parentColumns.Count ? "." + parentColumns[i] : string.Empty);
                references[fk.Columns[i]] = references.TryGetValue(fk.Columns[i], out var old) ? old + ", " + parent : parent;
            }
        }

        var columns = info.Columns.OrderBy(c => c.Position).Select((c, n) => new TableSpecColumn(
            n + 1, c.Name, FormatType(c), c.IsNullable, pk.Contains(c.Name), references.GetValueOrDefault(c.Name),
            string.IsNullOrWhiteSpace(c.DefaultValue) ? null : c.DefaultValue.Trim(), c.Comment)).ToList();
        var indexes = info.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal).Select(i => new TableSpecIndex(i.Name, i.IsUnique, i.Columns)).ToList();
        return new TableSpecTable(info.Table.Owner, info.Table.Name, info.Comment, columns, indexes);
    }

    private static async Task<IReadOnlyList<string>> ParentColumnsAsync(IMetadataProvider meta, ISession session, ConstraintInfo fk,
        Dictionary<DbObjectRef, TableInfo?> parents, CancellationToken ct)
    {
        var parentRef = fk.ReferencedTable! with { ObjectType = "TABLE" };
        if (!parents.TryGetValue(parentRef, out var parent))
        {
            try
            {
                parent = await meta.GetTableAsync(session, parentRef, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                parent = null; // 권한 없음 등: 표 이름만 적는다.
            }

            parents[parentRef] = parent;
        }

        return parent?.Constraints.FirstOrDefault(c => string.Equals(c.Name, fk.ReferencedConstraint, StringComparison.Ordinal))?.Columns ?? [];
    }

    /// <summary>타입 이름에 정밀도·길이를 붙인다: <c>NUMBER(7,2)</c>, <c>VARCHAR2(10)</c>, <c>DATE</c>.</summary>
    internal static string FormatType(ColumnInfo c)
    {
        if (c.DataType.Contains('(', StringComparison.Ordinal))
        {
            return c.DataType; // INTERVAL DAY(2) TO SECOND(6) 처럼 이름에 이미 정밀도가 있다.
        }

        if (c.Precision is { } p)
        {
            return c.Scale is { } s and not 0
                ? string.Create(CultureInfo.InvariantCulture, $"{c.DataType}({p},{s})")
                : string.Create(CultureInfo.InvariantCulture, $"{c.DataType}({p})");
        }

        return c.Length is { } l and > 0 && (c.DataType.Contains("CHAR", StringComparison.Ordinal) || c.DataType == "RAW")
            ? string.Create(CultureInfo.InvariantCulture, $"{c.DataType}({l})")
            : c.DataType;
    }
}
