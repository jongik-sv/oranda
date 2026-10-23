using Oranda.App.Features.SqlEditor;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Workspace;

// MyBatisWorkspaceSession 의 일부: 복구(F5) 입구 · 요소 만들기 · 원문 해석 도우미(MyBatisWorkspaceSession.cs 에서 나눔, 동작 불변).
public sealed partial class MyBatisWorkspaceSession
{
    /// <summary>
    /// 복구(F5): 이미 열린 탭(작업 공간 저장본에서 되살린 문장 탭)을 요소에 잇는다. 탭 글이 지금 원문이 된다.
    /// <paramref name="fileSpace"/> = 탭 글이 옛 형식(파일 들여쓰기가 남은 원문, 곁 파일 판 1)이다: 그 요소는 원문 그대로 보인다(접두 없음).
    /// 형식과 상관없이 탭 글이 기준 원문(파일 공간) 그대로면 고치지 않은 탭이므로 탭 공간 기준 글로 다시 싣는다
    /// (형식을 모르는 입구 — 곁 파일 없음·깨짐 — 에서 접두가 두 번 붙지 않게 하는 안전장치).
    /// </summary>
    internal void AdoptTab(MyBatisStatementEntry entry, SqlEditorViewModel tab, bool fileSpace = false)
    {
        if (entry.Tab is not null || EntryOf(tab) is not null)
        {
            return;
        }

        if (entry.Baseline is { } baseline && entry.BaselineText is { } view && !string.Equals(view, baseline.Text, StringComparison.Ordinal)
            && string.Equals(tab.Text, baseline.Text, StringComparison.Ordinal))
        {
            tab.LoadText(view);
        }
        else if (fileSpace && entry.Indent is not { Length: 0 })
        {
            entry.SetIndent(string.Empty);
        }

        tab.Title = entry.Id ?? tab.Title;
        Track(entry, tab);
    }

    /// <summary>
    /// 복구(F5): 어느 요소에도 이어지지 않은 탭(곁 파일과 작업 공간 저장본의 시차, 같은 작업 공간 다시 복원)을 탭 제목 = id 로 잇는다.
    /// 맞는 요소가 없으면 탭 글을 새 문장으로 되살린다(id 를 읽을 수 있을 때). <paramref name="fileSpace"/> 는 <see cref="AdoptTab"/> 과 같다.
    /// </summary>
    internal void AdoptOrphanTabs(bool fileSpace = false)
    {
        foreach (var tab in _workspace.Tabs.Where(t => EntryOf(t) is null).ToList())
        {
            if (_entries.FirstOrDefault(e => e.Tab is null && !e.IsDeleted && e.CanOpen && string.Equals(e.Id, tab.Title, StringComparison.Ordinal)) is { } entry)
            {
                if (!entry.IsAmbiguous)
                {
                    entry.StoredText = tab.Text;
                }

                AdoptTab(entry, tab, fileSpace);
            }
            else if (MyBatisStatementText.TryFindIdValue(tab.Text, out var start, out var length)
                && tab.Text.Substring(start, length) is var id && ValidateId(id, null) is null
                && MyBatisStatementText.KindOfText(tab.Text) is { } kind)
            {
                var created = new MyBatisStatementEntry(null, kind, id, tab.Text, ambiguous: false, indent: fileSpace ? string.Empty : null);
                var at = _entries.FindLastIndex(e => e.Kind == kind);
                _entries.Insert(at < 0 ? _entries.Count : at + 1, created);
                AdoptTab(created, tab);
            }
        }

        RaiseStateChanged();
    }

    /// <summary>복구(F5): 저장해 둔 상태로 요소를 바꾼다. 기준 문서·탭은 건드리지 않는다.</summary>
    internal void RestoreEntries(IEnumerable<MyBatisStatementEntry> entries, int? selectedIndex)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        _selected = selectedIndex is int i && i >= 0 && i < _entries.Count ? _entries[i] : null;
        RaiseStateChanged();
    }

    /// <summary>
    /// 복구(F5): 파일 공간 원문으로 요소를 새로 만든다(기준 요소면 그 줄 앞 공백을 걷어 낸다, <see cref="FromFile"/>). 새 문장은 원문 그대로(접두 없음).
    /// </summary>
    internal static MyBatisStatementEntry NewEntry(IMyBatisMapperDocument document, MyBatisElement? baseline, MyBatisStatementKind kind, string? id, string fileText) =>
        FromFile(document, baseline, kind, id, fileText, ambiguous: baseline is not null && IsAmbiguous(document, baseline));

    /// <summary>복구(F5): 탭 공간 원문과 저장해 둔 접두로 요소를 만든다(곁 파일 판 2).</summary>
    internal static MyBatisStatementEntry RestoreEntry(IMyBatisMapperDocument document, MyBatisElement? baseline, MyBatisStatementKind kind, string? id, string text, string? indent) =>
        new(baseline, kind, id, text, ambiguous: baseline is not null && IsAmbiguous(document, baseline), indent);

    /// <summary>
    /// 파일 공간 원문 → 요소. 기준 요소면 여는 태그 줄 앞 공백을 접두로 둘째 줄부터 걷어 내되, 다시 붙여 원문 바이트 그대로 돌아올 때만 그렇게 한다
    /// (덜 들여 쓴 줄·공백만 있는 줄이 있으면 원문 그대로, 접두 빈 글).
    /// </summary>
    private static MyBatisStatementEntry FromFile(IMyBatisMapperDocument document, MyBatisElement? baseline, MyBatisStatementKind kind, string? id, string fileText, bool ambiguous)
    {
        var indent = baseline is null ? string.Empty : MyBatisStatementText.IndentBefore(document.Text, baseline.ElementRange.Start);
        return MyBatisStatementText.TryDedent(fileText, indent, out var text)
            ? new MyBatisStatementEntry(baseline, kind, id, text, ambiguous, indent)
            : new MyBatisStatementEntry(baseline, kind, id, fileText, ambiguous, string.Empty);
    }

    /// <summary>
    /// 글 원문으로 새 기준 문서를 읽되 연 때의 BOM 을 지킨다(<c>Parse(string)</c> 은 BOM 을 늘 없음으로 본다, 02-01 후속 메모).
    /// BOM 은 UTF-8 에만 있으므로 UTF-8 바이트로 읽는다. 다른 인코딩은 xml 선언에서 그대로 정해진다.
    /// </summary>
    internal static IMyBatisMapperDocument ParseLike(string text, MapperFileFormat format)
    {
        var parser = new MapperParser();
        if (!format.HasBom)
        {
            return parser.Parse(text);
        }

        var bom = System.Text.Encoding.UTF8.GetPreamble();
        var body = System.Text.Encoding.UTF8.GetBytes(text);
        var bytes = new byte[bom.Length + body.Length];
        bom.CopyTo(bytes, 0);
        body.CopyTo(bytes, bom.Length);
        return parser.Parse(bytes);
    }

    /// <summary>
    /// 02-01 Apply 가 id 로 이 요소를 하나로 고를 수 없다: 같은 id 를 문장 → sql → 그 밖(요소 이름) 순 묶음에서 찾으므로,
    /// 그 묶음에 같은 id 가 여럿(databaseId 만 다름 등)이거나, sql 조각인데 같은 id 문장이 있으면 편집하지 않는다.
    /// </summary>
    private static bool IsAmbiguous(IMyBatisMapperDocument document, MyBatisElement element)
    {
        if (element.Id is not { } id)
        {
            return false;
        }

        var same = document.Elements.Where(e => string.Equals(e.Id, id, StringComparison.Ordinal)).ToList();
        if (same.Count <= 1)
        {
            return false;
        }

        var group = same.Exists(e => e.IsStatement) ? "statement" : same.Exists(e => e.Kind == MyBatisElementKind.Sql) ? "sql" : GroupOf(same[0]);
        return GroupOf(element) != group || same.Count(e => GroupOf(e) == group) > 1;
    }

    private static string GroupOf(MyBatisElement e) => e.IsStatement ? "statement" : e.Kind == MyBatisElementKind.Sql ? "sql" : e.Name;

    private static IMyBatisMapperDocument ParseText(string text) => new MapperParser().Parse(text);

    /// <summary>
    /// 새 문장을 넣을 자리의 들여쓰기: Apply 가 첫 줄에 붙이는 것과 같다(AfterId 요소의 줄 앞 공백, AfterId 가 없으면 마지막 요소, 요소가 없으면 2칸).
    /// 둘째 줄부터 같은 접두를 붙여야 저장 뒤 다시 읽을 때 같은 탭 글로 돌아온다.
    /// </summary>
    private string InsertIndent(int index)
    {
        var el = InsertAnchor(index) is { } after ? _document.Find(after) : _document.Elements.Count > 0 ? _document.Elements[^1] : null;
        return el is null ? "  " : MyBatisStatementText.IndentBefore(_document.Text, el.ElementRange.Start);
    }

    /// <summary>원문은 그대로 두고 id 만 바꾼 고침인가(RenameElement 로 저장한다). 같은 공간끼리 견준다.</summary>
    private static bool IsRenameOnly(MyBatisStatementEntry e) =>
        e.BaselineText is { } baseline
            ? string.Equals(MyBatisStatementText.RenameId(baseline, e.Id!), e.Text, StringComparison.Ordinal)
            : string.Equals(MyBatisStatementText.RenameId(e.Baseline!.Text, e.Id!), e.FileText, StringComparison.Ordinal);

    private static MyBatisStatementKind KindFromName(string name) => name switch
    {
        "select" => MyBatisStatementKind.Select,
        "insert" => MyBatisStatementKind.Insert,
        "update" => MyBatisStatementKind.Update,
        "delete" => MyBatisStatementKind.Delete,
        _ => MyBatisStatementKind.Sql,
    };
}
