using Oranda.Core.Composition;
using Oranda.Core.Extensibility;
using Oranda.Core.Language;
using Oranda.Core.Execution;
using Oranda.Core.Text;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// <see cref="IEditorContext"/> 구현(APP-09, TRD 4.10). 활성 SQL 편집기 탭을 읽고, 제안 편집은 차이 미리보기를 거쳐
/// 사용자가 받아들일 때만 문서에 넣는다(실행 취소 1단계). 실행은 이 객체가 하지 않는다.
/// UI 스레드에서 호출한다.
/// </summary>
[OrandaService(typeof(IEditorContext))]
public sealed class EditorContext : IEditorContext
{
    private SqlEditorHost? _host;

    // 생성자는 매개변수 없이 둔다. 셸의 생성자 고르기(ServiceContainer.Create)는 등록되지 않은 참조 형식 매개변수를
    // 만나면 지연 어셈블리를 훑어 시작 때 Oranda.Oracle·Oranda.Language 를 로드한다(D-006, 시작 시험이 막는다).
    // 호스트를 바꿔야 하는 시험은 <see cref="Host"/> 를 초기화 구문으로 준다.

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>.</summary>
    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    /// <summary>활성 탭의 선택이 열 블록·여러 커서처럼 조각 둘 이상인가. 선택을 한 덩어리로 읽거나 바꾸는 기능이 실행 전에 거른다.</summary>
    public bool HasMultiPieceSelection => Host.Active?.HasMultiPieceSelection ?? false;

    public EditorSnapshot? GetActiveDocument()
    {
        var tab = Host.Active;
        if (tab is null)
        {
            return null;
        }

        // 열 블록·여러 커서 선택은 한 덩어리 범위로 나타낼 수 없다: 블록을 감싸는 범위를 내보내면 블록 밖 글이 선택된 것처럼 읽히고 바뀐다.
        // 그래서 선택 없음(캐럿 자리)으로 알린다. 한 덩어리 선택을 막아야 하는 쪽은 <see cref="HasMultiPieceSelection"/> 으로 먼저 거른다.
        var selection = tab.HasMultiPieceSelection ? new TextRange(tab.CaretOffset, 0) : new TextRange(tab.SelectionStart, tab.SelectionLength);
        return new EditorSnapshot(tab.TabId, tab.Text, selection, tab.CaretOffset)
        {
            Title = tab.Title,
            ConnectionProfileId = tab.ConnectionProfileId,
        };
    }

    public SqlStatement? GetCurrentStatement() => Host.Active?.GetStatementAtCaret();

    public async Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (Host.Active is not { } tab)
        {
            return false;
        }

        var outcome = await ProposeToTabAsync(tab, edit, baseVersion: null, ct).ConfigureAwait(true);
        return outcome.IsApplied;
    }

    /// <summary>
    /// 상태 반환 판: 정한 탭에 제안을 비교 창으로 보이고, 받아들이면 넣는다(실행 취소 1단계). 결과는 받아들임·손 고침·거절·어긋남·읽기 전용 중 하나다.
    /// <paramref name="baseVersion"/> 이 있으면 창을 띄우기 전에 탭의 판과 같은지 먼저 본다(다르면 창 없이 <see cref="EditProposalStatus.Stale"/>).
    /// 창을 보는 사이 글이 바뀌었거나 탭이 닫혔으면 넣지 않고 <see cref="EditProposalStatus.Stale"/> 이다.
    /// </summary>
    public async Task<EditProposalOutcome> ProposeToTabAsync(SqlEditorViewModel tab, TextEdit edit, long? baseVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(edit);
        if (tab.IsReadOnly)
        {
            return new EditProposalOutcome(EditProposalStatus.ReadOnly, tab.Version);
        }

        if ((baseVersion is { } expected && expected != tab.Version) || !edit.Range.IsWithin(tab.Document.TextLength))
        {
            return new EditProposalOutcome(EditProposalStatus.Stale, tab.Version);
        }

        var versionAtPreview = tab.Version;
        var before = tab.Document.GetText(edit.Range.Start, edit.Range.Length);
        var newText = await Host.Dialogs
            .PreviewEditTextAsync(edit.Description ?? "제안된 편집", before, edit.NewText, ct)
            .ConfigureAwait(true);
        if (newText is null)
        {
            return new EditProposalOutcome(EditProposalStatus.Rejected, tab.Version);
        }

        // 미리보기를 보는 사이 문서가 바뀌었으면 범위가 어긋났을 수 있으니 적용하지 않는다.
        if (tab.Version != versionAtPreview || !Host.Tabs.Contains(tab))
        {
            return new EditProposalOutcome(EditProposalStatus.Stale, tab.Version);
        }

        if (!tab.Apply(new TextOpResult(edit.Range.Start, edit.Range.Length, newText, edit.Range.Start, newText.Length)))
        {
            return new EditProposalOutcome(EditProposalStatus.ReadOnly, tab.Version);
        }

        var status = string.Equals(newText, edit.NewText, StringComparison.Ordinal) ? EditProposalStatus.Accepted : EditProposalStatus.Modified;
        return new EditProposalOutcome(status, tab.Version, newText);
    }
}

/// <summary>제안 편집 한 번의 끝.</summary>
public enum EditProposalStatus
{
    /// <summary>제안 글 그대로 넣었다.</summary>
    Accepted,

    /// <summary>비교 창에서 사람이 고친 글을 넣었다.</summary>
    Modified,

    /// <summary>사람이 거절했다.</summary>
    Rejected,

    /// <summary>탭 글이 제안의 기준 판과 달라 넣지 않았다.</summary>
    Stale,

    /// <summary>읽기 전용 탭이라 넣지 않았다.</summary>
    ReadOnly,
}

/// <summary>제안 편집의 결과: 상태, 그 뒤 탭의 판, 넣은 최종 글(넣었을 때만).</summary>
public sealed record EditProposalOutcome(EditProposalStatus Status, long Version, string? FinalText = null)
{
    /// <summary>글을 넣었다(그대로 또는 고쳐서).</summary>
    public bool IsApplied => Status is EditProposalStatus.Accepted or EditProposalStatus.Modified;
}
