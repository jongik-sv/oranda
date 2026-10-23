using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>양쪽이 고친 문장 하나의 답(알림 띠 「파일 것으로 / 내 것으로」).</summary>
public enum MapperMergeChoice
{
    Mine,
    Disk,
}

/// <summary>
/// 양쪽이 고친 문장 하나(design.md 5.3 「같은 문장을 양쪽이 고침」). <see cref="Key"/> = 기준 쪽 id, <see cref="CurrentId"/> = 세션의 지금 id.
/// 원문이 없는 쪽은 null(디스크에서 지워짐 = <see cref="DiskText"/> null, 내가 지움 = <see cref="MineText"/> null).
/// </summary>
public sealed record MapperMergeConflict(string Key, string CurrentId, string? BaseText, string? DiskText, string? MineText);

/// <summary>
/// 3방향 문장 병합 계획(design.md 5.3). 세 원문 = 기준(세션 <c>Document</c>) · 디스크 · 내 고침(<c>PendingEdits</c>).
/// <see cref="KeepMine"/>·<see cref="KeepMineWith"/> 는 세션 <c>ApplyDiskText</c> 에 그대로 넘기는 지금 id 목록이다.
/// </summary>
/// <param name="DiskChanged">디스크에서 바뀐(새로 생김·지워짐 포함) 기준 쪽 id. 정렬.</param>
/// <param name="KeepMine">충돌 없이 남길 내 고침의 지금 id.</param>
/// <param name="Conflicts">양쪽이 다르게 고친 문장(원문 순서).</param>
/// <param name="NewIdClashes">
/// 내 새 문장과 같은 id 를 디스크가 새로 만들었다. 세션은 새 문장을 늘 남기므로(「파일 것으로」 불가) 묻지 않고 알린다. 저장 때 id 겹침 검사가 쓰기를 막는다.
/// </param>
/// <param name="HasMine">내 고침이 하나라도 있다.</param>
public sealed record MapperMergePlan(
    IReadOnlyList<string> DiskChanged,
    IReadOnlyList<string> KeepMine,
    IReadOnlyList<MapperMergeConflict> Conflicts,
    IReadOnlyList<string> NewIdClashes,
    bool HasMine)
{
    /// <summary>충돌의 답을 더한 keepMine. 답이 없는 충돌은 내 것으로 둔다(고침을 잃지 않는 쪽).</summary>
    public IReadOnlyList<string> KeepMineWith(IReadOnlyDictionary<string, MapperMergeChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var keep = new List<string>(KeepMine);
        foreach (var c in Conflicts)
        {
            if (!choices.TryGetValue(c.Key, out var choice) || choice == MapperMergeChoice.Mine)
            {
                keep.Add(c.CurrentId);
            }
        }

        return keep;
    }
}

/// <summary>3방향 문장 병합 계산(design.md 5.3). 문장 id 로 맞댄다. UI·파일과 무관한 순수 함수.</summary>
public static class MapperMerge
{
    public static MapperMergePlan Plan(IMyBatisMapperDocument baseline, IMyBatisMapperDocument disk, IReadOnlyList<MapperEdit> mine, IMyBatisMapperParser parser)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(mine);
        ArgumentNullException.ThrowIfNull(parser);
        var baseTexts = TextsByKey(baseline);
        var diskTexts = TextsByKey(disk);

        // 열쇠 = (묶음, id): 다른 묶음끼리 같은 id 는 합법이라 따로 본다. 같은 열쇠가 여럿(databaseId 변형)이면 원문 묶음 전체를 견준다.
        bool Changed((string, string) k) =>
            !(baseTexts.TryGetValue(k, out var b) ? b : []).SequenceEqual(diskTexts.TryGetValue(k, out var d) ? d : [], StringComparer.Ordinal);

        string? First(Dictionary<(string, string), List<string>> texts, (string, string) k) => texts.TryGetValue(k, out var list) ? list[0] : null;

        var diskChanged = baseTexts.Keys.Union(diskTexts.Keys).Where(Changed).Select(k => k.Item2).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var keep = new List<string>();
        var conflicts = new List<MapperMergeConflict>();
        var clashes = new List<string>();
        foreach (var edit in mine)
        {
            var key = MapperSavePlan.KeyOf(edit);
            var current = MapperSavePlan.CurrentIdOf(edit, parser);

            // 편집이 가리키는 요소의 묶음: 기준 요소(Apply 가 고르는 것과 같은 Find) 또는 새 문장 원문.
            var element = edit is MapperEdit.InsertElement ins ? MapperSavePlan.ElementOfText(ins.NewText, parser) : baseline.Find(key);
            var group = element is null ? "statement" : GroupOf(element);
            var k = (group, key);
            string? baseText = First(baseTexts, k);
            string? diskText = First(diskTexts, k);
            switch (edit)
            {
                case MapperEdit.InsertElement:
                    if (diskTexts.ContainsKey((group, current)) && !baseTexts.ContainsKey((group, current)))
                    {
                        clashes.Add(current);
                    }

                    keep.Add(current);
                    break;

                case MapperEdit.ReplaceElement when !Changed(k):
                    keep.Add(current);
                    break;

                case MapperEdit.ReplaceElement r when diskText is not null && SameText(diskText, r.NewText) && diskTexts[k].Count == 1:
                    break; // 양쪽이 같게 고쳤다: 디스크 것을 받으면 같은 글이라 고침이 사라진다

                case MapperEdit.ReplaceElement r:
                    conflicts.Add(new MapperMergeConflict(key, current, baseText, diskText, r.NewText));
                    break;

                case MapperEdit.DeleteElement when !Changed(k):
                    keep.Add(current);
                    break;

                case MapperEdit.DeleteElement when diskText is null:
                    break; // 양쪽이 지웠다

                case MapperEdit.DeleteElement:
                    conflicts.Add(new MapperMergeConflict(key, current, baseText, diskText, null));
                    break;

                case MapperEdit.RenameElement n when !Changed(k) && !(diskTexts.ContainsKey((group, n.NewId)) && !baseTexts.ContainsKey((group, n.NewId))):
                    keep.Add(current);
                    break;

                case MapperEdit.RenameElement n:
                    conflicts.Add(new MapperMergeConflict(key, current, baseText, diskText, RenamedText(baseText, n)));
                    break;
            }
        }

        return new MapperMergePlan(diskChanged, keep, conflicts, clashes, mine.Count > 0);
    }

    private static Dictionary<(string, string), List<string>> TextsByKey(IMyBatisMapperDocument doc)
    {
        var map = new Dictionary<(string, string), List<string>>();
        foreach (var e in doc.Elements)
        {
            if (e.Id is not { } id)
            {
                continue;
            }

            var k = (GroupOf(e), id);
            if (!map.TryGetValue(k, out var list))
            {
                map[k] = list = [];
            }

            list.Add(e.Text);
        }

        return map;
    }

    // Apply·Check 와 같은 묶음: 문장 4종 · sql · 그 밖은 요소 이름.
    private static string GroupOf(MyBatisElement e) => e.IsStatement ? "statement" : e.Kind == MyBatisElementKind.Sql ? "sql" : e.Name;

    // 편집기 글은 LF 일 수 있으므로 줄 끝을 빼고 견준다(Apply 가 문서 줄 끝으로 맞춘다).
    private static bool SameText(string a, string b) =>
        string.Equals(a.ReplaceLineEndings("\n"), b.ReplaceLineEndings("\n"), StringComparison.Ordinal);

    // 나란히 보기용: 기준 원문의 첫 id="…" 값만 바꾼 글(정확한 Rename 규칙은 Apply 가 갖는다).
    private static string? RenamedText(string? baseText, MapperEdit.RenameElement rename)
    {
        if (baseText is null)
        {
            return null;
        }

        foreach (var quote in new[] { "\"", "'" })
        {
            var needle = "id=" + quote + rename.Id + quote;
            int at = baseText.IndexOf(needle, StringComparison.Ordinal);
            if (at >= 0)
            {
                return baseText[..at] + "id=" + quote + rename.NewId + quote + baseText[(at + needle.Length)..];
            }
        }

        return baseText;
    }
}
