# ai-tuning-2 회차 보고 (2026-10-29, win30)

## 결과
운영 DB 에서 AI 튜닝을 쓸 수 있게 만들었습니다. 로컬 `dev-jji-win144` = `5cbcdc6`(앞 회차 5f74505 위 커밋 6개). push 는 하지 않았습니다.

| 머지 | 커밋 | 내용 |
|---|---|---|
| mask-core c1 | 6b5253e | 리터럴 가림 계약: ISqlLiteralMasker·LiteralVault, AgentRunScope.Literals, SqlTextFields, AiAccessPolicy.MaskLiterals |
| mask-core c2 | 7333819 | 운영 상한 Plan→Query, 운영 EXPLAIN 기본 켬, 운영 benchmark_queries 허용(문장당 30초 상한) |
| mask-core c3+c4 | 2907b15 | SqlLiteralMasker(렉서 기반, DATE/TIMESTAMP 묶음, 주석 비움, DML 뒤 첫 힌트만 유지·안의 값 가림) + Gate 통합(인자 되돌림·literal_unknown, 가림 필요인데 사전·마스커 없으면 SQL 도구 닫힘, 결과 SqlTextFields 가림, 감사 literals=masked), AgentRunScope.MaskLiteralsRequired |
| mask-core c5 | 1607234 | 26ai 통합 시험: 가린 인자로 explain·cursor_plan·운영 benchmark, 원문 없음 확인 |
| mask-app | 5c7dac4 | 시작 창 모델 opus·sonnet·haiku(설정 값 선택), 흐름 가림·제안/인덱스 되돌림, 시작 창 가림 줄·운영 안내, 설정 MaskLiterals 확인란, 운영 benchmark 확인란(기본 끔), DISPLAY__CURSOR 접근 키 결함 |
| mask-fix | 5cbcdc6 | z2 결함: 인덱스 탭이 활성일 때 재작성 제안이 버려짐, 인덱스 후보 탭 중복. 감사 = 가린 글 길이·placeholders=N. AI 대화 창 Markdown 표시(새 패키지 없음, 선택·복사 가능) |

## 확인
- 머지 6건 모두 트리 대조 일치. 메인 `dotnet build Oranda.sln` 경고 0·오류 0(마지막 mask-fix 는 z2 앱의 dll 잠금 때문에 같은 트리의 워크트리 빌드로 대신함)
- z1: dev 위 Oracle 통합 시험 Ai·Tuning 77/77, 건너뜀 0(podman 26ai)
- z2: 새 데이터 폴더, 운영 라벨·Query 접속 「AIT-prod」, 감시값 'ZQX9SENT'·735193·DATE '2026-03-17', haiku
  - 앱이 띄운 claude 하위 프로세스 기록(c26765f8…jsonl, 212KB)에서 감시값 0건, `:v1` 57건(조정자가 직접 다시 확인)
  - 도구 5개(get_tuning_context·get_cursor_plan·get_index_info·explain_plan·benchmark_queries) 성공, 제안 비교 창은 원래 값으로 되돌림, agent_audit 6행 모두 literals=masked·감시값 0
  - 캡처 = b9c3510b scratchpad\z2\cap\
- 레인 시험(마지막): Core 741/741, App 4280/4281(RoleEdgeCaseTests 간헐 1건, 단독 재실행 통과), Language Ai 99, Platform Ai 49(+1 기존 건너뜀), Architecture 13

## 사용자 결정
- 운영 EXPLAIN 기본 켬, 운영 상한 Query·benchmark 허용(SELECT 만·5회), 리터럴 가림 이번에 넣음(운영은 항상 켬, 끌 수 없음)
- 빠르게(time) 모드로 전환(부하 금지 해제. 부하 주범 = Defender·보안 프로그램)
- 운영 benchmark 확인란 기본값 = 꺼짐 유지
- 결정 14 위험 수용: 같은 원문 = 같은 번호라서 LLM 이 추측값을 넣어 원래 값을 확인할 수 있음. 운영 Query+benchmark 허용이면 자리표시 없이도 행 수로 같은 신호를 얻으므로 번호 규칙으로는 막히지 않음. 감사에 placeholders=N 을 남겨 추적
- z2 결함은 이번 회차에 수정, 대화 창 Markdown 요청 추가

## 남은 상태
- z2 앱 pid 16068(ORANDA_DATA_DIR = b9c3510b scratchpad\z2\data) 띄워 둠, 26ai 시험 표 AIT_ORDERS 유지(사용자 결정). 정리하라고 하면 앱 종료·표 삭제
- 앞 세션 scratchpad(8f437467…\scratchpad\data\connections.json)에 실제 운영 접속 정보가 있고 접속 주석에 비밀번호처럼 보이는 글이 있음 → 사용자에게 알림

## 다음 회차 후속 (WBS.md 이슈 칸 정본)
- 가림: `--+` 줄 힌트는 주석으로 비워짐, TRUE/FALSE·따옴표 별칭 자유 글은 그대로, DATE :vN 앞 키워드 중복
- 흐름: 이어 말하기 IsRunning 검사와 실행 사이 await 경쟁(L2), 재작성 후보 첫 것만 제안(L4), 이어 말하기 입력란 「가리지 않고 보냄」 안내
- 화면: 대화 창 코드 블록 SQL 색 강조(고정폭만 넣음), 제안 비교 창 전에 시작 탭을 활성으로 돌림(IEditorContext 가 활성 탭만 다룸)
- 정리: 안 쓰는 옛 벤치마크 확인란 literals-en 줄, RoleEdgeCaseTests 간헐 실패

## PC 메모(win30)
- oranda\.claude\skills = dmes-standard 쪽 junction. 조정자 스크립트는 실제 경로(C:/Users/dksystems/project/dmes-standard/.claude/skills/coordinator/scripts)로 불러야 함(아니면 isMain 거짓 → 출력 없이 rc 0)
- 레인 대화가 bg 세션으로 이어질 수 있음(continued-in): 같은 이름의 bg 세션이 진짜 레인, 옛 탭은 껍데기 → relink + pid 갱신
- spawn-manual.mjs 화면 확인이 일찍 실패하면 세션 파일로 확인 뒤 term-send-safe 로 첫 지시

## 지표
Claude 환산 비용 $48.43(조정 28%), 결정 21. 변경  46 files changed, 4254 insertions(+), 120 deletions(-)(run-metrics 의 lines=0 은 집계 실패)
