using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 문장이 참조하는 객체 찾기(범위 검사의 기준). 구현은 ai-tools: Oranda 파서(<c>Oranda.Language</c>)로 표·뷰 참조를 뽑고
/// 동의어를 실제 owner.이름으로 푼다. Core 는 파서를 참조하지 않으므로 인터페이스만 둔다.
/// </summary>
public interface IStatementReferenceExtractor
{
    /// <summary>문장 글을 분석한다: 분류·참조 객체(동의어 푼 뒤)·문장 수.</summary>
    /// <param name="defaultSchema">이름에 소유자가 없을 때 쓰는 기본 스키마(<see cref="Contracts.AiContext.CurrentSchema"/>). null 이면 접속 사용자. 공유 세션의 현재 스키마를 바꾸지 않고 이 값으로 푼다.</param>
    ValueTask<AgentStatementInfo> AnalyzeAsync(ISession db, string sql, string? defaultSchema, CancellationToken ct);

    /// <summary><c>NAME</c> 또는 <c>OWNER.NAME</c> 을 실제 객체로 푼다(현재 스키마·동의어 반영). 없으면 null.</summary>
    ValueTask<AgentObjectRef?> ResolveAsync(ISession db, string name, string? defaultSchema, CancellationToken ct);

    /// <summary>
    /// 주어진 객체의 「상대」: 인덱스가 걸린 표, FK 로 이어진 부모·자식 표, 입력이 뷰면 그 기반 표(계획에 나오는 기반 표의 인덱스를 물을 수 있도록).
    /// 입력 객체는 포함하지 않는다.
    /// </summary>
    ValueTask<IReadOnlyList<AgentObjectRef>> GetRelatedAsync(ISession db, IReadOnlyList<AgentObjectRef> objects, CancellationToken ct);
}

/// <summary>실제 객체(동의어 푼 뒤). 이름은 대문자 정규화.</summary>
/// <param name="Owner">소유자.</param>
/// <param name="Name">이름.</param>
/// <param name="Kind">종류(<c>TABLE</c>·<c>VIEW</c> 등, 모르면 빈 문자열).</param>
public sealed record AgentObjectRef(string Owner, string Name, string Kind = "")
{
    /// <summary>비교 키 <c>OWNER.NAME</c>(대문자).</summary>
    public string Key => Owner.ToUpperInvariant() + "." + Name.ToUpperInvariant();
}

/// <summary>문장 분석 결과.</summary>
/// <param name="Kind">실효 분류(불확실하면 <see cref="IsUncertain"/>).</param>
/// <param name="IsUncertain">분류에 확신이 없다(정책은 DML 로 본다).</param>
/// <param name="Objects">문장이 참조하는 객체.</param>
/// <param name="StatementCount">문장 수(도구 인자는 한 문장이어야 한다).</param>
/// <param name="HasLockingClause"><c>FOR UPDATE</c> 등 행 잠금 절이 있다(주석을 끼운 <c>FOR/**/UPDATE</c> 포함). 게이트가 거부한다.</param>
/// <param name="HasRemoteReference">DB 링크(<c>@링크</c>) 참조가 있다. 게이트가 거부한다(다른 DB 로 나간다).</param>
/// <param name="CallsUserFunction">사용자 함수·패키지 호출이나 시퀀스 NEXTVAL 이 있다. 참조 객체에 드러나지 않는 부작용·우회라 게이트가 거부한다.</param>
public sealed record AgentStatementInfo(
    StatementKind Kind,
    bool IsUncertain,
    IReadOnlyList<AgentObjectRef> Objects,
    int StatementCount,
    bool HasLockingClause = false,
    bool HasRemoteReference = false,
    bool CallsUserFunction = false);
