using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Rollback;

namespace Oranda.App.Features.GridEdit;

/// <summary>
/// 운영 접속 그리드 반영의 확인 한 번(사용자 요청 2026-11-16). 반영 하나가 행마다 DML 을 실행 엔진(정책 관문)으로 보내도
/// 운영 확인 창은 첫 문장에서 한 번만 뜨고, 그 창은 미리보기 창이 보이던 글(모든 문장 + 바인드 값 줄)과 커밋 안내를 보인다.
/// 뒤 문장은 창 없이 첫 답을 다시 쓴다. 범위는 실행 흐름(AsyncLocal)을 따라가고 반영 호출 동안만 열리며(<see cref="Enter"/>),
/// 이번 반영이 만든 DML 글(<see cref="Covers"/>)에만 걸린다. 판단(어느 문장을 물을지)은 정책 관문 그대로다. 이 범위는 묻는 모양과 횟수만 바꾼다.
/// </summary>
public sealed class GridEditConfirmScope
{
    private static readonly AsyncLocal<GridEditConfirmScope?> s_current = new();
    private readonly HashSet<string> _texts;
    private bool _firstOffered;

    private GridEditConfirmScope(string preview, bool commit, ConnectionProfile? profile, IReadOnlyList<SqlStatement> statements)
    {
        Preview = preview;
        Commit = commit;
        Profile = profile;
        Statements = Math.Max(1, statements.Count);
        _texts = new HashSet<string>(statements.Select(x => x.Text), StringComparer.Ordinal);
        Verbs = string.Join(", ", statements.GroupBy(x => x.Verb ?? "DML").Select(g => $"{g.Key} {g.Count()}"));
    }

    /// <summary>지금 실행 흐름의 범위. 그리드 반영 밖이면 null.</summary>
    public static GridEditConfirmScope? Current => s_current.Value;

    /// <summary>확인 창 본문: 미리보기 창이 보이던 글(생성 DML 모두와 바인드 값 줄).</summary>
    public string Preview { get; }

    /// <summary>반영 뒤 바로 커밋하는 동작이다.</summary>
    public bool Commit { get; }

    /// <summary>이 반영이 실행할 DML 문장 수(확인 창 원복 줄이 보인다).</summary>
    public int Statements { get; }

    /// <summary>문장 종류별 수(「UPDATE 2, DELETE 1」). 여러 문장이면 확인 창 메시지에 붙인다.</summary>
    public string Verbs { get; }

    /// <summary>반영하는 세션의 접속(확인 창 머리 띠). 모르면 null.</summary>
    public ConnectionProfile? Profile { get; }

    /// <summary>확인 창의 커밋 안내 한 줄(미리보기 창의 안내와 같은 뜻).</summary>
    public string Notice => NoticeFor(Commit);

    /// <summary>첫 확인의 답. 아직 묻지 않았으면 null.</summary>
    public ConfirmAnswer? Answer { get; private set; }

    /// <summary>사용자가 확인 창에서 취소했다.</summary>
    public bool Declined => Answer is { Proceed: false };

    /// <summary>확인 창이 뜬 횟수(이 범위 안).</summary>
    public int Asked { get; private set; }

    /// <summary>커밋 안내(DML 미리보기 창과 같은 글): 반영 = 「… 커밋하지 않습니다 …」, 반영 후 커밋 = 「… 바로 커밋합니다.」.</summary>
    public static string NoticeFor(bool commit) => commit
        ? "아래 DML 을 실행한 뒤 바로 커밋합니다."
        : "아래 DML 만 실행하고 커밋하지 않습니다. 커밋 또는 롤백을 해야 확정됩니다.";

    /// <summary>범위를 만든다(아직 열지 않는다). <paramref name="statements"/> = 이번 반영이 실행할 생성 DML.</summary>
    public static GridEditConfirmScope Create(string preview, bool commit, ConnectionProfile? profile, IReadOnlyList<SqlStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        return new GridEditConfirmScope(preview ?? string.Empty, commit, profile, statements);
    }

    /// <summary>지금 실행 흐름에서 범위를 연다. 돌려준 값을 Dispose 하면 이전 범위로 돌아간다.</summary>
    public IDisposable Enter()
    {
        var previous = s_current.Value;
        s_current.Value = this;
        return new Restore(previous);
    }

    /// <summary>이번 반영이 만든 DML 이다(그 밖의 문장은 이 범위로 묻지 않는다).</summary>
    public bool Covers(SqlStatement stmt) => stmt is not null && _texts.Contains(stmt.Text);

    /// <summary>
    /// 정책 관문의 확인을 이 범위로 받는다. 첫 물음은 <paramref name="ask"/> 로 창을 띄우고, 뒤 물음은 첫 답을 다시 쓴다.
    /// 원복 스크립트: 첫 창에 확인란이 있었으면 그 선택을(그 문장의 제안이 켤 수 있을 때), 없었으면 그 문장 제안의 기본값. 백업 테이블은 만들지 않는다.
    /// 다시 입력을 요구하는 판단은 늘 따로 묻는다.
    /// </summary>
    public async Task<ConfirmAnswer> ConfirmAsync(PolicyDecision decision, ConfirmExtras extras, Func<Task<ConfirmAnswer>> ask)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(ask);
        if (Answer is { } first && string.IsNullOrWhiteSpace(decision.RetypeText))
        {
            if (!first.Proceed)
            {
                return ConfirmAnswer.Declined;
            }

            return _firstOffered
                ? new ConfirmAnswer(true, first.CreateScript && extras?.Rollback is { Applicable: true, OverLimit: false }, null)
                : ConfirmAnswer.Default(extras);
        }

        Asked++;
        var answer = await ask().ConfigureAwait(false);
        if (Answer is null)
        {
            Answer = answer;
            _firstOffered = extras?.Rollback is { Applicable: true, OverLimit: false };
        }

        return answer;
    }

    private sealed class Restore(GridEditConfirmScope? previous) : IDisposable
    {
        public void Dispose() => s_current.Value = previous;
    }
}
