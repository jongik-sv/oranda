using Oranda.App.Features.GridEdit;
using Oranda.Core.Execution;

namespace Oranda.App.Features.ResultGrid;

// SqlResultPaneViewModel 의 일부: 결과 탭 줄의 편집 모드 단추(같은 쿼리를 편집 모드로 다시 실행했다가 읽기 결과로 돌아간다).
public sealed partial class SqlResultPaneViewModel
{
    /// <summary>
    /// 반영하지 않은 변경이 있는 채로 읽기 결과로 돌아가도 되는지 묻는 함수(예 = 변경을 버린다). 편집기 결과 탭이 채운다.
    /// 비어 있으면 묻지 못하므로 돌아가지 않는다(변경을 조용히 버리지 않는다).
    /// </summary>
    public Func<CancellationToken, Task<bool>>? ConfirmDiscardEdits { get; set; }

    /// <summary>
    /// 이 결과를 만든 문장이 조회(SELECT) 하나로 분류가 확실하다. 편집 모드 단추는 문장을 다시 실행하므로 부작용이 있을 수 있는
    /// PL/SQL 블록·스크립트의 마지막 결과·분류가 불확실한 문장(REF CURSOR 결과 포함)에서는 켜지 않는다 — 켜기와 돌아가기 모두.
    /// </summary>
    public bool IsPlainQuery => Statement is { Kind: StatementKind.Query, IsClassificationUncertain: false };

    /// <summary>
    /// 편집 세션이 붙어 있다(고칠 수 없는 결과로 다시 실행되어 <see cref="IsEditMode"/> 가 false 여도 true). 탭 제목에 「(편집)」이 남는 상태이므로
    /// 단추로 읽기 결과로 돌아갈 수 있어야 한다.
    /// </summary>
    public bool HasEditSession => _editSession is not null;

    /// <summary>
    /// 셀 편집기가 열려 있으면 값을 먼저 확정해 달라는 요청(결과 영역 화면이 받는다). 행 번호나 결과를 바꾸는 단추(편집 모드·열 필터)가
    /// 편집기에 친 값을 잃거나 다른 행에 넣지 않게 부른다 — 단추는 포커스를 받지 않아 편집기가 스스로 확정하지 않는다.
    /// </summary>
    public event EventHandler? CommitEditorRequested;

    internal void RequestCommitEditor() => CommitEditorRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 편집 모드 단추: 읽기 결과면 같은 문장을 편집 모드로 다시 실행하고(F9 와 같은 길·같은 조건, 못 하면 기존 안내 막대),
    /// 편집 모드면 같은 문장을 일반 실행해 읽기 결과로 돌아간다. 반영하지 않은 변경이 있으면 먼저 묻고 아니라고 하면 그대로 둔다.
    /// </summary>
    public async Task ToggleEditModeAsync(CancellationToken ct = default)
    {
        if (!IsPlainQuery)
        {
            return;
        }

        RequestCommitEditor(); // 열린 셀 편집기의 값을 먼저 확정한다(그러면 아래 변경 확인에 잡힌다)
        if (_editSession is null)
        {
            if (!await StartEditModeAsync(ct).ConfigureAwait(true))
            {
                ShowEditHint();
            }

            return;
        }

        if (Statement is not { } stmt || GridEditCommands.RerunReadOnly is not { } rerun)
        {
            return;
        }

        if (IsEditDirty && (ConfirmDiscardEdits is not { } ask || !await ask(ct).ConfigureAwait(true)))
        {
            return;
        }

        await rerun(stmt, ct).ConfigureAwait(true);
    }
}
