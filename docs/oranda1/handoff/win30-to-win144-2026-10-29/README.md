# 인계: win30 → win144 (2026-10-29 16:10 무렵)

win30 PC 는 프로세스 감시 프로그램(Genian) 때문에 빌드·시험이 너무 느려 작업을 win144 로 옮긴다. 이 폴더가 인계 정본이다. win30 의 조정 상태(`~/.coord/ai-tuning-*`)는 git 밖이라 따라가지 않으므로 필요한 내용을 여기 옮겨 두었다.

## 브랜치

| 브랜치(원격) | SHA | 내용 |
|---|---|---|
| `main`·`dev-jji-win144`·`dev-jji-mac` | 이 인계 커밋(아래 oranda-02 커밋 3개 + 인계 문서) | 끝나고 시험한 작업 전부(아래 「끝난 것」) |
| `wip/chat-detail` | 041d410 | AI 대화 창·게이트 작업 중(아래 1). 기점 2bb161a |
| `wip/plan-arows` | a4fb575 | A-Rows 자동 작업 중(아래 2). 기점 2bb161a |

- 두 wip 브랜치는 체크포인트 커밋이 섞여 있다(squash 안 함). 이어서 일한 뒤 `dev-jji-win144` 최신 위로 squash·rebase 해서 커밋 1개로 머지한다(ff-only, merge commit 없음)
- dev 맨 위에는 다른 세션(oranda-02)의 커밋 3개가 있다. dc06dc8 비교 창 크기·스크롤·줄 diff, 06d1b27 튜닝 대화 전 입력 막기·접속 저장 뒤 AI 키 다시 계산, b8574c8 바인드 그리드 끌어 고르기. 관련 App 시험 218건 통과(headless). wip 두 브랜치는 이 3개보다 앞(2bb161a)에서 갈라졌다. 대화 창 파일(AiChatView.axaml·AiChatViewModel.cs·AiChatTests·AiCommandsTests·literals-en.json)이 06d1b27 과 겹치니 chat-detail 을 rebase 할 때 주의

## 끝난 것 (dev 에 들어감)

- ai-tuning-2: 운영 DB AI 튜닝(운영 상한 Query, EXPLAIN 기본 켬, 운영 benchmark 문장당 30초), 리터럴 가림(SQL 글·계획 술어 → `:vN`, 제안에서 되돌림, fail-closed), 시작 창 모델 opus·sonnet·haiku, AI 대화 창 Markdown, 재작성 제안이 시작 탭에 들어가게, 인덱스 후보 탭 하나 → `ai-tuning-2-run-report.md`
- ai-tuning-3: AI 튜닝 기본 켜짐(기능·드라이버·운영 Query), 실행 계획 트리 열 너비 조절·저장·접기, 「패널·표 글자(pt)」 설정 → `ai-tuning-3-run-report.md`
- ai-tuning-4 c2: 참조 추출기가 ROLLUP·CUBE·PIVOT·NTH_VALUE 같은 내장 구문을 사용자 함수로 보던 것 수정(970c0a9, wip/chat-detail 안에 있음, 아직 dev 아님)

## 남은 일

### 1. wip/chat-detail (지시서 `lanes/chat-detail-brief.md`, 진행 메모 `lanes/chat-detail-memo.md`, 설계 `ai-tuning-4-design.md`)

- 끝: c2(위), c4 응답 형식 규칙(결론 → 근거 → 제안, 3baeb88), c1 의 도구 카드 사람 말 표 `ToolCardText`(f12bde1, 화면 연결 전)
- 진행 중 c7 「사용자 정의 함수가 든 SQL 을 도구가 거부하지 않게」(041d410, 빌드·시험 안 함)
  - 규칙: 해석·조회 도구는 허용. 실행 도구(benchmark 등)는 `SET TRANSACTION READ ONLY` 안에서 실행(`BenchmarkCase.ReadOnly` → `OracleTuningService.RunBenchmarkAsync`). 실행 경로에서만 ALL_DEPENDENCIES 계층 확인(깊이·객체 상한): 자치 트랜잭션 PRAGMA·UTL_HTTP/UTL_TCP/UTL_SMTP/UTL_FILE/UTL_MAIL/DBMS_PIPE/DBMS_SCHEDULER/DBMS_JOB/DBMS_AQ 의존·상한 초과/확인 실패면 거부(이유 user_function_autonomous / _external / _unverified). NEXTVAL·DB 링크는 계속 거부. 감사에 user_functions=N. 남는 위험 = EXECUTE IMMEDIATE 로 숨긴 호출
  - 할 일: BenchmarkQueriesHandler → BenchmarkCase.ReadOnly 연결, 빌드·기존 시험 복구(sql_unsafe 문구·CheckSqlAsync 시그니처), 단위 시험, 26ai 통합 3건(함수→함수→자치 트랜잭션 / UTL_HTTP 의존 / 평범한 함수 benchmark 허용)
- 남은 항목
  - c1 화면 연결: ChatToolEntry 에 제목·핵심 인자·거부/실패 이유·펼침. 드라이버 사건 `AiToolCall.ArgumentsSummary`·`AiToolResult.Note` 를 화면이 버리고 있음. AiChatMarkdownHeadlessTests 의 `t.Text == "get_cursor_plan"` 단언 갱신
  - c5 Markdown 품질(간격·코드 블록·표·인용·수평선, 가벼운 SQL 강조). 고치기 전 PNG 는 레인이 찍어 둠(win30 워크트리, 따라가지 않음)
  - c6 재작성 제안 「다시 보기」(비교 창을 닫아도 대화 창 카드에서 다시 엶)
  - c3 확인과 머지: 전체 시험 → Headless PNG → opus/high 리뷰(c2·c7 fail-closed) → squash → 머지
- 사용자 요청 원문: 도구 카드가 「어떤 내용인지 모르겠다」, 「AI 대화 창 md 가 허접」, 「너무 주저리 주저리 써서 핵심을 모르겠다」, 「결과 쿼리 다시 보기」, 「도구가 거부하지 않게」

### 2. wip/plan-arows (지시서 `lanes/plan-arows-brief.md`, 메모 `lanes/plan-arows-memo.md`)

- 사용자: Ctrl+Shift+E 로 열어도 A-Rows 가 안 나오고 「힌트 넣어 다시 실행」 안내만 나옴 → 「힌트가 자동으로 켜지면 된다」
- 구현 끝(a4fb575, 시험 한 번도 안 돌림): `execution.gatherPlanStatisticsHint` 기본 켬(저장된 false 존중), 확실한 SELECT·WITH 만 힌트 사본 자동 재실행(FOR UPDATE·DML·PL/SQL·불확실 분류는 띠만), 세션 바쁨이면 다음 Ctrl+Shift+E 에 재시도, 취소 단추, 설정 실행 탭 확인란
- 할 일: 빌드(CA1861 고친 뒤 재빌드 전) → ExecutionPlan·Shell.Settings·Localization·Core Settings 시험 → 영어 짝 → squash·머지

### 3. 제안 비교 창 크기 (사용자: 「AI 튜닝 팝업을 너무 크게 하지 말고 스크롤 바를 넣고 알맞은 크기로」)

- dc06dc8 로 dev 에 들어감: 너비 min(1100, 작업 영역 80%), 높이 min(560, 작업 영역 70%), 최소 640×240, 위 줄 diff 칸 + 아래 전체 글 두 칸 각자 스크롤. 실제 앱 확인은 아직(headless 만). 확인 경로 = 긴 SQL 탭 → 보기 → 확장 시험 패널 → 「읽기」 → 「제안 넣기」, 또는 AI 튜닝 재작성 제안
- 원인: 비교 창이 SizeToContent 라 긴 문장이면 화면보다 커져 「적용」·「거절」 단추가 밖으로 밀림(Enter = 적용, Esc = 거절)

## 사용자 결정 (이어서 지킬 것)

- push 는 사용자 지시 때만. `main`·`dev-jji-win144`·`dev-jji-mac` 는 같은 SHA 로 ff push(강제 금지, 원격이 앞서면 rebase 뒤 확인하고 올림)
- 운영 benchmark 확인란 기본 꺼짐 유지. 리터럴 가림 결정 14(같은 원문 = 같은 번호라서 추측 확인 통로) 위험 수용
- AI 튜닝은 옵션을 두고 기본 켬. 조회 전용(viewer) 역할은 기본에서 뺌
- 대화 창 Markdown 은 새 NuGet 없이 직접 렌더러

## 알려진 문제·후속

- 툴바: AI 단추 1개 늘어 1280 창 + 아주 긴 접속 이름이면 오른쪽 2단추가 「…」 로 접힘
- 흔들리는 시험: WorkspaceUiTests 1MB 매퍼 1초(부하), RoleEdgeCaseTests·SaveFeedback 공유 상태, MapperFolderTests.The_folder_tree…(2bb161a 이전부터 실패, oranda-02 보고)
- 가림 후속: `--+` 줄 힌트, TRUE/FALSE·따옴표 별칭 자유 글, DATE :vN 앞 키워드 중복, 이어 말하기 await 경쟁, 재작성 후보 첫 것만 제안
- 대화 창 코드 블록 SQL 색 강조(c5 에서 다시)
- ORANDA_DATA_DIR 를 따로 줘도 Documents\기본.workspace.md 는 같은 파일을 연다 → 두 번째 앱 인스턴스를 정상 종료하면 사용자 작업 공간 파일을 덮어쓸 수 있음(oranda-02 발견)
- Oracle 통합 시험 = ORANDA_IT_* 사용자 환경 변수가 있어야 함(win144 에서 다시 설정). 26ai 시험 표 AIT_ORDERS 는 win30 의 로컬 컨테이너에만 있음
