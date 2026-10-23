namespace Oranda.Core.MyBatis;

/// <summary>sql 조각 하나(include 대상). <see cref="Text"/> 는 그 <c>&lt;sql&gt;</c> 요소 원문(여는 태그 ~ 닫는 태그)이다.</summary>
/// <param name="Id">조각 id.</param>
/// <param name="Namespace">조각이 든 매퍼의 namespace(없으면 null).</param>
/// <param name="Text">요소 원문.</param>
/// <param name="MapperPath">조각이 든 매퍼 파일 경로(알면). 다른 파일 조각을 보일 때 쓴다.</param>
public sealed record MyBatisFragment(string Id, string? Namespace, string Text, string? MapperPath = null);

/// <summary>
/// include 의 sql 조각 찾기(MB-02·MB-10, TRD 4). 계약(TSK-01-02).
/// 같은 매퍼 안 조각은 세션·계산기 쪽 구현(TSK-02-02·03-01), 다른 파일 조각은 폴더 구현(TSK-03-07)이 맡는다.
/// App 이 구현하므로 Core 에 둔다(Language 에 두면 시작 때 Oranda.Language 가 로드된다, design.md 9장·D-008).
/// </summary>
public interface IMyBatisFragmentResolver
{
    /// <summary>
    /// <paramref name="refid"/>(<c>empCols</c> 또는 <c>com.acme.hr.EmpMapper.empCols</c>)의 sql 조각. 이름에 namespace 가 없으면
    /// <paramref name="currentNamespace"/>(include 를 쓴 문장의 매퍼)에서 찾는다. 없으면 null(계산기가 「조각 없음」으로 알린다).
    /// </summary>
    MyBatisFragment? Resolve(string refid, string? currentNamespace);
}
