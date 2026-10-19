using AvaloniaEdit.Document;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Storage;
using Oranda.Language.Snippets;
using Oranda.Platform.Storage;

namespace Oranda.App.Features.Templates;

/// <summary>사용자 조각 파일 <c>{data}/snippets.json</c> 읽기·쓰기.</summary>
public sealed class SnippetStore(IAppDataPaths paths)
{
    public string FilePath => Path.Combine(paths.DataDirectory, "snippets.json");

    /// <summary>파일이 없으면 빈 목록. 읽을 수 없으면 <see cref="InvalidDataException"/>.</summary>
    public async Task<IReadOnlyList<Snippet>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        return UserSnippetFile.Parse(await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false));
    }

    public Task SaveAsync(IEnumerable<Snippet> snippets, CancellationToken ct = default) =>
        JsonDocumentFile.WriteBytesAsync(FilePath, System.Text.Encoding.UTF8.GetBytes(UserSnippetFile.Serialize(snippets)), ct);
}

/// <summary>
/// 삽입한 조각의 자리(탭 이동). 자리는 문서 앵커로 따라가므로 자리를 채우는 동안 글자가 바뀌어도 어긋나지 않는다.
/// 같은 번호의 자리는 첫 자리에 친 글자를 다음으로 넘어갈 때 나머지 자리에 복사한다.
/// 편집기 View 가 Tab/Shift+Tab 에서 <see cref="MoveNext"/>/<see cref="MovePrevious"/> 를 부른다.
/// </summary>
public sealed class SnippetSession
{
    private readonly SqlEditorViewModel _editor;
    private readonly List<List<(TextAnchor Start, TextAnchor End)>> _groups = [];
    private int _current = -1;

    public SnippetSession(SqlEditorViewModel editor, SnippetExpansion expansion, int baseOffset)
    {
        _editor = editor;
        foreach (var group in expansion.Groups)
        {
            var list = new List<(TextAnchor, TextAnchor)>();
            foreach (var stop in group)
            {
                var s = editor.Document.CreateAnchor(baseOffset + stop.Start);
                s.MovementType = AnchorMovementType.BeforeInsertion;
                var e = editor.Document.CreateAnchor(baseOffset + stop.Start + stop.Length);
                e.MovementType = AnchorMovementType.AfterInsertion;
                list.Add((s, e));
            }

            _groups.Add(list);
        }
    }

    /// <summary>자리가 아직 남아 있는지(마지막 자리를 지나면 끝난다).</summary>
    public bool IsActive { get; private set; } = true;

    public int StopCount => _groups.Count;

    /// <summary>지금 자리 번호(0부터, 시작 전 -1).</summary>
    public int CurrentIndex => _current;

    /// <summary>다음 자리로. 마지막 자리에서 부르면 세션이 끝난다(false).</summary>
    public bool MoveNext()
    {
        if (!IsActive)
        {
            return false;
        }

        SyncMirrors();
        if (_current + 1 >= _groups.Count)
        {
            IsActive = false;
            return false;
        }

        _current++;
        SelectCurrent();
        if (_current == _groups.Count - 1)
        {
            // 마지막($0)에 닿으면 더 옮길 곳이 없다.
            IsActive = false;
        }

        return true;
    }

    public bool MovePrevious()
    {
        if (!IsActive || _current <= 0)
        {
            return false;
        }

        SyncMirrors();
        _current--;
        SelectCurrent();
        return true;
    }

    /// <summary>탭 이동을 끝낸다.</summary>
    public void Cancel() => IsActive = false;

    private void SelectCurrent()
    {
        var (s, e) = _groups[_current][0];
        _editor.Select(s.Offset, Math.Max(0, e.Offset - s.Offset));
    }

    private void SyncMirrors()
    {
        if (_current < 0 || _groups[_current].Count < 2)
        {
            return;
        }

        var (s, e) = _groups[_current][0];
        var text = _editor.Document.GetText(s.Offset, Math.Max(0, e.Offset - s.Offset));
        // 뒤에서부터 바꿔야 앞 자리의 위치가 흔들리지 않는다.
        foreach (var (ms, me) in _groups[_current].Skip(1).OrderByDescending(x => x.Start.Offset))
        {
            _editor.Document.Replace(ms.Offset, Math.Max(0, me.Offset - ms.Offset), text);
        }
    }
}

/// <summary>조각을 SQL 편집기에 넣는다(편집기 ViewModel API 만 쓴다).</summary>
public static class SnippetInserter
{
    private static readonly Dictionary<Guid, SnippetSession> Sessions = [];

    /// <summary>이 편집기에서 진행 중인 탭 이동. 없으면 null.</summary>
    public static SnippetSession? ActiveSession(SqlEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return Sessions.TryGetValue(editor.TabId, out var s) && s.IsActive ? s : null;
    }

    /// <summary>
    /// 선택 영역(없으면 캐럿 앞의 줄임말 낱말, 그것도 없으면 캐럿 자리)을 조각으로 바꾼다. 읽기 전용이면 false.
    /// 조각이 아닌 종류(힌트·함수·의사 컬럼)는 사용 예 글자를 그대로 넣는다.
    /// </summary>
    public static bool Insert(SqlEditorViewModel editor, Snippet snippet)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(snippet);
        if (editor.IsReadOnly)
        {
            return false;
        }

        var text = editor.Text;
        var start = editor.SelectionStart;
        var length = editor.SelectionLength;
        if (length == 0 && snippet.Kind == SnippetKind.Snippet)
        {
            var w = WordBefore(text, editor.CaretOffset);
            if (w.Length > 0 && string.Equals(text.Substring(editor.CaretOffset - w.Length, w.Length), snippet.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                start = editor.CaretOffset - w.Length;
                length = w.Length;
            }
        }

        var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        if (start == 0)
        {
            lineStart = 0;
        }

        var indent = new string(text.Skip(lineStart).TakeWhile(c => c is ' ' or '\t').ToArray());
        if (indent.Length > start - lineStart)
        {
            indent = indent[..(start - lineStart)];
        }

        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var expansion = SnippetExpander.Expand(snippet.Body, indent, newLine);
        var first = expansion.Groups.Count > 0 ? expansion.Groups[0][0] : new SnippetStop(0, expansion.Text.Length, 0);
        editor.Apply(new SqlEditor.TextOpResult(start, length, expansion.Text, start + first.Start, first.Length));
        Sessions.Remove(editor.TabId);
        if (expansion.Groups.Count > 1)
        {
            var session = new SnippetSession(editor, expansion, start);
            // 첫 자리는 이미 선택했으므로 시작 위치를 맞춘다.
            session.MoveNext();
            Sessions[editor.TabId] = session;
        }

        return true;
    }

    /// <summary>캐럿 앞의 줄임말 후보 낱말(영문·숫자·밑줄).</summary>
    public static string WordBefore(string text, int caret)
    {
        var i = Math.Clamp(caret, 0, text.Length);
        var end = i;
        while (i > 0 && (char.IsAsciiLetterOrDigit(text[i - 1]) || text[i - 1] == '_'))
        {
            i--;
        }

        return text[i..end];
    }

    /// <summary>캐럿 앞 낱말이 조각 줄임말이면 그 조각으로 펼친다(Tab 확장). 아니면 false.</summary>
    public static bool TryExpandPrefix(SqlEditorViewModel editor, IEnumerable<Snippet>? user = null)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var word = WordBefore(editor.Text, editor.CaretOffset);
        if (word.Length == 0 || editor.SelectionLength > 0)
        {
            return false;
        }

        var snippet = SnippetLibrary.FindByPrefix(word, user);
        return snippet is not null && Insert(editor, snippet);
    }
}
