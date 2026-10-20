using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.SchemaBrowser;

// SchemaBrowserViewModel 의 일부: 폴더(표·열·제약) 지연 로딩(TSK-10-07 에서 나눔, 동작 불변).
public sealed partial class SchemaBrowserViewModel
{
    /// <summary>
    /// 표·뷰가 펼쳐질 때 하위 폴더만 만든다(DB 를 읽지 않는다). 표 = 6폴더, 뷰·머티리얼라이즈드 뷰 = Columns·Triggers.
    /// 폴더 안은 폴더를 펼칠 때 읽는다(<see cref="LoadFolderAsync"/>).
    /// </summary>
    private void AddFolders(SchemaNode table)
    {
        table.Children.Clear();
        table.TableLoad = null;
        var kinds = table.Ref!.ObjectType == "TABLE"
            ? (ReadOnlySpan<SchemaFolderKind>)
            [
                SchemaFolderKind.Columns, SchemaFolderKind.Indexes, SchemaFolderKind.Constraints,
                SchemaFolderKind.Triggers, SchemaFolderKind.SnapshotLog, SchemaFolderKind.RecycleBin,
            ]
            : [SchemaFolderKind.Columns, SchemaFolderKind.Triggers];
        foreach (var kind in kinds)
        {
            var folder = new SchemaNode(this, SchemaNodeKind.Folder, FolderTitle(kind))
            {
                Owner = table.Owner,
                Parent = table,
                FolderKind = kind,
            };
            folder.ResetChildren();
            table.Children.Add(folder);
        }
    }

    internal static string FolderTitle(SchemaFolderKind kind) => kind switch
    {
        SchemaFolderKind.Columns => "Columns",
        SchemaFolderKind.Indexes => "Indexes",
        SchemaFolderKind.Constraints => "Constraints",
        SchemaFolderKind.Triggers => "Triggers",
        SchemaFolderKind.SnapshotLog => "Snapshot Log",
        _ => "Recycle Bin",
    };

    /// <summary>폴더를 펼칠 때 그 내용을 읽는다. Columns·Indexes·Constraints 는 표 노드가 든 <c>GetTableAsync</c> 결과를 같이 쓴다.</summary>
    private async Task LoadFolderAsync(SchemaNode folder, ISession session, CancellationToken ct)
    {
        var table = folder.Parent!;
        var target = table.Ref!;
        var items = new List<SchemaNode>();
        var empty = "없음";
        switch (folder.FolderKind)
        {
            case SchemaFolderKind.Columns:
                foreach (var c in (await LoadTableAsync(table, session, ct)).Columns.OrderBy(c => c.Position))
                {
                    items.Add(new SchemaNode(null, SchemaNodeKind.Column, c.Name) { Owner = table.Owner, Detail = DescribeColumn(c), Parent = folder });
                }

                break;
            case SchemaFolderKind.Indexes:
                foreach (var i in (await LoadTableAsync(table, session, ct)).Indexes.OrderBy(i => i.Name, StringComparer.Ordinal))
                {
                    items.Add(new SchemaNode(null, SchemaNodeKind.Index, i.Name)
                    {
                        Owner = table.Owner,
                        Detail = (i.IsUnique ? "UNIQUE " : string.Empty) + "(" + string.Join(", ", i.Columns) + ")",
                        Parent = folder,
                        OpenRef = target,
                    });
                }

                break;
            case SchemaFolderKind.Constraints:
                foreach (var c in (await LoadTableAsync(table, session, ct)).Constraints.OrderBy(c => c.Name, StringComparer.Ordinal))
                {
                    items.Add(new SchemaNode(null, SchemaNodeKind.Constraint, c.Name)
                    {
                        Owner = table.Owner,
                        Detail = DescribeConstraint(c),
                        Parent = folder,
                        OpenRef = target,
                    });
                }

                break;
            case SchemaFolderKind.Triggers:
                if (_treeProvider is null)
                {
                    break;
                }

                var triggers = await _treeProvider.ListTriggersAsync(session, target, ct);
                if (triggers.Denied)
                {
                    empty = "권한 없음";
                    break;
                }

                foreach (var trg in triggers.Items)
                {
                    items.Add(MakeObject(new DbObjectRef(trg.Owner, trg.Name, "TRIGGER"), folder,
                        detail: string.Equals(trg.Status, "ENABLED", StringComparison.OrdinalIgnoreCase) ? null : "DISABLED"));
                }

                break;
            case SchemaFolderKind.SnapshotLog:
                if (_treeProvider is null)
                {
                    break;
                }

                var logs = await _treeProvider.ListSnapshotLogsAsync(session, target, ct);
                if (logs.Denied)
                {
                    empty = "권한 없음";
                    break;
                }

                foreach (var log in logs.Items)
                {
                    items.Add(MakeObject(new DbObjectRef(log.LogOwner, log.LogTable, "TABLE"), folder));
                }

                break;
            case SchemaFolderKind.RecycleBin:
                // user_recyclebin 은 접속 사용자 것만 보인다. 다른 스키마의 표는 읽을 방법이 없으므로 권한 없음으로 보인다.
                if (!string.Equals(target.Owner, ConnectedUser(session), StringComparison.OrdinalIgnoreCase))
                {
                    empty = "권한 없음";
                    break;
                }

                if (_admin is null)
                {
                    break;
                }

                foreach (var r in (await _admin.ListRecycleBinAsync(session, ct))
                    .Where(r => string.Equals(r.OriginalName, target.Name, StringComparison.Ordinal)
                        && string.Equals(r.ObjectType, "TABLE", StringComparison.OrdinalIgnoreCase)))
                {
                    items.Add(new SchemaNode(null, SchemaNodeKind.Message, r.ObjectName)
                    {
                        Owner = table.Owner,
                        Detail = r.DropTime?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                        Parent = folder,
                    });
                }

                break;
        }

        folder.Children.Clear();
        if (items.Count == 0)
        {
            folder.Children.Add(new SchemaNode(null, SchemaNodeKind.Message, empty) { Parent = folder });
            return;
        }

        foreach (var n in items)
        {
            folder.Children.Add(n);
        }
    }

    /// <summary>접속 사용자(프록시 접속이면 실제 세션 사용자).</summary>
    private static string? ConnectedUser(ISession session) => session.Server.SessionUser ?? session.Profile.User;

    /// <summary>표·뷰 노드마다 <c>GetTableAsync</c> 를 한 번만 부른다(세 폴더가 같이 쓴다). 실패하면 다음에 다시 읽는다.</summary>
    private async Task<TableInfo> LoadTableAsync(SchemaNode table, ISession session, CancellationToken ct)
    {
        var load = table.TableLoad ??= _meta!.GetTableAsync(session, table.Ref!, ct);
        try
        {
            return await load;
        }
        catch
        {
            if (ReferenceEquals(table.TableLoad, load))
            {
                table.TableLoad = null;
            }

            throw;
        }
    }

    internal static string DescribeConstraint(ConstraintInfo c)
    {
        var kind = c.Kind switch
        {
            ConstraintKind.PrimaryKey => "PK",
            ConstraintKind.Unique => "UNIQUE",
            ConstraintKind.ForeignKey => "FK",
            _ => "CHECK",
        };
        var detail = c.Kind == ConstraintKind.Check && c.Condition is { } cond ? cond : "(" + string.Join(", ", c.Columns) + ")";
        if (c.ReferencedTable is { } r)
        {
            detail += " → " + r.Name;
        }

        return c.IsEnabled ? $"{kind} {detail}" : $"{kind} {detail} DISABLED";
    }

    /// <summary>노드가 표·뷰 노드이거나 그 하위 노드(폴더·열·인덱스 등)인지(<c>tableNodeSelected</c> 문맥 키).</summary>
    internal static bool IsInsideTable(SchemaNode? node)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n is { Kind: SchemaNodeKind.Item, Ref: { } r } && SchemaNode.IsTableLike(r.ObjectType))
            {
                return true;
            }
        }

        return false;
    }

    internal static string DescribeColumn(ColumnInfo c)
    {
        var type = c.DataType;
        if (c.Precision is { } p)
        {
            type += c.Scale is { } s and not 0 ? $"({p},{s})" : $"({p})";
        }
        else if (c.Length is { } l and > 0 && c.DataType.Contains("CHAR", StringComparison.Ordinal))
        {
            type += $"({l})";
        }

        return c.IsNullable ? type : type + " NOT NULL";
    }
}
