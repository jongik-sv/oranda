using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.MyBatisWorkspace.Hooks;

/// <summary>
/// MyBatis 작업 공간 저장과 닫기 확인(MB-06, design.md 5장). 계약(TSK-01-02) — 구현은 03-03.
/// 부르는 곳: Ctrl+S(<c>file.save</c>)·<see cref="SqlEditorHost.SaveAsync"/>·<see cref="SqlEditorHost.SaveAllAsync"/>·<c>oranda.mybatis.save</c>,
/// 작업 공간 닫기(<see cref="SqlEditorHost.TryCloseWorkspaceAsync"/>, 세션 dirty 면 탭 수와 상관없이).
/// </summary>
public interface IMyBatisSaveHandler
{
    /// <summary>바뀐 문장만 매퍼에 쓴다(바뀌는 부분 창·검사·원자적 쓰기). 썼거나 쓸 것이 없으면 true, 취소·실패면 false.</summary>
    Task<bool> SaveAsync(SqlWorkspaceViewModel workspace, CancellationToken ct);

    /// <summary>저장 안 한 문장이 있는 매퍼를 닫기 전 「저장 / 저장 안 함 / 취소」. 닫아도 되면 true.</summary>
    Task<bool> ConfirmCloseAsync(SqlWorkspaceViewModel workspace, CancellationToken ct);
}
