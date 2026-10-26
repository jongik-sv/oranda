using Oranda.Core.Sessions;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>
/// SQL 작업 공간이 아니면서 자기 접속을 가진 문서(PL/SQL 편집기·PL/SQL 작업 공간, TSK-12-04). 툴바 접속 콤보는 이 문서가 활성이면
/// 그 접속을 보이고, 고르면 그 문서의 접속을 바꾼다(<see cref="SessionRouter.SelectAsync"/>) — 다음 컴파일·실행부터 고른 DB 로 간다.
/// </summary>
public interface IConnectionDocument
{
    /// <summary>문서가 쓰는 세션. 아직 없으면(복원한 탭이 제 접속을 기다림) null.</summary>
    ISession? Session { get; }

    /// <summary>컴파일·실행 중이라 접속을 바꿀 수 없다.</summary>
    bool IsSessionBusy { get; }

    /// <summary>문서의 접속을 <paramref name="session"/> 으로 바꾼다. 바꿨거나 이미 그 접속이면 true, 바꿀 수 없으면 false.</summary>
    bool UseSession(ISession session);

    /// <summary><see cref="Session"/> 이 바뀌었다(보이는 탭이 바뀐 것도 포함).</summary>
    event EventHandler? SessionChanged;
}
