using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>「바뀌는 부분」 창 왼쪽 목록 한 줄의 종류(시안 SaveDiff 「수정」·「새 문장」).</summary>
public enum MapperSaveItemKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
}

/// <summary>
/// 저장할 편집 하나(design.md 5.1 의 5번 「바뀐 문장 목록」).
/// <see cref="Key"/> = 편집의 <see cref="MapperEdit"/> 가 가리키는 id(새 문장이면 새 id), <see cref="CurrentId"/> = 세션의 지금 id(<c>Rebase</c> 에 넘기는 id).
/// <see cref="Diff"/> 의 줄 번호는 이 편집 하나만 적용한 결과가 아니라 목록 전체를 적용한 새 원문 기준이다.
/// </summary>
public sealed record MapperSaveItem(
    string Key,
    string CurrentId,
    string ElementName,
    MapperSaveItemKind Kind,
    MapperEdit Edit,
    IReadOnlyList<MapperDiffLine> Diff,
    int Added,
    int Removed,
    int FirstLine,
    int LastLine);

/// <summary>
/// 세션의 저장 안 한 편집 → 「바뀌는 부분」 항목(design.md 5.1). 편집 전체를 한 번 적용하고 새 원문을 다시 읽어 문장마다 기준 원문 ↔ 새 원문을 맞댄다.
/// 파일 전체를 diff 하지 않는다(멀리 떨어진 두 문장 사이가 커지므로). 요소는 종류(요소 이름) + id 로 찾는다(다른 묶음끼리 같은 id 는 합법).
/// </summary>
public static class MapperSavePlan
{
    /// <summary>편집이 저장된 뒤의 지금 id(세션 <c>Rebase</c>·<c>ApplyDiskText</c> 가 맞대는 id). 원문을 고쳐 id 를 바꾼 문장은 새 원문에서 읽는다.</summary>
    public static string CurrentIdOf(MapperEdit edit, IMyBatisMapperParser parser)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(parser);
        return edit switch
        {
            MapperEdit.ReplaceElement r => ElementOfText(r.NewText, parser)?.Id ?? r.Id,
            MapperEdit.RenameElement n => n.NewId,
            MapperEdit.InsertElement i => i.Id,
            MapperEdit.DeleteElement d => d.Id,
            _ => throw new ArgumentOutOfRangeException(nameof(edit)),
        };
    }

    /// <summary>편집이 가리키는 기준 쪽 id(새 문장은 새 id).</summary>
    public static string KeyOf(MapperEdit edit) => edit switch
    {
        MapperEdit.ReplaceElement r => r.Id,
        MapperEdit.RenameElement n => n.Id,
        MapperEdit.InsertElement i => i.Id,
        MapperEdit.DeleteElement d => d.Id,
        _ => throw new ArgumentOutOfRangeException(nameof(edit)),
    };

    /// <summary>
    /// 항목 목록. <paramref name="baseline"/>.Apply 가 던지는 <see cref="ArgumentException"/>(없는 id·겹침)과 새 원문이 깨졌을 때의 <see cref="FormatException"/> 은 그대로 올린다
    /// (부른 쪽이 알리고 쓰지 않는다).
    /// </summary>
    public static IReadOnlyList<MapperSaveItem> Build(IMyBatisMapperDocument baseline, IReadOnlyList<MapperEdit> edits, IMyBatisMapperParser parser)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(parser);
        if (edits.Count == 0)
        {
            return [];
        }

        var newText = baseline.Apply(edits);
        var after = parser.Parse(newText);
        var items = new List<MapperSaveItem>(edits.Count);
        foreach (var edit in edits)
        {
            var key = KeyOf(edit);
            var currentId = CurrentIdOf(edit, parser);
            var before = edit is MapperEdit.InsertElement ? null : baseline.Find(key);
            var name = before?.Name ?? (edit is MapperEdit.InsertElement ins ? ElementOfText(ins.NewText, parser)?.Name : null) ?? string.Empty;
            var now = edit is MapperEdit.DeleteElement ? null : FindByName(after, name, currentId);
            int oldFirst = before is null ? 0 : MapperLineDiff.LineOf(baseline.Text, before.ElementRange.Start);
            int newFirst = now is null ? 0 : MapperLineDiff.LineOf(after.Text, now.ElementRange.Start);
            var diff = MapperLineDiff.Build(TextOf(baseline, before), TextOf(after, now), oldFirst, newFirst);
            var (added, removed) = MapperLineDiff.Count(diff);
            var kind = edit switch
            {
                MapperEdit.InsertElement => MapperSaveItemKind.Added,
                MapperEdit.DeleteElement => MapperSaveItemKind.Deleted,
                MapperEdit.RenameElement => MapperSaveItemKind.Renamed,
                _ => MapperSaveItemKind.Modified,
            };
            int first = now is null ? oldFirst : newFirst;
            int lines = MapperLineDiff.SplitLines(TextOf(now is null ? baseline : after, now ?? before)).Length;
            items.Add(new MapperSaveItem(key, currentId, name, kind, edit, diff, added, removed, first, first + Math.Max(0, lines - 1)));
        }

        return items;
    }

    /// <summary>문장 원문 하나(여는 태그 ~ 닫는 태그)를 감싼 매퍼로 읽은 요소. 깨졌으면 null.</summary>
    internal static MyBatisElement? ElementOfText(string text, IMyBatisMapperParser parser)
    {
        try
        {
            var elements = parser.Parse("<mapper>" + text + "</mapper>").Elements;
            return elements.Count == 1 ? elements[0] : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static MyBatisElement? FindByName(IMyBatisMapperDocument doc, string name, string id) =>
        doc.Elements.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal) && string.Equals(e.Name, name, StringComparison.Ordinal))
            ?? doc.Find(id);

    // 요소 원문은 매퍼 원문의 범위 그대로 잘라 쓴다(Apply 가 줄 끝을 문서 것으로 맞춘 뒤의 글).
    private static string TextOf(IMyBatisMapperDocument doc, MyBatisElement? element) =>
        element is null ? string.Empty : doc.Text.Substring(element.ElementRange.Start, element.ElementRange.Length);
}
