using System.Collections.Specialized;
using System.ComponentModel;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Workspace;

/// <summary>
/// MyBatis 작업 공간 하나의 상태(design.md 2.2·9장, MB-03). 열기 훅(<see cref="MyBatisOpenHandler"/>)이 만들어 작업 공간에 붙인다.
/// 기준 문서(연 때·마지막 저장)와 요소마다의 지금 상태(<see cref="MyBatisStatementEntry"/>)를 들고, 문장 탭 ↔ 문장을 잇는다.
/// 문장 탭의 <c>IsDirty</c> 는 「이 문장이 기준 원문과 다름」으로 맞춘다. 탭을 닫아도 고침은 세션에 남는다. 매퍼 파일에는 쓰지 않는다(저장은 03-03).
/// UI 스레드에서 쓴다.
/// </summary>
public sealed class MyBatisWorkspaceSession : IMyBatisWorkspaceSession
{
    private readonly SqlWorkspaceViewModel _workspace;
    private readonly Func<string, IMyBatisMapperDocument> _parse;
    private readonly List<MyBatisStatementEntry> _entries = [];
    private readonly SessionFragments _fragments;
    private IMyBatisMapperDocument _document;
    private MyBatisStatementEntry? _selected;
    private bool _noneSelected;
    private bool _syncingTabs;

    /// <param name="workspace">세션을 붙일 작업 공간(붙이는 일은 부르는 쪽이 한다).</param>
    /// <param name="mapperPath">매퍼 파일 절대 경로.</param>
    /// <param name="document">파일 바이트로 읽은 기준 문서(<c>Parse(bytes)</c>, 02-01 후속 메모).</param>
    /// <param name="parse">글자 원문 파서(저장·병합 뒤 새 기준, 고친 문장의 속성 읽기). 기본 = 02-01 <see cref="MapperParser"/>.</param>
    public MyBatisWorkspaceSession(SqlWorkspaceViewModel workspace, string mapperPath, IMyBatisMapperDocument document, Func<string, IMyBatisMapperDocument>? parse = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(mapperPath);
        ArgumentNullException.ThrowIfNull(document);
        _workspace = workspace;
        MapperPath = mapperPath;
        _document = document;
        _parse = parse ?? ParseText;
        _fragments = new SessionFragments(this);
        LoadEntries(document);
        _workspace.Tabs.CollectionChanged += OnWorkspaceTabsChanged;
        _workspace.PropertyChanged += OnWorkspacePropertyChanged;
    }

    public string MapperPath { get; private set; }

    public string? MapperNamespace => _document.MapperNamespace;

    public IMyBatisMapperDocument Document => _document;

    public string BaselineHash => _document.Hash;

    public bool IsDirty => _entries.Any(e => e.IsModified);

    public string? SelectedStatementId => SelectedEntry?.Id;

    /// <summary>고른 요소(「문장」 창에서 고른 것 또는 활성 문장 탭의 것). 같은 id 가 여럿이어도 그 요소 자체다.</summary>
    public MyBatisStatementEntry? SelectedEntry =>
        _noneSelected ? null
            : _selected is { IsDeleted: false } picked && _entries.Contains(picked) ? picked
            : _workspace.ActiveTab is { } tab ? EntryOf(tab) : null;

    public IMyBatisFragmentResolver Fragments => _fragments;

    /// <summary>세션이 붙은 작업 공간.</summary>
    public SqlWorkspaceViewModel Workspace => _workspace;

    /// <summary>요소 목록(기준 문서 순서, 새 문장은 같은 종류의 마지막 뒤). 지운 요소도 들어 있다(<see cref="MyBatisStatementEntry.IsDeleted"/>).</summary>
    public IReadOnlyList<MyBatisStatementEntry> Entries => _entries;

    /// <summary>「문장」 창에 보일 요소(지우지 않은 것).</summary>
    public IEnumerable<MyBatisStatementEntry> VisibleEntries => _entries.Where(e => !e.IsDeleted);

    public event EventHandler? StateChanged;

    public string? StatementIdOf(SqlEditorViewModel tab) => _entries.FirstOrDefault(e => ReferenceEquals(e.Tab, tab))?.Id;

    /// <summary>탭이 보이는 요소. 이 세션의 문장 탭이 아니면 null.</summary>
    public MyBatisStatementEntry? EntryOf(SqlEditorViewModel tab) => _entries.FirstOrDefault(e => ReferenceEquals(e.Tab, tab));

    /// <summary>
    /// 지금 id 로 요소를 찾는다(지운 요소는 빼고). 같은 id 면 열 수 있는 요소(문장·sql 조각)를 먼저, 그다음 원문 순서.
    /// databaseId 만 다른 같은 id 문장처럼 하나로 고를 수 없으면 첫 요소다 — 화면은 <see cref="OpenEntry"/>·<see cref="SelectEntry"/> 로 요소를 직접 넘긴다.
    /// </summary>
    public MyBatisStatementEntry? Find(string? id) =>
        id is null ? null
            : _entries.FirstOrDefault(e => !e.IsDeleted && e.CanOpen && string.Equals(e.Id, id, StringComparison.Ordinal))
                ?? _entries.FirstOrDefault(e => !e.IsDeleted && string.Equals(e.Id, id, StringComparison.Ordinal));

    public MyBatisElement? ElementOf(string statementId)
    {
        if (Find(statementId) is not { } entry)
        {
            return null;
        }

        var text = entry.Text;
        if (entry.Baseline is { } baseline && string.Equals(baseline.Text, text, StringComparison.Ordinal))
        {
            return baseline;
        }

        // 고친 원문은 감싼 매퍼로 다시 읽어 속성을 맞춘다. 입력 중이라 깨졌으면 기준 속성을 그대로 쓴다.
        try
        {
            var parsed = _parse("<mapper>" + text + "</mapper>").Elements;
            if (parsed.Count == 1)
            {
                return parsed[0] with
                {
                    Text = text, // 탭 글 그대로(여는 태그 앞뒤 주석·공백까지, 계산 범위의 기준)
                    ElementRange = entry.Baseline?.ElementRange ?? default,
                    BodyRange = entry.Baseline?.BodyRange ?? default,
                };
            }
        }
        catch (FormatException)
        {
        }

        return (entry.Baseline ?? new MyBatisElement
        {
            Kind = MyBatisStatementEntry.ElementKindOf(entry.Kind),
            Name = entry.Name,
            Text = text,
        }) with { Id = entry.Id, Text = text };
    }

    public IReadOnlyList<MapperEdit> PendingEdits()
    {
        var edits = new List<MapperEdit>();
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (!e.IsModified || e.IsAmbiguous)
            {
                continue;
            }

            if (e.IsNew)
            {
                edits.Add(new MapperEdit.InsertElement(e.Id!, e.Text, InsertAnchor(i)));
            }
            else if (e.IsDeleted)
            {
                edits.Add(new MapperEdit.DeleteElement(e.OriginalId!));
            }
            else if (!string.Equals(e.Id, e.OriginalId, StringComparison.Ordinal)
                && string.Equals(MyBatisStatementText.RenameId(e.Baseline!.Text, e.Id!), e.Text, StringComparison.Ordinal))
            {
                edits.Add(new MapperEdit.RenameElement(e.OriginalId!, e.Id!));
            }
            else
            {
                edits.Add(new MapperEdit.ReplaceElement(e.OriginalId!, e.Text));
            }
        }

        return edits;
    }

    public void SelectStatement(string? statementId) => SelectEntry(Find(statementId));

    /// <summary>요소를 고른다(null = 고른 것 없음). 「문장」 창은 같은 id 요소를 가리지 않도록 이것을 쓴다.</summary>
    public void SelectEntry(MyBatisStatementEntry? entry)
    {
        if (ReferenceEquals(_selected, entry) && _noneSelected == (entry is null))
        {
            return;
        }

        // null = 「문장」 창에서 묶음 머리·id 없는 요소를 골랐다: 활성 탭의 문장으로 돌아가지 않고 고른 것 없음이다(명령이 엉뚱한 문장을 집지 않게).
        _selected = entry;
        _noneSelected = entry is null;
        RaiseStateChanged();
    }

    public SqlEditorViewModel? OpenStatement(string statementId) => Find(statementId) is { } entry ? OpenEntry(entry) : null;

    /// <summary>요소의 문장 탭을 연다(이미 있으면 앞으로). 열 수 없는 요소(resultMap 등)면 null.</summary>
    public SqlEditorViewModel? OpenEntry(MyBatisStatementEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry is not { CanOpen: true, IsDeleted: false } || !_entries.Contains(entry))
        {
            return null;
        }

        var host = _workspace.Host;
        host.ActiveWorkspace = _workspace;
        if (entry.Tab is { } open)
        {
            _workspace.ActiveTab = open;
            return open;
        }

        var tab = new SqlEditorViewModel(Guid.NewGuid(), entry.StoredText)
        {
            Title = entry.Id!,
            IsReadOnly = !entry.IsEditable,
        };
        Track(entry, tab);
        host.AddMyBatisTab(_workspace, tab);
        return tab;
    }

    public async Task BeginNewStatementAsync(CancellationToken ct)
    {
        var dialogs = _workspace.Host.Dialogs;
        string[] kinds = ["select", "insert", "update", "delete", "sql"];
        var kind = await dialogs.ChooseAsync("새 문장 종류", kinds, ct).ConfigureAwait(true);
        if (kind is null)
        {
            return;
        }

        var id = await dialogs.PromptAsync("새 문장 id", string.Empty, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        id = id.Trim();
        if (ValidateId(id, null) is { } problem)
        {
            await dialogs.ShowMessageAsync("새 문장", problem, ct).ConfigureAwait(true);
            return;
        }

        CreateStatement(KindFromName(kind), id);
    }

    /// <summary>새 문장을 만들고 그 탭을 연다(같은 종류의 마지막 요소 뒤에 둔다). id 가 맞지 않으면 <see cref="ArgumentException"/>.</summary>
    public SqlEditorViewModel CreateStatement(MyBatisStatementKind kind, string id)
    {
        if (ValidateId(id, null) is { } problem)
        {
            throw new ArgumentException(problem, nameof(id));
        }

        var at = _entries.FindLastIndex(e => e.Kind == kind);
        var text = MyBatisStatementText.NewStatement(kind, id, _document.Format.NewLine, IndentOfElements(at >= 0 ? _entries[at].Baseline : null));
        var entry = new MyBatisStatementEntry(null, kind, id, text, ambiguous: false);
        _entries.Insert(at < 0 ? _entries.Count : at + 1, entry);
        RaiseStateChanged();
        return OpenEntry(entry)!;
    }

    /// <summary>문장 id 를 바꾼다: 원문의 id 속성과 탭 제목을 함께 바꾼다(열린 탭이면 실행 취소할 수 있는 편집으로).</summary>
    public void RenameStatement(string id, string newId) =>
        RenameEntry(Find(id) ?? throw new ArgumentException($"바꿀 수 없는 문장입니다: {id}", nameof(id)), newId);

    /// <summary>
    /// 요소의 id 를 바꾼다. 원문에서 여는 태그의 id 속성을 찾지 못하면(깨진 원문) 바꾸지 않고 <see cref="ArgumentException"/> —
    /// 글은 그대로인데 목록 id 만 바뀌면 저장이 옛 id 를 쓰기 때문이다.
    /// </summary>
    public void RenameEntry(MyBatisStatementEntry entry, string newId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsEditable || entry.IsDeleted)
        {
            throw new ArgumentException($"바꿀 수 없는 문장입니다: {entry.Id}", nameof(entry));
        }

        if (ValidateId(newId, entry) is { } problem)
        {
            throw new ArgumentException(problem, nameof(newId));
        }

        if (!MyBatisStatementText.TryFindIdValue(entry.Text, out var start, out var length))
        {
            throw new ArgumentException($"{entry.Id} 의 여는 태그에서 id 속성을 찾지 못했습니다.", nameof(entry));
        }

        entry.Id = newId;
        if (entry.Tab is { } tab)
        {
            tab.Title = newId;
            tab.Document.Replace(start, length, newId); // 실행 취소할 수 있는 편집. 글이 바뀌면 OnTabTextChanged 가 id 를 다시 읽는다
        }
        else
        {
            entry.StoredText = string.Concat(entry.StoredText.AsSpan(0, start), newId, entry.StoredText.AsSpan(start + length));
        }

        RaiseStateChanged();
    }

    /// <summary>문장을 지운다: 탭을 닫고 지움으로 표시한다(새 문장이면 목록에서 뺀다). 매퍼에는 저장 때 반영된다.</summary>
    public void DeleteStatement(string id) =>
        DeleteEntry(Find(id) ?? throw new ArgumentException($"지울 수 없는 문장입니다: {id}", nameof(id)));

    /// <summary>요소를 지운다(<see cref="DeleteStatement"/> 와 같고 요소를 직접 받는다).</summary>
    public void DeleteEntry(MyBatisStatementEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsEditable || entry.IsDeleted)
        {
            throw new ArgumentException($"지울 수 없는 문장입니다: {entry.Id}", nameof(entry));
        }

        if (entry.Tab is { } tab)
        {
            _workspace.Host.Close(tab); // 닫힘 사건이 글을 StoredText 에 남긴다
        }

        if (entry.IsNew)
        {
            _entries.Remove(entry);
        }
        else
        {
            entry.IsDeleted = true;
        }

        if (ReferenceEquals(_selected, entry))
        {
            _selected = null;
        }

        RaiseStateChanged();
    }

    /// <summary>새 id 가 쓸 수 있는지. 문제가 있으면 알림 글, 없으면 null. <paramref name="self"/> 는 이름을 바꾸는 요소(자기 id 와는 겹쳐도 됨).</summary>
    public string? ValidateId(string? id, MyBatisStatementEntry? self)
    {
        if (!MyBatisStatementText.IsValidId(id))
        {
            return "id 는 영문·숫자·밑줄로 쓰고 숫자로 시작하지 않습니다.";
        }

        // 문장·sql 조각은 같은 id 를 함께 쓰면 02-01 Apply 가 문장 쪽을 먼저 고르므로 한 공간으로 본다. resultMap 등과는 겹쳐도 된다.
        return _entries.Any(e => !e.IsDeleted && !ReferenceEquals(e, self) && e.CanOpen && string.Equals(e.Id, id, StringComparison.Ordinal))
            ? $"id 가 {id} 인 문장·sql 조각이 이미 있습니다."
            : null;
    }

    /// <summary>sql 조각을 끼우는 include 수(같은 매퍼, 지금 원문 기준).</summary>
    public int UsageCount(string fragmentId) =>
        _entries.Where(e => !e.IsDeleted && !(e.Kind == MyBatisStatementKind.Sql && string.Equals(e.Id, fragmentId, StringComparison.Ordinal)))
            .Sum(e => MyBatisStatementText.CountIncludes(e.Text, fragmentId, MapperNamespace));

    /// <summary>같은 매퍼 sql 조각 id → include 수(지금 원문 기준, 매퍼를 한 번 훑는다). 「문장」 창이 조각마다 다시 훑지 않게 쓴다.</summary>
    public IReadOnlyDictionary<string, int> UsageCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in _entries.Where(e => !e.IsDeleted))
        {
            MyBatisStatementText.AddIncludes(e.Text, MapperNamespace, counts);
        }

        return counts;
    }

    public void Relocate(string newMapperPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(newMapperPath);
        MapperPath = newMapperPath;
        _workspace.SourcePath = newMapperPath;
        RaiseStateChanged();
    }

    public void Rebase(string newText, IReadOnlyCollection<string> writtenStatementIds)
    {
        ArgumentNullException.ThrowIfNull(newText);
        ArgumentNullException.ThrowIfNull(writtenStatementIds);
        Rebuild(ParseLike(newText, _document.Format), keepMine: e => e.Id is not { } id || !writtenStatementIds.Contains(id), takeDisk: false);
    }

    public void ApplyDiskText(string diskText, IReadOnlyCollection<string> keepMine)
    {
        ArgumentNullException.ThrowIfNull(diskText);
        ArgumentNullException.ThrowIfNull(keepMine);
        // 저장 안 한 새 문장은 디스크에 없으므로 keepMine 과 상관없이 남긴다.
        Rebuild(ParseLike(diskText, _document.Format), keepMine: e => e.IsNew || (e.Id is { } id && keepMine.Contains(id)), takeDisk: true);
    }

    /// <summary>작업 공간을 닫을 때 부른다: 탭·작업 공간 구독을 푼다.</summary>
    public void Detach()
    {
        _workspace.Tabs.CollectionChanged -= OnWorkspaceTabsChanged;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        foreach (var e in _entries.Where(e => e.Tab is not null))
        {
            Untrack(e);
        }
    }

    /// <summary>복구(F5): 이미 열린 탭(작업 공간 저장본에서 되살린 문장 탭)을 요소에 잇는다. 탭 글이 지금 원문이 된다.</summary>
    internal void AdoptTab(MyBatisStatementEntry entry, SqlEditorViewModel tab)
    {
        if (entry.Tab is not null || EntryOf(tab) is not null)
        {
            return;
        }

        tab.Title = entry.Id ?? tab.Title;
        Track(entry, tab);
    }

    /// <summary>
    /// 복구(F5): 어느 요소에도 이어지지 않은 탭(곁 파일과 작업 공간 저장본의 시차, 같은 작업 공간 다시 복원)을 탭 제목 = id 로 잇는다.
    /// 맞는 요소가 없으면 탭 글을 새 문장으로 되살린다(id 를 읽을 수 있을 때).
    /// </summary>
    internal void AdoptOrphanTabs()
    {
        foreach (var tab in _workspace.Tabs.Where(t => EntryOf(t) is null).ToList())
        {
            if (_entries.FirstOrDefault(e => e.Tab is null && !e.IsDeleted && e.CanOpen && string.Equals(e.Id, tab.Title, StringComparison.Ordinal)) is { } entry)
            {
                if (!entry.IsAmbiguous)
                {
                    entry.StoredText = tab.Text;
                }

                AdoptTab(entry, tab);
            }
            else if (MyBatisStatementText.TryFindIdValue(tab.Text, out var start, out var length)
                && tab.Text.Substring(start, length) is var id && ValidateId(id, null) is null
                && MyBatisStatementText.KindOfText(tab.Text) is { } kind)
            {
                var created = new MyBatisStatementEntry(null, kind, id, tab.Text, ambiguous: false);
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

    /// <summary>복구(F5): 기준 원문으로 요소를 새로 만든다(복구 참여자가 곁 파일을 맞대기 전 상태).</summary>
    internal static MyBatisStatementEntry NewEntry(IMyBatisMapperDocument document, MyBatisElement? baseline, MyBatisStatementKind kind, string? id, string text) =>
        new(baseline, kind, id, text, ambiguous: baseline is not null && IsAmbiguous(document, baseline));

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

    /// <summary>새 문장 들여쓰기: 바로 앞에 둘 기준 요소(없으면 첫 문장, 그것도 없으면 첫 요소)가 놓인 줄의 앞 공백. Apply 도 앞 요소를 따른다.</summary>
    private string IndentOfElements(MyBatisElement? anchor)
    {
        var el = anchor ?? _document.Elements.FirstOrDefault(e => e.IsStatement) ?? (_document.Elements.Count > 0 ? _document.Elements[0] : null);
        if (el is null)
        {
            return "  ";
        }

        var text = _document.Text;
        int start = Math.Clamp(el.ElementRange.Start, 0, text.Length);
        int lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        int k = lineStart;
        while (k < start && (text[k] == ' ' || text[k] == '\t'))
        {
            k++;
        }

        return text[lineStart..k];
    }

    private static MyBatisStatementKind KindFromName(string name) => name switch
    {
        "select" => MyBatisStatementKind.Select,
        "insert" => MyBatisStatementKind.Insert,
        "update" => MyBatisStatementKind.Update,
        "delete" => MyBatisStatementKind.Delete,
        _ => MyBatisStatementKind.Sql,
    };

    private void LoadEntries(IMyBatisMapperDocument document)
    {
        _entries.Clear();
        var counts = document.Elements.Where(e => e.Id is not null).GroupBy(e => e.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var el in document.Elements)
        {
            bool ambiguous = el.Id is { } id && counts[id] > 1 && IsAmbiguous(document, el);
            _entries.Add(new MyBatisStatementEntry(el, MyBatisStatementEntry.KindOf(el.Kind), el.Id, el.Text, ambiguous));
        }
    }

    /// <summary>
    /// 새 기준 문서로 요소를 다시 짓는다. <paramref name="keepMine"/> 가 참인 고친 요소는 내 상태(원문·id·지움)를 남기고,
    /// 나머지는 새 기준 것이 된다. 열린 탭은 새 요소에 다시 잇고, 사라진 요소의 탭은 닫는다.
    /// <paramref name="takeDisk"/> = 밖에서 바뀐 디스크(5.3): 요소를 기준 id 로 맞댄다. 거짓 = 저장 뒤(Rebase): 저장한 요소는 지금 id 로 맞댄다.
    /// </summary>
    private void Rebuild(IMyBatisMapperDocument document, Func<MyBatisStatementEntry, bool> keepMine, bool takeDisk)
    {
        var old = _entries.ToList();
        var oldSelected = _selected;
        _selected = null;
        _document = document;
        LoadEntries(document);
        var taken = new HashSet<MyBatisStatementEntry>(ReferenceEqualityComparer.Instance);
        foreach (var prev in old)
        {
            bool mine = prev.IsModified && keepMine(prev);
            var lookup = !takeDisk && !mine && prev.IsModified ? prev.Id : prev.OriginalId;

            // 같은 id(databaseId 변형)는 원문 순서대로 하나씩 맞댄다.
            var target = lookup is null ? null
                : _entries.FirstOrDefault(e => !taken.Contains(e) && string.Equals(e.OriginalId, lookup, StringComparison.Ordinal)
                    && (prev.IsNew || e.Kind == prev.Kind));
            if (target is not null)
            {
                taken.Add(target);
            }

            if (mine && !(prev.IsDeleted && target is null))
            {
                if (target is null)
                {
                    // 새 문장(또는 새 기준에서 사라진 문장)은 새 문장으로 남긴다.
                    target = new MyBatisStatementEntry(null, prev.Kind, prev.Id, prev.Text, ambiguous: false);
                    var at = _entries.FindLastIndex(e => e.Kind == prev.Kind);
                    _entries.Insert(at < 0 ? _entries.Count : at + 1, target);
                    taken.Add(target);
                }
                else
                {
                    target.Id = prev.Id;
                    target.IsDeleted = prev.IsDeleted;
                    target.StoredText = prev.Text;
                }
            }

            if (ReferenceEquals(prev, oldSelected) && target is { IsDeleted: false })
            {
                _selected = target;
            }

            if (prev.Tab is not { } tab)
            {
                continue;
            }

            Untrack(prev);
            if (target is null || target.IsDeleted)
            {
                _syncingTabs = true;
                try
                {
                    _workspace.Host.Close(tab);
                }
                finally
                {
                    _syncingTabs = false;
                }

                continue;
            }

            if (!string.Equals(tab.Text, target.StoredText, StringComparison.Ordinal))
            {
                tab.LoadText(target.StoredText);
            }

            Track(target, tab);
        }

        RaiseStateChanged();
    }

    /// <summary>InsertElement 의 AfterId: 앞쪽에서 가장 가까운, 지우지 않았고 id 로 고를 수 있는 기준 요소.</summary>
    private string? InsertAnchor(int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            var e = _entries[i];
            if (!e.IsNew && !e.IsDeleted && !e.IsAmbiguous && e.OriginalId is not null)
            {
                return e.OriginalId;
            }
        }

        return null;
    }

    private void Track(MyBatisStatementEntry entry, SqlEditorViewModel tab)
    {
        entry.Tab = tab;
        tab.IsReadOnly = !entry.IsEditable;
        tab.TextChanged += OnTabTextChanged;
        tab.IsDirty = entry.IsModified;
    }

    private void Untrack(MyBatisStatementEntry entry)
    {
        if (entry.Tab is { } tab)
        {
            tab.TextChanged -= OnTabTextChanged;
            entry.StoredText = tab.Text;
            entry.Tab = null;
        }
    }

    private void OnTabTextChanged(object? sender, EventArgs e)
    {
        if (sender is SqlEditorViewModel tab && EntryOf(tab) is { } entry)
        {
            // 탭에서 id 속성을 직접 고치거나 id 바꾸기를 실행 취소하면 목록 id 도 따라간다. 쓸 수 없는 id(입력 중·겹침)면 마지막 id 를 둔다.
            if (entry.IsEditable && MyBatisStatementText.TryFindIdValue(tab.Text, out var start, out var length)
                && tab.Text.AsSpan(start, length) is var span && !span.SequenceEqual(entry.Id)
                && span.ToString() is var typed && ValidateId(typed, entry) is null)
            {
                entry.Id = typed;
                tab.Title = typed;
            }

            tab.IsDirty = entry.IsModified; // 탭 VM 은 글이 바뀌면 늘 dirty 로 둔다 → 기준과 같아지면 다시 내린다
            RaiseStateChanged();
        }
    }

    private void OnWorkspaceTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_syncingTabs)
        {
            return;
        }

        foreach (var tab in e.OldItems?.OfType<SqlEditorViewModel>() ?? [])
        {
            // 탭 순서 바꾸기(Move)도 OldItems 에 그 탭이 온다: 작업 공간에 남아 있으면 그대로 잇는다.
            if (!_workspace.Tabs.Contains(tab) && EntryOf(tab) is { } entry)
            {
                Untrack(entry); // 탭을 닫아도 고침은 남는다(design.md 2.2)
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var entry in _entries.Where(x => x.Tab is not null && !_workspace.Tabs.Contains(x.Tab)).ToList())
            {
                Untrack(entry);
            }
        }
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlWorkspaceViewModel.MyBatis) && !ReferenceEquals(_workspace.MyBatis, this))
        {
            Detach(); // 작업 공간을 닫으면 호스트가 세션을 뗀다
        }
        else if (e.PropertyName == nameof(SqlWorkspaceViewModel.ActiveTab))
        {
            // 고른 문장은 활성 문장 탭을 따라간다(문장 창에서 고른 뒤 다른 탭으로 가면 그 탭의 문장).
            _selected = _workspace.ActiveTab is { } tab && EntryOf(tab) is { } entry ? entry : _selected;
            _noneSelected = false;
            RaiseStateChanged();
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>같은 매퍼 → 등록된 해석기(03-07 폴더 등) 순으로 sql 조각을 찾는다.</summary>
    private sealed class SessionFragments(MyBatisWorkspaceSession session) : IMyBatisFragmentResolver
    {
        public MyBatisFragment? Resolve(string refid, string? currentNamespace)
        {
            ArgumentNullException.ThrowIfNull(refid);
            var ns = session.MapperNamespace;
            var local = refid;
            if (ns is not null && refid.Length > ns.Length + 1 && refid.StartsWith(ns, StringComparison.Ordinal) && refid[ns.Length] == '.')
            {
                local = refid[(ns.Length + 1)..];
            }

            bool sameMapper = !local.Contains('.', StringComparison.Ordinal) && (currentNamespace is null || string.Equals(currentNamespace, ns, StringComparison.Ordinal) || !ReferenceEquals(local, refid));
            if (sameMapper && session._entries.FirstOrDefault(e => e.Kind == MyBatisStatementKind.Sql && !e.IsDeleted && string.Equals(e.Id, local, StringComparison.Ordinal)) is { } frag)
            {
                return new MyBatisFragment(local, ns, frag.Text, session.MapperPath);
            }

            foreach (var other in session._workspace.Host.MyBatisHooks.FragmentResolvers)
            {
                if (other.Resolve(refid, currentNamespace) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
