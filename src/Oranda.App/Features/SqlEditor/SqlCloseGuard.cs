namespace Oranda.App.Features.SqlEditor;

/// <summary>SQL 탭이 닫히는 이유. 가드는 이유에 따라 다르게 답할 수 있다.</summary>
public enum SqlCloseReason
{
    /// <summary>탭 하나를 닫는다(탭 머리 ×, 닫기 명령).</summary>
    Tab,

    /// <summary>작업 공간을 닫는다. 탭 내용은 작업 공간 파일에 남는다.</summary>
    Workspace,

    /// <summary>같은 ID 의 작업 공간을 복원해 열려 있던 탭이 교체된다.</summary>
    Replace,

    /// <summary>앱을 끝낸다.</summary>
    AppExit,
}

/// <summary>
/// 탭이 닫히기 전에 묻는 비동기 확인. <paramref name="tabs"/> 는 닫히려는 탭이다(비어 있지 않다).
/// 닫아도 되면 true, 취소하면 false 를 돌려준다. 예외는 취소로 본다(글을 잃는 쪽이 더 나쁘다).
/// </summary>
public delegate Task<bool> SqlCloseGuard(IReadOnlyList<SqlEditorViewModel> tabs, SqlCloseReason reason, CancellationToken ct);
