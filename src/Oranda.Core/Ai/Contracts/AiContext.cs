using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;

namespace Oranda.Core.Ai.Contracts;

/// <summary>
/// AI 요청에 실어 보내는 편집기·접속 문맥(설계 §2.3). 비밀번호·호스트·서비스 이름은 담지 않는다 — 접속 별칭과 환경 라벨만.
/// </summary>
/// <param name="ConnectionName">접속 별칭(<see cref="ConnectionProfile.Name"/>). 접속이 없으면 null.</param>
/// <param name="Environment">접속의 환경 라벨. 접속이 없으면 null.</param>
/// <param name="AccessLevel">이 접속의 실효 허용 수준(<see cref="AiAccessPolicy.Effective"/>).</param>
/// <param name="TabId">요청을 낸 편집기 탭 id.</param>
/// <param name="StatementText">튜닝·설명 대상 문장 글(선택 또는 커서 위치 문장).</param>
/// <param name="SqlId">대상 문장의 sql_id 를 알면 그 값(실제 계획·통계 조회용).</param>
/// <param name="CurrentSchema">편집기 세션의 현재 스키마(<c>ALTER SESSION SET CURRENT_SCHEMA</c> 반영). 문장의 객체 이름을 푸는 기본 스키마다. null 이면 접속 사용자.</param>
public sealed record AiContext(
    string? ConnectionName,
    ConnectionEnvironment? Environment,
    AiAccessLevel AccessLevel,
    string? TabId = null,
    string? StatementText = null,
    string? SqlId = null,
    string? CurrentSchema = null);
