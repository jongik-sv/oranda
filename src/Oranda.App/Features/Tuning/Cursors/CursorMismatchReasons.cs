using System;
using System.Collections.Generic;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.Tuning.Cursors;

/// <summary>
/// gv$sql_shared_cursor 의 불일치 열 이름 → 뜻(앱 안 사전, ko/en). 사전에 없는 열은 열 이름만 보인다(<see cref="Meaning"/> = null).
/// </summary>
public static class CursorMismatchReasons
{
    private static readonly Dictionary<string, (string Ko, string En)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BIND_MISMATCH"] = ("바인드 변수의 형·길이가 달라 공유하지 못함", "Bind variable types or lengths differ"),
        ["BIND_EQUIV_FAILURE"] = ("바인드 값 선택도가 달라 새 자식을 만듦 (Adaptive Cursor Sharing)", "Bind selectivity differed, so a new child was built (Adaptive Cursor Sharing)"),
        ["BIND_LENGTH_UPGRADEABLE"] = ("문자 바인드 길이 구간이 바뀜 (32 → 128 → 2000 …)", "Character bind length moved to a larger bucket (32 → 128 → 2000 …)"),
        ["BIND_UACS_DIFF"] = ("바인드 인식 커서 여부가 다름 (ACS)", "Bind-aware state differs (ACS)"),
        ["BIND_PEEKED_PQ_MISMATCH"] = ("병렬 실행에서 엿본 바인드 값이 다름", "Peeked bind values differ for parallel execution"),
        ["ROLL_INVALID_MISMATCH"] = ("통계 갱신 뒤 천천히 무효화(롤링)", "Rolling invalidation after a statistics refresh"),
        ["OPTIMIZER_MISMATCH"] = ("optimizer 환경(세션 매개변수)이 다름", "Optimizer environment (session parameters) differs"),
        ["OPTIMIZER_MODE_MISMATCH"] = ("optimizer_mode 가 다름 (ALL_ROWS·FIRST_ROWS)", "optimizer_mode differs (ALL_ROWS / FIRST_ROWS)"),
        ["LANGUAGE_MISMATCH"] = ("NLS 언어 설정이 다름", "NLS language settings differ"),
        ["TRANSLATION_MISMATCH"] = ("문장 속 객체가 다른 스키마의 객체로 풀림", "Objects in the statement resolve to a different schema"),
        ["AUTH_CHECK_MISMATCH"] = ("권한 점검 결과가 다름 (다른 사용자가 실행)", "Authorization check differs (run by another user)"),
        ["STATS_ROW_MISMATCH"] = ("SQL 트레이스 등으로 통계 수집 방식이 다름", "Statistics collection differs (for example SQL trace)"),
        ["USE_FEEDBACK_STATS"] = ("카디널리티 피드백으로 다시 최적화함", "Re-optimized from cardinality feedback"),
        ["LOAD_OPTIMIZER_STATS"] = ("옵티마이저 통계를 새로 읽어야 함", "Optimizer statistics had to be reloaded"),
        ["PQ_SLAVE_MISMATCH"] = ("병렬 슬레이브의 환경이 다름", "Parallel slave environment differs"),
        ["PX_MISMATCH"] = ("병렬 실행 설정이 다름", "Parallel execution settings differ"),
        ["MULTI_PX_MISMATCH"] = ("여러 병렬 서버 설정이 다름", "Multiple parallel server settings differ"),
        ["TOP_LEVEL_RPI_CURSOR"] = ("최상위·재귀 호출 여부가 다름", "Top-level vs recursive call differs"),
        ["ROW_LEVEL_SEC_MISMATCH"] = ("행 수준 보안(VPD) 술어가 다름", "Row-level security (VPD) predicates differ"),
        ["SQL_TYPE_MISMATCH"] = ("SQL 형이 다름", "SQL type differs"),
        ["LITERAL_MISMATCH"] = ("리터럴 처리(cursor_sharing)가 다름", "Literal handling (cursor_sharing) differs"),
        ["INST_DRTLD_MISMATCH"] = ("직접 경로 적재 여부가 다름", "Direct-path load setting differs"),
        ["MV_REWRITE_MISMATCH"] = ("구체화 뷰 재작성 설정이 다름", "Materialized view rewrite settings differ"),
        ["TYPCHK_DEP_MISMATCH"] = ("형 점검 의존 관계가 다름", "Type-check dependencies differ"),
        ["NO_TRIGGER_MISMATCH"] = ("트리거 유무가 다름", "Trigger presence differs"),
        ["FLASHBACK_CURSOR"] = ("플래시백 질의 커서는 공유하지 않음", "Flashback query cursors are not shared"),
        ["PURGED_CURSOR"] = ("DBMS_SHARED_POOL.PURGE 로 지움", "Purged with DBMS_SHARED_POOL.PURGE"),
        ["INCOMPLETE_CURSOR"] = ("만들다 만 커서", "Cursor was not fully built"),
        ["TOP_LEVEL_DDL_MISMATCH"] = ("최상위 DDL 커서", "Top-level DDL cursor"),
        ["PLSQL_DEBUG"] = ("PL/SQL 디버그 설정이 다름", "PL/SQL debug setting differs"),
        ["CURSOR_PARTS_MISMATCH"] = ("커서 구성 부분이 다름", "Cursor parts differ"),
        ["STB_OBJECT_MISMATCH"] = ("SQL Profile·Baseline 등 계획 고정 객체가 바뀜", "Plan stability object (SQL profile, baseline) changed"),
        ["PDML_ENV_MISMATCH"] = ("병렬 DML 환경이 다름", "Parallel DML environment differs"),
        ["HASH_MATCH_FAILED"] = ("해시가 맞지 않아 새로 만듦", "Hash match failed"),
        ["OPTIMIZER_MODE_HINTED_MISMATCH"] = ("힌트로 정한 optimizer_mode 가 다름", "Hinted optimizer_mode differs"),
        ["SLAVE_QC_MISMATCH"] = ("병렬 조정자·슬레이브 커서가 다름", "Parallel coordinator / slave cursor differs"),
        ["ACCESS_PATH_MISMATCH"] = ("접근 경로가 달라짐", "Access path changed"),
    };

    /// <summary>뜻(지금 언어). 사전에 없으면 null.</summary>
    public static string? Meaning(string reason) =>
        Table.TryGetValue(reason ?? string.Empty, out var m) ? LiteralTranslator.IsEnglish ? m.En : m.Ko : null;
}
