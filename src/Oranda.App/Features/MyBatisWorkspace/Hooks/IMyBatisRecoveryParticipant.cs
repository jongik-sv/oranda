using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.MyBatisWorkspace.Hooks;

/// <summary>복구 상태를 둘 곳: 작업 공간 저장본(<c>workspaces/</c>) 또는 자동 저장본(<c>autosave/</c>).</summary>
public enum MyBatisRecoveryTarget
{
    Saved,
    Autosave,
}

/// <summary>
/// MyBatis 작업 공간 복구 상태(design.md 5.6). 계약(TSK-01-02) — 구현은 03-01(열린 매퍼·문장·고침)·03-03(감시 기준) 등 여럿.
/// 상태는 작업 공간 저장본 곁 파일에 둔다(구현마다 자기 파일, 이름은 <c>{id}.mybatis-{Key}.json</c>, 폴더는 <c>IAppDataPaths</c> 의 작업 공간·자동 저장 폴더).
/// 호스트(01-02 훅)가 작업 공간 저장·자동 저장·삭제·ID 바꾸기·복원 때 함께 부른다. MyBatis 작업 공간(<see cref="SqlWorkspaceViewModel.MyBatis"/> 가 있음)이 아니면 구현이 아무것도 하지 않는다.
/// </summary>
public interface IMyBatisRecoveryParticipant
{
    /// <summary>곁 파일 이름에 쓰는 짧은 열쇠(<c>^[a-z0-9-]+$</c>, 구현마다 다름).</summary>
    string Key { get; }

    /// <summary>자동 저장 표지에 더할 글(바뀌었는지 보는 용도). 상태가 없으면 null.</summary>
    string? Fingerprint(SqlWorkspaceViewModel workspace);

    Task SaveAsync(SqlWorkspaceViewModel workspace, MyBatisRecoveryTarget target, CancellationToken ct);

    /// <summary>
    /// 작업 공간 복원(<see cref="SqlEditorHost.Restore(Oranda.Core.Workspaces.Workspace)"/>) 뒤 상태를 되돌린다(세션 다시 붙이기 등). UI 스레드에서 동기로 부른다.
    /// 곁 파일이 없으면 아무것도 하지 않는다.
    /// </summary>
    void Restore(SqlWorkspaceViewModel workspace, MyBatisRecoveryTarget target);

    Task DeleteAsync(string workspaceId, MyBatisRecoveryTarget target, CancellationToken ct);

    /// <summary>「작업 공간 저장」이 작업 공간 ID 를 바꿨다(저장본 곁 파일 이름을 따라 바꾼다).</summary>
    Task RenameAsync(string oldWorkspaceId, string newWorkspaceId, CancellationToken ct);
}
