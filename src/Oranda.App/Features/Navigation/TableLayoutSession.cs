using Oranda.App.Features.SqlEditor;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// 테이블 레이아웃 창 하나의 상태: 표·옵션과 「편집기에 넣기」. 창(View)은 이 클래스만 부른다.
/// 넣은 글은 편집기 실행 취소 한 단계이고 넣은 뒤 커서는 글 끝이다.
/// </summary>
public sealed class TableLayoutSession
{
    private readonly Action<TableLayoutSettings>? _save;

    public TableLayoutSession(SqlEditorViewModel tab, TableLayoutModel model, TableLayoutOptions options, Action<TableLayoutSettings>? save = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        Tab = tab;
        Model = model;
        Options = options;
        _save = save;
    }

    public SqlEditorViewModel Tab { get; }

    public TableLayoutModel Model { get; }

    public TableLayoutOptions Options { get; private set; }

    /// <summary>옵션을 바꾼다. 확인란 4개가 달라졌으면 설정으로 저장한다(별칭만 바뀌면 저장하지 않는다).</summary>
    public void SetOptions(TableLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var changed = options.ToSettings() != Options.ToSettings();
        Options = options;
        if (changed)
        {
            _save?.Invoke(options.ToSettings());
        }
    }

    /// <summary>
    /// 넣을 글(⌘C 로 복사하는 글과 같다). 행은 표 순서로 놓이고, 여러 줄이면 둘째 줄부터 편집기 커서 열에 맞춰 들인다.
    /// </summary>
    public string TextFor(IReadOnlyList<TableLayoutRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var offset = InsertOffset();
        var lineStart = Tab.Document.GetLineByOffset(offset).Offset; // 줄 시작은 문서 줄 모델(\r·\n·\r\n 모두)에서 받는다.
        return TableLayoutPaste.Build(rows, Options, TableLayoutPaste.IndentFor(Tab.Document.GetText(lineStart, offset - lineStart), offset - lineStart), NewLine(offset));
    }

    // 문서가 쓰는 줄바꿈(커서 줄의 끝 → 없으면 바로 앞 줄 → 기본 \n). Windows 에서 연 CRLF 파일에 LF 가 섞이지 않게 한다.
    private string NewLine(int offset)
    {
        var line = Tab.Document.GetLineByOffset(offset);
        var delimited = line.DelimiterLength > 0 ? line : line.PreviousLine;
        return delimited is null ? "\n" : Tab.Document.GetText(delimited.EndOffset, delimited.DelimiterLength);
    }

    /// <summary>행들을 편집기 커서 자리(선택이 있으면 선택을 대체)에 넣는다. 넣었으면 true.</summary>
    public bool Paste(IReadOnlyList<TableLayoutRow> rows)
    {
        var text = TextFor(rows);
        if (text.Length == 0)
        {
            return false;
        }

        var length = Tab.Text.Length;
        var start = InsertOffset();
        var replace = Math.Clamp(Tab.SelectionLength, 0, length - start);
        var end = start + text.Length;
        var applied = Tab.Apply(new TextOpResult(start, replace, text, end, 0));
        if (applied)
        {
            Tab.Surface?.FocusEditor();
        }

        return applied;
    }

    private int InsertOffset() => Math.Clamp(Tab.SelectionLength > 0 ? Tab.SelectionStart : Tab.CaretOffset, 0, Tab.Text.Length);
}
