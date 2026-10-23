using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.MyBatisWorkspace.Hooks;

/// <summary>
/// .xml 매퍼를 MyBatis 작업 공간으로 연다(MB-03, design.md 9장). 계약(TSK-01-02) — 구현은 03-01.
/// 부르는 곳: <see cref="SqlEditorHost.OpenWorkspaceFileAsync"/>·<see cref="SqlEditorHost.OpenFileAsync"/> 의 .xml 분기(명령줄·끌어 놓기 포함).
/// 구현이 없으면 지금 경로(읽기 전용 SQL 펼침 / 글자 탭)로 간다.
/// </summary>
public interface IMyBatisOpenHandler
{
    /// <summary>
    /// <paramref name="fullPath"/> 를 연다. 같은 파일의 MyBatis 작업 공간이 있으면 그리로 옮겨 간다. 처리했으면 true(호스트는 지금 경로로 가지 않는다).
    /// 루트가 mapper 가 아니거나 깨진 xml 이면 알리고 true(작업 공간을 만들지 않음). 다루지 않을 파일이면 false.
    /// </summary>
    Task<bool> TryOpenAsync(SqlEditorHost host, string fullPath, CancellationToken ct);
}
