using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Events;

// TRD 7장 이벤트 표. 발행자 → 구독자는 표 그대로.

/// <summary>세션 관리자 → 상태바, 스키마 브라우저, 모니터.</summary>
public sealed record SessionOpened(Guid SessionId, ConnectionProfile Profile, ServerInfo Server, SessionPurpose Purpose) : OrandaEvent;

/// <summary>세션 관리자 → 상태바, 스키마 브라우저, 모니터.</summary>
public sealed record SessionClosed(Guid SessionId) : OrandaEvent;

/// <summary>끊김 감지(CONN-11). <paramref name="HadPendingTransaction"/> 이면 미커밋 손실을 알린다.</summary>
public sealed record SessionBroken(Guid SessionId, bool HadPendingTransaction, DbError? Error) : OrandaEvent;

/// <summary>실행 엔진 → 상태바, 종료 확인.</summary>
public sealed record TransactionStateChanged(Guid SessionId, bool Pending) : OrandaEvent;

/// <summary>실행 엔진 → 이력 저장, 출력 창. <see cref="ExecutionOptions"/> 의 출처·이력 생략 여부를 함께 싣는다.</summary>
public sealed record StatementExecuted(Guid SessionId, SqlStatement Statement, ExecutionResult Result, ExecutionOptions Options) : OrandaEvent;

/// <summary>실행 엔진 → 출력 창.</summary>
public sealed record DbmsOutputReceived(Guid SessionId, IReadOnlyList<string> Lines) : OrandaEvent;

/// <summary>DDL 실행 후 / 변경 감지 → 자동완성 캐시, 스키마 트리. <paramref name="Objects"/> 가 비면 그 DB 전체.</summary>
public sealed record MetadataInvalidated(string DbKey, IReadOnlyList<DbObjectRef> Objects) : OrandaEvent;

/// <summary>정책 관문 → 로그(감사용 로컬 기록).</summary>
public sealed record PolicyDecisionMade(Guid? SessionId, SqlStatement Statement, PolicyDecision Decision) : OrandaEvent;

/// <summary>설정 서비스 → 에디터, 그리드, 테마. <paramref name="Section"/> 은 <c>AppSettings</c> 섹션의 JSON 이름(<c>editor</c>, <c>grid</c> …).</summary>
public sealed record SettingsChanged(string Section) : OrandaEvent;

/// <summary>
/// 접속 서비스 → AI 문맥 키. 접속 프로필을 저장했다(<paramref name="Profile"/> = 저장된 그대로). 열린 세션의 프로필은 접속할 때의 사본이라
/// 저장한 값(환경 라벨·AI 허용 수준 등)을 따라가지 않으므로, 최신 값이 필요한 쪽이 이 알림을 받는다.
/// </summary>
public sealed record ConnectionProfileSaved(ConnectionProfile Profile) : OrandaEvent;
