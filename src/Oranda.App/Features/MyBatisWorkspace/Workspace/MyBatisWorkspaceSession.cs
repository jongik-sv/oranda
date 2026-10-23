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
public sealed partial class MyBatisWorkspaceSession : IMyBatisWorkspaceSession
{
    private readonly SqlWorkspaceViewModel _workspace;
    private readonly Func<string, IMyBatisMapperDocument> _parse;
    private readonly List<MyBatisStatementEntry> _entries = [];
    private readonly SessionFragments _fragments;
    private IMyBatisMapperDocument _document;
    private MyBatisStatementEntry? _selected;
    private bool _noneSelected;
    private bool _syncingTabs;
    private bool _batching;

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

        // 탭 공간 원문(파일 들여쓰기를 걷어 낸 글)으로 돌려준다: 계산 결과 범위가 탭 글 오프셋과 맞아야 한다.
        var text = entry.Text;
        if (entry.BaselineView is { } baseline && string.Equals(baseline.Text, text, StringComparison.Ordinal))
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
        }) with
        { Id = entry.Id, Text = text };
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

            // 편집 글은 파일 공간이다: 탭 글에 파일 들여쓰기를 다시 붙인다(Reindent).
            if (e.IsNew)
            {
                edits.Add(new MapperEdit.InsertElement(e.Id!, e.Indent is null ? MyBatisStatementText.Reindent(e.Text, InsertIndent(i)) : e.FileText, InsertAnchor(i)));
            }
            else if (e.IsDeleted)
            {
                edits.Add(new MapperEdit.DeleteElement(e.OriginalId!));
            }
            else if (!string.Equals(e.Id, e.OriginalId, StringComparison.Ordinal) && IsRenameOnly(e))
            {
                edits.Add(new MapperEdit.RenameElement(e.OriginalId!, e.Id!));
            }
            else
            {
                edits.Add(new MapperEdit.ReplaceElement(e.OriginalId!, e.FileText));
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

        // 탭 글은 들여쓰기 없는 틀이고, 저장 때 넣을 자리(Apply 의 AfterId 요소)의 들여쓰기를 둘째 줄부터 붙인다(접두 null).
        var at = _entries.FindLastIndex(e => e.Kind == kind);
        var text = MyBatisStatementText.NewStatement(kind, id, _document.Format.NewLine);
        var entry = new MyBatisStatementEntry(null, kind, id, text, ambiguous: false, indent: null);
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

    /// <summary>요소 하나의 원문을 바꾼다(<see cref="SetEntryTexts"/> 와 같다).</summary>
    public void SetEntryText(MyBatisStatementEntry entry, string text) => SetEntryTexts([(entry, text)]);

    /// <summary>
    /// 여러 요소의 원문(탭 공간 글)을 한 번에 바꾼다(매퍼 전체 포맷·그 되돌리기). <see cref="RenameEntry"/> 처럼 탭이 열려 있으면 탭 문서를 바꾸고
    /// (바뀐 가운데만 한 번 교체 — 그 탭의 실행 취소 1단계), 없으면 <see cref="MyBatisStatementEntry.StoredText"/> 를 바꾼다. 상태 알림은 끝에 한 번이다.
    /// 편집할 수 없거나 지운·이 세션 밖 요소가 하나라도 있으면 아무것도 바꾸지 않고 <see cref="ArgumentException"/>.
    /// </summary>
    public void SetEntryTexts(IReadOnlyList<(MyBatisStatementEntry Entry, string Text)> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var (entry, text) in changes)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(text);
            if (!entry.IsEditable || entry.IsDeleted || !_entries.Contains(entry))
            {
                throw new ArgumentException($"바꿀 수 없는 문장입니다: {entry.Id}", nameof(changes));
            }
        }

        _batching = true;
        try
        {
            foreach (var (entry, text) in changes)
            {
                if (entry.Tab is { } tab)
                {
                    ReplaceChanged(tab, text); // 글이 바뀌면 OnTabTextChanged 가 id·dirty 를 맞춘다(알림은 아래에서 한 번)
                }
                else
                {
                    entry.StoredText = text;
                    if (MyBatisStatementText.TryFindIdValue(text, out var start, out var length)
                        && text.Substring(start, length) is var id && !string.Equals(id, entry.Id, StringComparison.Ordinal) && ValidateId(id, entry) is null)
                    {
                        entry.Id = id;
                    }
                }
            }
        }
        finally
        {
            // 중간에 탭 구독자가 던져도 이미 바뀐 요소가 목록·* 에 보이게 알린다.
            _batching = false;
            RaiseStateChanged();
        }
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
}
