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
