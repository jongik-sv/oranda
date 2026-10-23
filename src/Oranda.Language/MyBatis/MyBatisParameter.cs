using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

/// <summary>파라미터가 쓰인 곳(여럿이면 합친다).</summary>
[Flags]
public enum MyBatisParameterUsage
{
    None = 0,

    /// <summary>if·when test 식.</summary>
    Test = 1,

    /// <summary><c>#{}</c> 바인드.</summary>
    Bind = 2,

    /// <summary><c>${}</c> 치환.</summary>
    Substitution = 4,

    /// <summary>foreach collection(목록 값).</summary>
    Foreach = 8,

    /// <summary>bind 태그 value 식.</summary>
    BindTag = 16,
}

/// <summary>
/// 문장에서 찾은 파라미터 하나(MB-04, TRD 3.2). 계약(TSK-01-02) — 구현은 TSK-02-02(ExtractParameters).
/// foreach item·index·bind name 처럼 문장 안에서 생긴 이름은 넣지 않는다.
/// </summary>
/// <param name="Name">점 경로 이름(<c>empNo</c>, <c>user.name</c>).</param>
/// <param name="Usage">쓰인 곳.</param>
/// <param name="JdbcType"><c>#{x,jdbcType=…}</c> 타입 힌트(처음 나온 것). 없으면 null.</param>
/// <param name="JavaType"><c>#{x,javaType=…}</c> 힌트. 없으면 null.</param>
/// <param name="Locations">문장 원문 안 쓰인 범위들(원문 순서).</param>
public sealed record MyBatisParameter(
    string Name,
    MyBatisParameterUsage Usage,
    string? JdbcType,
    string? JavaType,
    IReadOnlyList<TextRange> Locations);
