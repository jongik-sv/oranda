using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Binds;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Shell;
using Oranda.Core.Text;
using Oranda.Core.Workspaces;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.SqlEditor;

/// <summary>접기 동작(commands.md <c>oranda.edit.fold</c> 등).</summary>
public enum FoldAction
{
    Fold,
    Unfold,
    FoldAll,
    UnfoldAll,
}

/// <summary>
/// 편집기 View 가 구현해 ViewModel 에 꽂는 면. View 가 없는 시험에서는 비어 있어도 ViewModel 이 동작한다.
/// 글자 바꾸기·찾기·북마크 같은 논리는 ViewModel 이 하고, 클립보드·접기·화면 이동처럼 컨트롤이 필요한 것만 여기로 온다.
/// </summary>
public interface IEditorSurface
{
    int CursorCount { get; }

    /// <summary>맨 위에 보이는 줄(1부터).</summary>
    int FirstVisibleLine { get; }

    void Cut();

    void Copy();

    void Paste();

    void SelectAll();

    void SelectRange(int start, int length);

    void ScrollToLine(int line);

    void Fold(FoldAction action);

    /// <summary>위(-1)·아래(+1) 줄에 커서를 더한다.</summary>
    void AddCursor(int direction);

    void ShowFind(bool replace);

    void FocusEditor();
}

/// <summary>
/// <c>oranda.panel.sqlEditor</c> — 탭 하나의 SQL 문서. 글자(<see cref="Document"/>)·파일 연결·커서·찾기·북마크를 가진다.
/// 실행 쪽 상태(접속·결과)는 다른 Task 가 붙인다. <see cref="ConnectionProfileId"/> 만 작업 공간 저장을 위해 둔다.
/// </summary>
[OrandaToolPanel("oranda.panel.sqlEditor", ToolPanelKind.Document, DockLocation.Document, AllowMultiple = true)]
public sealed partial class SqlEditorViewModel : ToolPanelViewModelBase, IBindOwner
{
    private readonly List<TextAnchor> _bookmarks = [];
    private int _suppressDirty;
    private long _bindNamesVersion = -1;
    private IReadOnlyList<string> _bindNames = [];

    public SqlEditorViewModel()
        : this(Guid.NewGuid(), string.Empty)
    {
    }

    public SqlEditorViewModel(Guid tabId, string text)
    {
        TabId = tabId;
        _startText = text ?? string.Empty;
        Document = new TextDocument(text ?? string.Empty);
        Document.UndoStack.ClearAll();
        Document.TextChanged += (_, _) =>
        {
            Version++;
            if (_suppressDirty == 0)
            {
                IsDirty = true;
            }

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            TextChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public Guid TabId { get; }

    // 탭을 처음 연(또는 마지막으로 LoadText 한) 글. 이름 없는 탭이 손대지 않은 채인지 가를 때 쓴다.
    private string _startText;

    /// <summary>
    /// 사용자가 이 글을 직접 바꿨는가: 고친 적이 있고, 글이 공백만이 아니며, 처음 글과 다를 때. 이름 없는 탭을 닫을 때 저장을 물을지 정한다
    /// (템플릿·만들어 넣은 글을 손대지 않았거나, 쳤다가 처음 글로 되돌렸으면 묻지 않는다).
    /// </summary>
    public bool HasUserEdits => IsDirty && !string.IsNullOrWhiteSpace(Text) && !string.Equals(Text, _startText, StringComparison.Ordinal);

    /// <summary>
    /// 탭을 닫을 때 저장을 물어야 하는가(SQL 탭·PL/SQL 탭이 함께 쓰는 판정). 읽기 전용 탭은 고칠 수 없으니 묻지 않는다.
    /// 파일에 묶인 탭은 고쳤을 때, 이름 없는 탭은 사용자가 글을 쳤을 때(<see cref="HasUserEdits"/>) 묻는다.
    /// 복원 때 파일을 못 읽어 비어 있는 탭도 묻지 않는다: 저장하면 빈 내용이 원본을 덮어쓴다.
    /// <paramref name="serverBound"/> 가 참이면 서버 객체를 열었거나 컴파일한 탭이라 이름 없는 탭으로 보지 않고 고쳤을 때 묻는다.
    /// </summary>
    public bool NeedsSaveConfirm(bool serverBound = false)
    {
        if (IsReadOnly)
        {
            return false;
        }

        if (FilePath is null && !serverBound)
        {
            return HasUserEdits;
        }

        return IsDirty && !(FileUnreadable && string.IsNullOrWhiteSpace(Text));
    }

    /// <summary>이 탭의 바인드 변수 목록(TSK-04-11). 바인드 변수 창은 활성 탭의 것을 보이고, 작업 공간·.sql·.sqd 도 탭마다 이 목록을 넣고 뺀다.</summary>
    public BindVariableStore Binds { get; } = new();

    public TextDocument Document { get; }

    /// <summary>글자가 바뀔 때마다 오르는 번호(자동 저장이 변경 여부를 본다).</summary>
    public long Version { get; private set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "SQL";

    [ObservableProperty]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    public partial string Encoding { get; set; } = "utf-8";

    [ObservableProperty]
    public partial Guid? ConnectionProfileId { get; set; }

    [ObservableProperty]
    public partial bool IsReadOnly { get; set; }

    [ObservableProperty]
    public partial int CaretOffset { get; set; }

    [ObservableProperty]
    public partial int SelectionStart { get; set; }

    [ObservableProperty]
    public partial int SelectionLength { get; set; }

    [ObservableProperty]
    public partial int ScrollLine { get; set; } = 1;

    /// <summary>결과 영역(TSK-06-03 의 <c>SqlResultPaneViewModel</c> 등)을 편집기 아래에 붙이는 자리.</summary>
    [ObservableProperty]
    public partial object? ResultPane { get; set; }

    public IEditorSurface? Surface { get; set; }

    public event EventHandler? TextChanged;

    public string Text => Document.Text;

    public bool HasSelection => SelectionLength > 0;

    public bool CanUndo => Document.UndoStack.CanUndo;

    public bool CanRedo => Document.UndoStack.CanRedo;

    public int CursorCount => Surface?.CursorCount ?? 1;

    public string DisplayTitle => (IsDirty ? "* " : string.Empty) + Title;

    /// <summary>탭 머리에서 이름을 제자리 편집하는 중(두 번 누르기·F2·오른쪽 클릭 「이름 바꾸기」).</summary>
    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    /// <summary>제자리 편집 중인 이름(확정 전 글자).</summary>
    [ObservableProperty]
    public partial string EditTitle { get; set; } = string.Empty;

    /// <summary>제자리 이름 편집을 시작한다. 편집 글자는 지금 이름에서 출발한다.</summary>
    public void BeginRename()
    {
        EditTitle = Title;
        IsRenaming = true;
    }

    /// <summary>편집한 이름을 확정한다. 빈 이름은 거절하고(편집 상태 유지) false.</summary>
    public bool CommitRename()
    {
        var name = EditTitle?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        Title = name;
        IsRenaming = false;
        return true;
    }

    /// <summary>제자리 이름 편집을 취소하고 원래 이름을 그대로 둔다.</summary>
    public void CancelRename() => IsRenaming = false;

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(DisplayTitle));

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));

    partial void OnSelectionLengthChanged(int value) => OnPropertyChanged(nameof(HasSelection));

    partial void OnCaretOffsetChanged(int value)
    {
        OnPropertyChanged(nameof(CaretPosition));
        OnPropertyChanged(nameof(PositionText));
    }

    // ---- 글자 읽기·바꾸기 -----------------------------------------------------------------

    /// <summary>파일에서 읽은 내용 등으로 글자를 통째로 바꾼다. 실행 취소 기록과 변경 표시를 지운다.</summary>
    public void LoadText(string text, bool dirty = false)
    {
        _suppressDirty++;
        try
        {
            Document.Text = text;
        }
        finally
        {
            _suppressDirty--;
        }

        Document.UndoStack.ClearAll();
        IsDirty = dirty;
        _startText = dirty ? string.Empty : text;
        _bookmarks.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public void MarkSaved() => IsDirty = false;

    /// <summary>작업 공간에서 복원할 때 파일을 못 읽어 빈 문서 + 변경됨으로 열린 탭이면 true. 닫을 때 빈 내용으로 원본을 덮어쓰지 않는 근거다.</summary>
    public bool FileUnreadable { get; private set; }

    /// <summary>글자 연산 결과를 문서에 적용(실행 취소 1단계)하고 선택을 옮긴다.</summary>
    public bool Apply(TextOpResult? result)
    {
        if (result is null || IsReadOnly)
        {
            return false;
        }

        Document.Replace(result.Start, result.Length, result.NewText);
        Select(result.SelectionStart, result.SelectionLength);
        return true;
    }

    /// <summary>현재 선택에 글자 연산을 적용한다.</summary>
    public bool Apply(Func<string, int, int, TextOpResult?> op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return Apply(op(Text, SelectionStart, SelectionLength));
    }

    public void Select(int start, int length)
    {
        start = Math.Clamp(start, 0, Document.TextLength);
        length = Math.Clamp(length, 0, Document.TextLength - start);
        SelectionStart = start;
        SelectionLength = length;
        CaretOffset = start + length;
        Surface?.SelectRange(start, length);
    }

    public void UpdateCaret(int caretOffset, int selectionStart, int selectionLength)
    {
        CaretOffset = caretOffset;
        SelectionStart = selectionStart;
        SelectionLength = selectionLength;
    }

    // ---- 줄·열 ------------------------------------------------------------------------

    public TextPosition PositionOf(int offset)
    {
        var loc = Document.GetLocation(Math.Clamp(offset, 0, Document.TextLength));
        return new TextPosition(loc.Line, loc.Column);
    }

    public int OffsetOf(TextPosition position)
    {
        int line = Math.Clamp(position.Line, 1, Document.LineCount);
        var dl = Document.GetLineByNumber(line);
        return dl.Offset + Math.Clamp(position.Column - 1, 0, dl.Length);
    }

    public TextPosition CaretPosition => PositionOf(CaretOffset);

    public string PositionText
    {
        get
        {
            var p = CaretPosition;
            return $"줄 {p.Line}, 열 {p.Column}";
        }
    }

    public bool GoToLine(int line)
    {
        if (line < 1 || line > Document.LineCount)
        {
            return false;
        }

        var dl = Document.GetLineByNumber(line);
        Select(dl.Offset, 0);
        Surface?.ScrollToLine(line);
        return true;
    }

    // ---- 문장·짝 ------------------------------------------------------------------------

    public SqlStatement? GetStatementAtCaret() => StatementSplitter.GetStatementAt(Text, CaretOffset, SplitOptions.Default);

    /// <summary>
    /// 글 전체가 참조하는 바인드 이름(처음 나온 순서, 대소문자만 다른 이름은 하나). 작업 공간·.sql 저장과 자동 저장 표지가 쓴다.
    /// 판(<see cref="Version"/>)이 그대로면 다시 나누지 않는다(자동 저장이 틱마다 부른다).
    /// </summary>
    public IReadOnlyList<string> ReferencedBindNames
    {
        get
        {
            if (_bindNamesVersion != Version)
            {
                _bindNames = [.. StatementSplitter.Split(Text, SplitOptions.Default)
                    .SelectMany(s => s.BindNames)
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                _bindNamesVersion = Version;
            }

            return _bindNames;
        }
    }

    /// <summary>실행한 범위를 잠깐 칠해 달라는 요청. View 가 옅은 배경을 칠했다가 서서히 지운다(View 가 없으면 아무 일도 없다).</summary>
    public event EventHandler<TextRange>? FlashRequested;

    /// <summary>
    /// 실행 범위 잠깐 강조(DataGrip·SQL Developer 의 실행 문장 표시). 실행 컨트롤러가 실행 직전에 부른다.
    /// 범위는 문서 안으로 자르고, 빈 범위는 무시한다.
    /// </summary>
    public void FlashRange(int start, int length)
    {
        start = Math.Clamp(start, 0, Document.TextLength);
        length = Math.Clamp(length, 0, Document.TextLength - start);
        if (length > 0)
        {
            FlashRequested?.Invoke(this, new TextRange(start, length));
        }
    }

    /// <summary>커서 옆 괄호·BEGIN/END 의 짝으로 커서를 옮긴다(<c>oranda.edit.goToMatchingBracket</c>).</summary>
    public bool GoToMatchingBracket()
    {
        var match = SqlStructure.FindMatch(SqlStructure.FindPairs(Text), CaretOffset);
        if (match is not { } m)
        {
            return false;
        }

        // 닫는 쪽에서 열림으로, 열림에서 닫는 쪽 끝으로 간다.
        int target = m.Other.Start < m.Here.Start ? m.Other.Start : m.Other.End;
        Select(target, 0);
        return true;
    }

    // ---- 북마크 -------------------------------------------------------------------------

    public bool HasBookmark => _bookmarks.Exists(a => !a.IsDeleted);

    /// <summary>북마크가 걸린 줄 번호(오름차순, 1부터).</summary>
    public IReadOnlyList<int> BookmarkLines
        => [.. _bookmarks.Where(a => !a.IsDeleted).Select(a => a.Line).Distinct().Order()];

    public bool ToggleBookmark()
    {
        int line = CaretPosition.Line;
        var existing = _bookmarks.Where(a => !a.IsDeleted && a.Line == line).ToList();
        if (existing.Count > 0)
        {
            _bookmarks.RemoveAll(a => existing.Contains(a));
            OnPropertyChanged(nameof(HasBookmark));
            BookmarksChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        var anchor = Document.CreateAnchor(Document.GetLineByNumber(line).Offset);
        anchor.MovementType = AnchorMovementType.BeforeInsertion;
        _bookmarks.Add(anchor);
        OnPropertyChanged(nameof(HasBookmark));
        BookmarksChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public event EventHandler? BookmarksChanged;

    /// <summary>다음(+1)·이전(-1) 북마크 줄로 간다. 끝에서 반대쪽 끝으로 돈다.</summary>
    public bool GoToBookmark(int direction)
    {
        var lines = BookmarkLines;
        if (lines.Count == 0)
        {
            return false;
        }

        int cur = CaretPosition.Line;
        int target = direction >= 0
            ? lines.FirstOrDefault(l => l > cur, lines[0])
            : lines.LastOrDefault(l => l < cur, lines[^1]);
        return GoToLine(target);
    }

    // ---- 찾기·바꾸기 ---------------------------------------------------------------------

    [ObservableProperty]
    public partial string FindText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReplaceText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial bool WholeWord { get; set; }

    [ObservableProperty]
    public partial bool IsFindPanelOpen { get; set; }

    [ObservableProperty]
    public partial bool IsReplaceMode { get; set; }

    [ObservableProperty]
    public partial string FindStatus { get; set; } = string.Empty;

    public bool HasFindText => FindText.Length > 0;

    partial void OnFindTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasFindText));
        UpdateFindStatus();
    }

    partial void OnUseRegexChanged(bool value) => UpdateFindStatus();

    partial void OnMatchCaseChanged(bool value) => UpdateFindStatus();

    partial void OnWholeWordChanged(bool value) => UpdateFindStatus();

    public void OpenFind(bool replace)
    {
        IsFindPanelOpen = true;
        IsReplaceMode = replace && !IsReadOnly;
        if (SelectionLength > 0 && SelectionLength < 200)
        {
            var sel = Document.GetText(SelectionStart, SelectionLength);
            if (!sel.Contains('\n', StringComparison.Ordinal))
            {
                FindText = sel;
            }
        }

        Surface?.ShowFind(IsReplaceMode);
    }

    public void CloseFind()
    {
        IsFindPanelOpen = false;
        Surface?.FocusEditor();
    }

    private TextFinder CreateFinder() => new(new FindOptions
    {
        Pattern = FindText,
        UseRegex = UseRegex,
        MatchCase = MatchCase,
        WholeWord = WholeWord,
    });

    private void UpdateFindStatus()
    {
        if (FindText.Length == 0)
        {
            FindStatus = string.Empty;
            return;
        }

        var f = CreateFinder();
        FindStatus = f.Error is not null ? "정규식 오류" : $"{f.Count(Text)}건";
    }

    /// <summary>선택 끝(또는 커서) 뒤의 다음 일치를 고르고 true.</summary>
    public bool FindNext()
    {
        var f = CreateFinder();
        var m = f.FindNext(Text, SelectionStart + SelectionLength);
        if (m is not { } hit)
        {
            FindStatus = f.Error is not null ? "정규식 오류" : "없음";
            return false;
        }

        Select(hit.Start, hit.Length);
        Surface?.ScrollToLine(PositionOf(hit.Start).Line);
        UpdateFindStatus();
        return true;
    }

    public bool FindPrevious()
    {
        var f = CreateFinder();
        var m = f.FindPrevious(Text, SelectionStart);
        if (m is not { } hit)
        {
            FindStatus = f.Error is not null ? "정규식 오류" : "없음";
            return false;
        }

        Select(hit.Start, hit.Length);
        Surface?.ScrollToLine(PositionOf(hit.Start).Line);
        return true;
    }

    /// <summary>선택이 일치면 바꾸고 다음으로, 아니면 다음 일치만 고른다.</summary>
    public bool ReplaceCurrent()
    {
        if (IsReadOnly)
        {
            return false;
        }

        var f = CreateFinder();
        var rep = SelectionLength > 0 ? f.ReplacementFor(Text, SelectionStart, SelectionLength, ReplaceText) : null;
        if (rep is not null)
        {
            Document.Replace(SelectionStart, SelectionLength, rep);
            Select(SelectionStart + rep.Length, 0);
        }

        return FindNext();
    }

    /// <summary>모두 바꾼다(실행 취소 1단계). 바꾼 횟수를 돌려준다.</summary>
    public int ReplaceAll()
    {
        if (IsReadOnly)
        {
            return 0;
        }

        var f = CreateFinder();
        var (text, count) = f.ReplaceAll(Text, ReplaceText);
        if (count > 0)
        {
            Document.Replace(0, Document.TextLength, text);
        }

        FindStatus = $"{count}건 바꿈";
        return count;
    }

    // ---- 작업 공간 ----------------------------------------------------------------------

    /// <summary>
    /// 작업 공간 탭으로 만든다. 글자는 저장 안 된 문서(파일 없음 또는 변경됨)만 담는다(data-model 2.6).
    /// 이 탭의 바인드 목록(<see cref="Binds"/>, 다른 목록을 주면 그것)에서 탭이 참조하는 바인드의 값도 담는다.
    /// </summary>
    public WorkspaceTab ToWorkspaceTab(BindVariableStore? binds = null)
    {
        bool keepContent = FilePath is null || IsDirty;
        var values = (binds ?? Binds).Snapshot(ReferencedBindNames);
        return new WorkspaceTab
        {
            Id = TabId,
            Title = Title,
            FilePath = FilePath,
            Content = keepContent ? Text : null,
            IsDirty = IsDirty,
            Encoding = Encoding,
            Caret = CaretPosition,
            SelectionStart = SelectionLength > 0 ? PositionOf(SelectionStart) : null,
            ScrollLine = Surface?.FirstVisibleLine ?? ScrollLine,
            ConnectionProfileId = ConnectionProfileId,
            Binds = values is { Count: > 0 } ? values : null,
        };
    }

    /// <summary>
    /// 작업 공간 탭을 복원한다(커서만, 선택 영역은 버린다). <paramref name="fileText"/> 는 파일에서 다시 읽은 내용(없으면 null). .sql 끝의 바인드 블록은
    /// 편집기에 보이지 않게 뗀다(값은 호스트가 바인드 변수 목록에 넣는다).
    /// </summary>
    public static SqlEditorViewModel FromWorkspaceTab(WorkspaceTab tab, string? fileText)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var vm = new SqlEditorViewModel(tab.Id == Guid.Empty ? Guid.NewGuid() : tab.Id, string.Empty)
        {
            Title = tab.Title,
            FilePath = tab.FilePath,
            Encoding = tab.Encoding,
            ConnectionProfileId = tab.ConnectionProfileId,
            ScrollLine = tab.ScrollLine,
        };
        // 바인드 값은 이 탭의 목록에 넣는다: 파일 끝 블록 먼저, 작업 공간에 남긴 값이 그 위에(작업 공간·자동 저장 쪽이 더 나중 값이다).
        if (fileText is not null && SqlEditorHost.CarriesBinds(tab.FilePath))
        {
            var (body, fileBinds) = SqlFileBinds.Split(fileText);
            fileText = body;
            if (tab.Content is null)
            {
                vm.Binds.Restore(fileBinds);
            }
        }

        vm.Binds.Restore(tab.Binds);

        var text = tab.Content ?? fileText ?? string.Empty;
        // 파일이 있어야 하는데 못 읽었고 저장된 내용도 없으면 빈 문서 + 변경됨으로 두어 닫을 때 알린다.
        bool dirty = tab.IsDirty || (tab.Content is null && fileText is null && tab.FilePath is not null);
        vm.LoadText(text, dirty);
        vm.FileUnreadable = tab.Content is null && fileText is null && tab.FilePath is not null;
        // 선택 영역은 복원하지 않고 커서 위치만 되살린다. 전체 선택이 복원된 채 키 입력이 들어가
        // 복원한 내용을 덮어쓴 사고가 있었다. 저장 형식(SelectionStart)은 읽기 호환을 위해 그대로 둔다.
        vm.Select(vm.OffsetOf(tab.Caret), 0);

        return vm;
    }
}
