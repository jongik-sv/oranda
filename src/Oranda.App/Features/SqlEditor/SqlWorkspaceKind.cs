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

/// <summary>
/// 함께 쓰는 세션이 다른 작업 공간의 실행으로 바쁠 때 실행을 누르면 작업 공간 위에 보이는 안내 막대의 내용(TSK-14-03, D-036).
/// 「새 세션에서 실행」 은 이 작업 공간을 새 세션으로 옮긴 뒤 누른 실행을 다시 보낸다. 「기다리기」 는 막대를 닫는다.
/// </summary>
/// <param name="SessionId">바쁜(함께 쓰는) 세션. 그 실행이 끝나면 막대를 닫는다.</param>
/// <param name="Text">막대 글(실행 중인 작업 공간 이름, 트랜잭션은 옮기지 않는다는 안내).</param>
/// <param name="RunInNewSession">「새 세션에서 실행」.</param>
/// <param name="Wait">「기다리기」.</param>
public sealed record SessionBusyNotice(Guid SessionId, string Text, Func<Task> RunInNewSession, Action Wait);
