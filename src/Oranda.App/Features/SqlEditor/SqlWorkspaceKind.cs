namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// SQL 작업 공간의 문서 종류(design.md 2.1). 도구 창 보임 규칙·명령·문맥 키가 보는 값이며,
/// 문서 탭 머리 글자로 남는 원본 표시 <see cref="SqlWorkspaceViewModel.Kind"/>(md·sqd·MyBatis)와는 별개다.
/// </summary>
public enum SqlWorkspaceKind
{
    /// <summary>일반 SQL 작업 공간(MyBatis 세션이 없다). 읽기 전용 SQL 펼침으로 연 .xml 작업 공간도 여기에 든다.</summary>
    Sql,

    /// <summary>MyBatis 작업 공간: <see cref="SqlWorkspaceViewModel.MyBatis"/> 에 세션이 붙어 있다.</summary>
    MyBatis,
}

/// <summary>작업 공간이 쓸 세션(TSK-14-01).</summary>
public enum WorkspaceSessionMode
{
    /// <summary>접속의 세션(프로필의 첫 작업 세션)을 다른 작업 공간·도구 창과 함께 쓴다(지금까지의 동작).</summary>
    Shared,

    /// <summary>자기 세션: 첫 실행 때 접속의 세션을 아무도 쓰지 않으면 그것을, 다른 작업 공간이 쓰고 있으면 같은 DB 에 세션을 하나 더 연다.</summary>
    Own,
}

/// <summary><see cref="WorkspaceSessionMode"/> ↔ 작업 공간 파일의 글자(<c>shared</c>·<c>own</c>).</summary>
public static class WorkspaceSessionModes
{
    public static WorkspaceSessionMode? Parse(string? text) => text switch
    {
        "own" => WorkspaceSessionMode.Own,
        "shared" => WorkspaceSessionMode.Shared,
        _ => null,
    };

    public static string? Format(WorkspaceSessionMode? mode) => mode switch
    {
        WorkspaceSessionMode.Own => "own",
        WorkspaceSessionMode.Shared => "shared",
        _ => null,
    };
}
