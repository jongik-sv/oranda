using Oranda.Core.Extensibility;

namespace Oranda.App.Features.SchemaBrowser;

// SchemaBrowserViewModel 의 일부: 편집기에 이름·SELECT·열 목록 넣기와 끌기 글(TSK-10-07 에서 나눔, 동작 불변).
public sealed partial class SchemaBrowserViewModel
{
    /// <summary>노드에 대한 삽입 텍스트. 만들 수 없으면 null.</summary>
    public async Task<string?> BuildTextAsync(SchemaNode? node, SchemaInsertMode mode, CancellationToken ct = default)
    {
        node ??= SelectedNode;
        if (node is null)
        {
            return null;
        }

        if (node.Kind is SchemaNodeKind.Column or SchemaNodeKind.Index or SchemaNodeKind.Constraint)
        {
            return SchemaSql.Quote(node.Title);
        }

        if (node.Ref is not { } r)
        {
            return null;
        }

        if (mode == SchemaInsertMode.Name)
        {
            return SchemaSql.QualifiedName(r);
        }

        if (!SchemaNode.IsTableLike(r.ObjectType) || _meta is null || CurrentSession is not { } session)
        {
            return mode == SchemaInsertMode.Select ? SchemaSql.Select(r, null) : SchemaSql.QualifiedName(r);
        }

        var info = await _meta.GetTableAsync(session, r, ct);
        return mode == SchemaInsertMode.Select ? SchemaSql.Select(r, info.Columns) : SchemaSql.ColumnList(info.Columns);
    }

    /// <summary>
    /// 활성 편집기의 선택 영역(또는 캐럿)에 넣는다. 편집기 계약(<see cref="IEditorContext.ProposeEditAsync"/>)을 거치므로
    /// 편집기가 미리보기·되돌리기를 책임진다. 편집기가 없거나 거절하면 false.
    /// </summary>
    public async Task<bool> InsertAsync(SchemaNode? node, SchemaInsertMode mode, CancellationToken ct = default)
    {
        ResolveServices();
        if (_editor?.GetActiveDocument() is not { } doc)
        {
            StatusText = "열려 있는 SQL 편집기가 없다";
            return false;
        }

        var text = await BuildTextAsync(node, mode, ct);
        if (text is null)
        {
            return false;
        }

        var range = doc.Selection.Length > 0
            ? doc.Selection
            : new Oranda.Core.Text.TextRange(Math.Clamp(doc.CaretOffset, 0, doc.Text.Length), 0);
        var description = mode switch
        {
            SchemaInsertMode.Name => "이름 넣기",
            SchemaInsertMode.Select => "SELECT 문 넣기",
            _ => "열 목록 넣기",
        };
        return await _editor.ProposeEditAsync(new TextEdit(range, text) { Description = description }, ct);
    }

    /// <summary>「새 SQL」이 새 탭을 만들 편집기 호스트. 시험은 자기 호스트를 꽂는다.</summary>
    internal Func<SqlEditor.SqlEditorHost> EditorHost { get; set; } = static () => SqlEditor.SqlEditorHost.Current;

    /// <summary>
    /// 표·뷰 노드(또는 그 하위 노드)로 새 SQL 탭을 열고 <see cref="SchemaInsertMode.Select"/> 와 같은 SELECT 문을 채운다.
    /// 탭의 접속은 지금 스키마 브라우저의 접속이고 캐럿은 문장 끝이다. 실행은 하지 않는다. 표·뷰가 아니면 false.
    /// </summary>
    public async Task<bool> NewSqlWithSelectAsync(SchemaNode? node, CancellationToken ct = default)
    {
        node ??= SelectedNode;
        while (node is not null && !(node.Kind == SchemaNodeKind.Item && node.Ref is { } r && SchemaNode.IsTableLike(r.ObjectType)))
        {
            node = node.Parent;
        }

        if (node is null)
        {
            return false;
        }

        ResolveServices();
        var text = await BuildTextAsync(node, SchemaInsertMode.Select, ct);
        if (text is null)
        {
            return false;
        }

        var doc = EditorHost().NewDocument(text);
        if (CurrentSession is { } session)
        {
            doc.ConnectionProfileId = session.Profile.Id;
        }

        doc.IsDirty = false;
        doc.Select(text.Length, 0);
        return true;
    }

    /// <summary>끌기 시작 때 쓸 텍스트: 표·뷰는 SELECT 문, 그 밖은 이름. Ctrl = 이름, Shift = 열 목록.</summary>
    public Task<string?> PrepareDragTextAsync(SchemaNode node, bool ctrl, bool shift, CancellationToken ct = default) =>
        BuildTextAsync(node, DragMode(ctrl, shift), ct);

    /// <summary>
    /// 누른 노드의 끌 글자 재료(표·뷰의 열 목록)를 미리 불러 캐시한다. 끌기가 시작되기 전에 끝나면 <see cref="BuildDragText"/> 가 쓴다.
    /// 실패해도 던지지 않는다(재료가 없으면 끌기 때 대체 글자로 끈다).
    /// </summary>
    public async Task PreloadDragMaterialAsync(SchemaNode node, CancellationToken ct = default)
    {
        ResolveServices();
        if (node.Ref is not { } r || !SchemaNode.IsTableLike(r.ObjectType) || _meta is null || CurrentSession is not { } session)
        {
            return;
        }

        // 옛 열 목록으로 끌지 않도록 비운다. 새로 불러오기 전에 끌면 대체 글자와 안내로 끈다.
        _dragColumns.Remove(r);
        try
        {
            var info = await _meta.GetTableAsync(session, r, ct);
            if (ReferenceEquals(CurrentSession, session))
            {
                _dragColumns[r] = info.Columns;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("끌 글자 재료(열 목록)를 불러오지 못했다 ({0}): {1}", SchemaSql.QualifiedName(r), ex.Message);
        }
    }

    /// <summary>
    /// 끌기 시작 때 쓸 글자를 <b>DB 를 부르지 않고 동기로</b> 만든다. 재료가 있으면 <see cref="PrepareDragTextAsync"/> 와 같은 글자.
    /// 표·뷰의 열 목록이 아직 필요한데 없으면 <see cref="DragTextWithoutMetadata"/> 로 대신하고 <c>ColumnsMissing</c> 을 켠다.
    /// </summary>
    public (string? Text, bool ColumnsMissing) BuildDragText(SchemaNode node, bool ctrl, bool shift)
    {
        var mode = DragMode(ctrl, shift);
        if (mode != SchemaInsertMode.Name && node is { Kind: SchemaNodeKind.Item, Ref: { } r } && SchemaNode.IsTableLike(r.ObjectType))
        {
            return _dragColumns.TryGetValue(r, out var columns)
                ? (mode == SchemaInsertMode.Select ? SchemaSql.Select(r, columns) : SchemaSql.ColumnList(columns), false)
                : (DragTextWithoutMetadata(node, ctrl, shift), true);
        }

        return (DragTextWithoutMetadata(node, ctrl, shift), false);
    }

    /// <summary>
    /// 메타데이터 없이 곧바로 만드는 끌기 텍스트. Windows OLE 끌기는 누름 이벤트 안에서 시작해야 하므로
    /// 열 정보를 아직 못 읽었으면 이것으로 시작한다(표·뷰 SELECT 는 <c>SELECT *</c>, 열 목록은 이름으로).
    /// </summary>
    public static string? DragTextWithoutMetadata(SchemaNode node, bool ctrl, bool shift)
    {
        if (node.Kind is SchemaNodeKind.Column or SchemaNodeKind.Index or SchemaNodeKind.Constraint)
        {
            return SchemaSql.Quote(node.Title);
        }

        if (node.Ref is not { } r)
        {
            return null;
        }

        return DragMode(ctrl, shift) switch
        {
            SchemaInsertMode.Select when SchemaNode.IsTableLike(r.ObjectType) => $"SELECT *\nFROM   {SchemaSql.QualifiedName(r)}",
            SchemaInsertMode.Select => SchemaSql.Select(r, null),
            _ => SchemaSql.QualifiedName(r),
        };
    }

    private static SchemaInsertMode DragMode(bool ctrl, bool shift) =>
        ctrl ? SchemaInsertMode.Name : shift ? SchemaInsertMode.Columns : SchemaInsertMode.Select;
}
