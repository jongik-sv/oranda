# ai-tuning-4 설계 메모

사용자 요청(2026-10-29 14:45 무렵, 캡처): AI 대화 창 도구 카드가 `get_index_info  1,809 자  허용` 처럼 이름·크기·판단만 보여 「어떤 내용인지 모르겠다, 좀 더 자세하게」. dev-jji-win144 = 2bb161a.

## 1. 도구 카드 자세히 (c1)
- 지금: `src/Oranda.App/Features/Ai/Chat/AiChatViewModel.cs:22` ChatToolEntry(Name, ResponseSize, Verdict, Done, CallId). 드라이버 사건에는 이미 더 있음 → `src/Oranda.Core/Ai/Contracts/AiEvent.cs:15` AiToolCall.ArgumentsSummary(화면용 인자 요약), `:18` AiToolResult.Note(거부·실패 이유). 화면이 버리고 있음
- 보일 것(한 줄 + 펼침):
  - 한 줄: 도구를 사람 말로(예 「인덱스 정보 조회 · MESAPUSER.TB_M30_SMTL_CSM_CMN」, 「실행 계획(EXPLAIN) · SQL 598자 · 자리표시 2」, 「테이블 통계 · APSUSER.TB_… (열 3)」), 판단, 응답 크기, 걸린 시간이 있으면 시간
  - 거부·실패면 이유를 한 줄로 사람 말로(예 sql_unsafe → 「안전하다고 판정할 수 없는 SQL 이라 보내지 않음」) + 원래 코드(sql_unsafe)
  - 눌러서 펼치면: 인자 요약 원문(가린 글 그대로, :vN 유지·원래 값 되돌림 금지), 이유 코드·설명
- 도구 9개 각각의 사람 말 이름·핵심 인자 고르기 표를 한 곳(순수 C#)에 두고 시험. 모르는 도구는 이름 그대로
- 원래 값이 화면에 새지 않을 것: ArgumentsSummary 가 이미 가린 글인지 확인(드라이버가 LLM 이 보낸 인자를 요약 = LLM 은 가린 글만 봄). 아니면 Redact
- 여러 번 같은 도구가 이어지면(인덱스 정보 7번) 묶음 표시는 이번엔 안 함(기본안). 필요해 보이면 질문

## 2. get_tuning_context 의 sql_unsafe 거부 (c2)
- 증상(사용자 앱 감사 기록 id 12, 운영 USMEAP, 2026-10-29 05:45Z): get_tuning_context arguments=`literals=masked, placeholders=0, budget_tokens=***` → Denied, reason=sql_unsafe, 159자. 대상 문장 머리 = `WITH CSM_BASE AS (  /**/  SELECT D...`(대화 창 제목). AI 는 대신 get_index_info 7번·get_table_stats·explain_plan 으로 우회
- 추정(확인 필요): 가림이 주석을 `/**/` 로 비운 글, 또는 WITH 절을 문장 참조 추출기(fail-closed)가 안전하지 않다고 봄. placeholders=0 이라 sql 인자 없이 대상 문장(편집기 원문)을 쓴 경로일 수 있음
- 할 일: sql_unsafe 를 내는 곳을 찾아 이 꼴(WITH 여러 CTE + 빈 주석 + 리터럴)로 재현 시험을 먼저 만들고 원인을 고침. fail-closed 원칙은 유지(진짜 위험한 문장은 계속 거부). 원인이 소유 밖(Language 렉서·추출기)이면 질문
- 사용자 원문 SQL 은 운영 DB 문장이라 레인이 받지 않음. 꼴만 흉내 낸 가짜 SQL 로 재현
