using Oranda.App.Features.SqlEditor;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// 테이블 레이아웃 창 하나의 상태: 표·옵션과 「편집기에 넣기」. 창(View)은 이 클래스만 부른다.
/// 넣은 글은 편집기 실행 취소 한 단계이고 넣은 뒤 커서는 글 끝이다.
/// 넣기 대상은 이 탭을 연 편집기 탭(<see cref="Tab"/>)이다. 그 편집기 탭이 닫히면(<see cref="MarkTargetClosed"/>) 넣기만 막히고 보기·복사는 계속 된다.
/// </summary>
public sealed class TableLayoutSession
{
    private readonly Action<TableLayoutSettings>? _save;

    public TableLayoutSession(SqlEditorViewModel tab, TableLayoutModel model, TableLayoutOptions options, Action<TableLayoutSettings>? save = null, Guid? connectionId = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        Tab = tab;
        Model = model;
        Options = options;
        _save = save;
        ConnectionId = connectionId ?? tab.ConnectionProfileId;
    }

    /// <summary>넣기를 막은 이유를 상태 줄과 단추 설명에 보이는 글.</summary>
    public const string TargetClosedNotice = "이 탭을 연 편집기 탭이 닫혀 넣을 수 없습니다. 복사는 할 수 있습니다.";

    /// <summary>넣을 편집기 탭(처음엔 이 탭을 연 편집기 탭, <see cref="Retarget"/> 으로 바뀐다).</summary>
    public SqlEditorViewModel Tab { get; private set; }

    /// <summary>이 표를 읽은 접속(편집기 탭이 조회 중이나 나중에 접속을 바꿔도 같은 표 판정은 이 값을 쓴다). 생성자에 주지 않으면 편집기 탭의 지금 접속.</summary>
    public Guid? ConnectionId { get; private set; }

    /// <summary>같은 표 판정 키: 접속 · 소유자.표. 접속이 다르면 같은 이름도 다른 표다.</summary>
    public (Guid? Connection, string Title) Key => (ConnectionId, Model.Title);

    /// <summary>넣기 대상 편집기 탭이 닫혔는가. 닫혔으면 <see cref="Paste"/> 는 아무것도 하지 않는다.</summary>
    public bool IsTargetClosed { get; private set; }

    /// <summary>표 내용(컬럼 목록)이 새로 읽은 값으로 바뀌었다.</summary>
    public event EventHandler? ModelChanged;

    /// <summary>
    /// 같은 표를 다시 열었다: 새로 읽은 표(<paramref name="model"/>)와 지금 설정(<paramref name="fresh"/>)의 쉼표 위치·감사 컬럼 목록을 받는다.
    /// 확인란 4개·「감사 컬럼 빼기」·별칭은 이 탭의 값을 그대로 쓴다.
    /// </summary>
    public void Refresh(TableLayoutModel model, TableLayoutOptions fresh)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(fresh);
        Model = model;
        Options = Options with { Comma = fresh.Comma, AuditColumns = fresh.AuditColumns };
        ModelChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>넣기 대상이 바뀌었거나 닫혔다.</summary>
    public event EventHandler? TargetChanged;

    /// <summary>넣기 대상 편집기 탭이 닫혔음을 알린다.</summary>
    public void MarkTargetClosed()
    {
        if (!IsTargetClosed)
        {
            IsTargetClosed = true;
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>같은 표를 다른 편집기 탭에서 다시 열었다: 넣기 대상을 그 탭으로 바꾸고 닫힘 표시를 푼다. 옵션 등 탭의 상태는 그대로다.</summary>
    public void Retarget(SqlEditorViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (ReferenceEquals(tab, Tab) && !IsTargetClosed)
        {
            return;
        }

        Tab = tab;
        IsTargetClosed = false;
        TargetChanged?.Invoke(this, EventArgs.Empty);
    }

    public TableLayoutModel Model { get; private set; }

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

    /// <summary>편집기 선택이 열 블록·여러 커서라 넣을 수 없는가.</summary>
    public bool IsBlocked => Tab.HasMultiPieceSelection;

    /// <summary>행들을 편집기 커서 자리(선택이 있으면 선택을 대체)에 넣는다. 넣었으면 true.</summary>
    public bool Paste(IReadOnlyList<TableLayoutRow> rows)
    {
        if (IsTargetClosed)
        {
            return false;
        }

        var text = TextFor(rows);
        if (text.Length == 0 || IsBlocked)
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
