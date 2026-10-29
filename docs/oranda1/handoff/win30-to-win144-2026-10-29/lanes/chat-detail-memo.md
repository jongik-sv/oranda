# chat-detail 정본 메모 (지시 chat-detail-1~3, 회차 ai-tuning-4) — 2026-10-29 중단(win144 로 이전)

- 조정 세션: oranda-55 / 워크트리 oranda-wt/chat-detail / 브랜치 chat-detail / 기준 2bb161a
- 끝난 항목(체크포인트 커밋): c2(BuiltIns 보강, 970c0a9), c1 의 순수 C# 부분(ToolCardText 표·시험 27개 + CliDriver 인자 요약 300→4000자, f12bde1), c4(프롬프트 응답 규칙 ResponseStyle, 3baeb88)
- 하던 항목 c7(사용자 함수 허용, 우선): 커밋은 wip. 완료분 = Core(BenchmarkCase.ReadOnly, IUserFunctionSafetyChecker, AgentStatementInfo.HasForbiddenCall·UserFunctions, AgentGate 분기·user_functions=N 감사·ReadOnlyExecution), Language 추출기 분류(IsForbiddenCall), UserFunctionSafetyChecker(+UserFunctionGraphWalker), OracleTuningService.RunBenchmarkAsync READ ONLY. 
- c7 남은 일: (1) 빌드·기존 시험(AgentGateTests·AgentHandlersTests 의 sql_unsafe 메시지/분기) 확인 (2) BenchmarkQueriesHandler→TuningToolbox.BenchmarkAsync 가 inv.ReadOnlyExecution 을 BenchmarkCase.ReadOnly 로 넘기게(아직 안 함) (3) 시험: 게이트(사용자 함수 허용/자치·외부·미확인 거부/NEXTVAL·@링크 유지, 실행 도구만 확인), 추출기 분류, Walker 가짜 query, 26ai 통합 3건(함수→함수→자치 전이, UTL_HTTP 의존, 평범한 함수 benchmark 허용+함수 안 INSERT 차단) (4) c3 의 opus 리뷰에 포함. 남는 위험 = EXECUTE IMMEDIATE 숨은 호출.
- c5 진행: 고치기 전 PNG 찍음(scratchpad/before). MarkdownView·MarkdownParser 아직 수정 안 함(인용·수평선·간격·SQL 강조).
- c1 남은 일·c6: oranda-02 커밋이 dev 에 들어왔다는 조정자 알림 뒤 AiChatView.axaml·AiChatViewModel.cs·AiChatTests 를 고칠 것(ChatToolEntry 에 Title·Detail·Reason·펼침, 진행 카드가 ResponseSize 칸에 인자 요약을 넣던 것 정리, AiChatMarkdownHeadlessTests 의 `t.Text == "get_cursor_plan"` 단언 갱신 필요). c6 = 제안 「다시 보기」.
- 깨진 시험: 아직 빌드 안 함 → Core/Language/App 시험이 깨졌을 수 있음(AgentGate sql_unsafe 문구 변경, CheckSqlAsync 시그니처).
- c3(머지 요청)은 모든 항목 끝낸 뒤 한 번에: heavy 시험 → PNG → opus/high 리뷰(c2·c7 fail-closed) → squash-branch → 머지 요청.
