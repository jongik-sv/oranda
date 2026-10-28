using Oranda.Core.Metadata;

namespace Oranda.Language.Ai.References;

/// <summary>열이 쓰인 절. 한 열이 여러 절에서 쓰이면 비트를 합친다.</summary>
[Flags]
public enum ColumnClause
{
    None = 0,
    Select = 1,
    Where = 2,
    Join = 4,
    Group = 8,
    Having = 16,
    Order = 32,
    /// <summary>SET·VALUES·INSERT 열 목록처럼 위에 들지 않는 자리.</summary>
    Other = 64,
}

/// <summary>문장에 적힌 그대로의 객체 참조(표·뷰·시노님 중 무엇인지는 아직 모른다). 이름은 따옴표가 없으면 대문자.</summary>
public sealed record ReferencedObject(string? Owner, string Name, string? DbLink = null)
{
    /// <summary>DB 링크 너머의 객체. 이 DB 에서는 조회하지 않는다.</summary>
    public bool IsRemote => DbLink is not null;

    /// <summary>적힌 모양(<c>OWNER.NAME</c> 또는 <c>NAME</c>).</summary>
    public string Display => Owner is null ? Name : Owner + "." + Name;
}

/// <summary>
/// 문장이 쓰는 열 하나. 별칭·표 이름으로 표가 정해지면 <see cref="Table"/> 에 담고,
/// 한정자 없이 쓴 열이 한 블록의 표 둘 이상과 겹치면 <see cref="Table"/> 은 null, 후보는 <see cref="Candidates"/> 에 담는다
/// (실제로 어느 표의 열인지는 부른 쪽이 열 목록과 대조해 정한다).
/// </summary>
public sealed record ColumnUse(ReferencedObject? Table, IReadOnlyList<ReferencedObject> Candidates, string Column, ColumnClause Clauses);

/// <summary>한 문장에서 찾은 객체 참조와 열 사용.</summary>
public sealed record StatementReferences(IReadOnlyList<ReferencedObject> Objects, IReadOnlyList<ColumnUse> Columns)
{
    /// <summary>문장에서 호출한 함수 이름(<c>PKG.FN</c> 또는 <c>FN</c>, 중복 없음). 내장 함수인지는 가리지 않는다.</summary>
    public IReadOnlyList<string> Calls { get; init; } = [];

    /// <summary><c>FOR UPDATE</c> 가 있다(주석·줄바꿈을 건너뛰고 토큰으로 판정).</summary>
    public bool HasLockingClause { get; init; }

    /// <summary><c>@링크</c> 로 원격 함수·열을 참조한다(FROM 의 원격 표는 <see cref="ReferencedObject.IsRemote"/>).</summary>
    public bool HasRemoteLink { get; init; }

    /// <summary>객체를 다 찾았다고 확신하지 못한다(괄호로 묶은 조인, TABLE(…), 예약어 이름의 표, 시점 식 안의 부분 질의).</summary>
    public bool Uncertain { get; init; }

    /// <summary><paramref name="obj"/> 에 속하거나 속할 수 있는 열 이름(중복 없음, 처음 나온 순서).</summary>
    public IReadOnlyList<string> ColumnsOf(ReferencedObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var c in Columns)
        {
            if ((c.Table == obj || c.Candidates.Contains(obj)) && seen.Add(c.Column))
            {
                list.Add(c.Column);
            }
        }

        return list;
    }
}

/// <summary>동의어를 푼 결과. <see cref="Target"/> 이 null 이면 <see cref="Problem"/> 이 이유다.</summary>
public sealed record ResolvedReference(ReferencedObject Written, DbObjectRef? Target, string? Problem);
