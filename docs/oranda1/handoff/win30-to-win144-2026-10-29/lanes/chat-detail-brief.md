[조정→chat-detail] 착수 지시 (지시 chat-detail-1) — 회차 ai-tuning-4

이 파일 = 이 레인의 할 일·소유 파일·금지 파일의 정본.
공통 규칙: C:/Users/DKSYSTEMS/.coord/ai-tuning/README.md (앞 회차 규칙 그대로, 레인 표만 이 회차 것으로 읽음). 설계 정본: C:/Users/DKSYSTEMS/.coord/ai-tuning-4/design.md. 충돌하면 이 지시가 우선.

## 레인

- 레인: chat-detail
- 브랜치: chat-detail
- 워크트리: C:/Users/DKSYSTEMS/project/oranda-wt/chat-detail(기준 = dev-jji-win144 최신 커밋 2bb161a, 이미 만들어 둠)
- 조정 세션: oranda-55(SendMessage 주소 oranda-55)
  - 질문·승인 요청·머지 요청 = SendMessage 로 여기에 보냄
  - 사용자 화면에 묻지 않음
- 첫 할 일: 공통 규칙·설계 정본·이 파일을 읽고 정본 메모(C:/Users/DKSYSTEMS/.coord/ai-tuning-4/lanes/chat-detail/memo.md)를 만든 뒤 착수. 착수하면 `[chat-detail→조정] 진행 보고: 지시 chat-detail-1 받음 / 첫 항목 <id>` 한 줄

## 할 일

1. c2 get_tuning_context 가 WITH·빈 주석 문장에서 sql_unsafe 로 거부되는 원인 찾기와 수정 (크기 M)
2. c1 AI 대화 창 도구 카드 자세히: 사람 말 한 줄·핵심 인자·거부/실패 이유·펼침 (크기 M)
3. c3 확인과 머지 요청 (크기 S)

항목 세부 = 설계 정본 C:/Users/DKSYSTEMS/.coord/ai-tuning-4/design.md §1·§2
- c2 를 먼저 한다(튜닝 품질에 직접 영향). 재현 시험 → 원인 → 수정. 원인을 찾으면 고치기 전에 한 줄 보고(원인·수정 방향)
- c1 시험: 도구별 사람 말 표 단위 시험, 거부 이유 표, 펼침 Headless 표시 시험, 원래 값이 화면에 안 나오는 시험(:vN 유지)
- c3: Core.Tests Ai·Language.Tests Ai·App.Tests Features.Ai·Localization + App.Tests 전체 1회(heavy) → Headless PNG(직접 봄) → opus/high 리뷰 1회(c2 의 fail-closed 유지 여부) → squash-branch --onto dev-jji-win144 --rebase → 머지 요청. 새 한글 문구는 literals-en.json 영어 짝 같은 커밋

## 소유 파일·금지 파일

- 소유: `src/Oranda.App/Features/Ai/**`, `tests/Oranda.App.Tests/Features/Ai/**`, `src/Oranda.Core/Ai/**`, `tests/Oranda.Core.Tests/Ai/**`, `src/Oranda.Language/Ai/**`, `tests/Oranda.Language.Tests/Ai/**`, `src/Oranda.Platform/Ai/**`, `tests/Oranda.Platform.Tests/Ai/**`, `src/Oranda.App/Resources/literals-en.json`(자기 줄 추가만)
- 금지: Language 의 Ai 밖(렉서·파서 본체), `src/Oranda.Oracle/**`, `Oranda.sln`, `Directory.Packages.props`
- 소유 밖을 꼭 고쳐야 하면 고치기 전에 질문

## 의존

- 없음. 즉시 착수. 회차는 time(빠르게) 모드
- 머지는 조정자가 직접 한다(메인 체크아웃에 다른 세션의 미커밋 변경이 있음). 메인 체크아웃에서 git merge 하지 않음. 머지 요청만 보냄
- 사용자가 띄운 앱(pid 36808, 워크트리 oranda-wt/run-app)은 건드리지 않음

## 무거운 작업 칸

- 공용 칸 사용
- 무거운 명령(전체 빌드 `dotnet build Oranda.sln`·시험 프로젝트 전체·Oracle 통합 시험·앱 publish) = heavy.mjs 경유
  - `node C:/Users/dksystems/project/dmes-standard/.claude/skills/dflow-dev/scripts/heavy.mjs -- dotnet test <시험 프로젝트> -m:2`
  - 2분 넘을 명령 = `heavy.mjs --detach -- <명령>` 뒤 `heavy.mjs wait <id> --max 240` 반복
  - exit 75 = 슬롯 대기 초과(실패 아님). 다시 부르면 줄 자리 이어받음
  - `--oracle` 옵션 쓰지 않음(이 저장소에 없는 자리 잠금)
- 무거운 명령 = 한 번에 하나만 시작
- dotnet build·test = `-m:2` 로 제한
- 무거운 작업 금지 통지 수신 → 풀릴 때까지 시작 금지, 단순 시험도 미룸

## 작업 방식

시간·토큰·성능 최적. 최소 충분 등급에서 시작.
- 한 칸 올림 = 실패·막힘 근거가 있을 때만(시험 실패·리뷰 지적 반복·20분 넘게 막힘)
- 시작 전에 핵심 수정 크기(파일·줄 수) 기재, 모든 단계를 그 크기에 비례
- 실행 수단 = 항목마다 업무 크기로 고름(기준표 `C:/Users/dksystems/project/dmes-standard/.claude/skills/coordinator/references/sizing.md`)
  - D0 직접 · D1 검색 워커 · D2 agent 하나 · D3 병렬 agent · D4 Workflow · D5 새 세션 · DP 풀(아래 「비-Claude 풀」)
  - 진행 보고에 고른 단계 기재(예: 실행: D2)
- Workflow(D4) = 같은 흐름(구현→리뷰→수정) M/L 항목이 4개 이상일 때만
  - agent 2개 이하로 끝나는 일 → Agent 도구 직접(D2·D3)
  - Workflow 안에서 풀 호출 금지(풀은 레인이 Workflow 밖에서)
  - 먼저 workflow-authoring 스킬 읽기
  - 조회·한 파일 수정 = Workflow 없이 직접
- 모든 agent() 에 model·effort 기재(크기 S/M/L = 항목 표 기준):
  - opus 를 쓰면 근거 한 줄(L·동시성·판정 중 무엇). token 모드 opus = 판정 리뷰·L 구현만
  - 같은 단계 agent = 같은 모델로 함께 띄움. 지시문은 공통 부분 먼저, 항목별 내용 뒤(캐시 재사용)
- 단순 시험 실행·결과 확인·기계적 치환 [*]: haiku / low
- 조사·위치 찾기·영향 범위·사용처 목록 [*]: search
- 문서 갱신 [*]: sonnet / medium
- 구현·수정·특성 테스트 작성 [S/M]: sonnet / medium
- 구현·수정·특성 테스트 작성 [L·동시성·트랜잭션·원인 모를 결함]: sonnet / high
- 리뷰(동작 보존 판정) [S]: sonnet / high
- 리뷰(동작 보존 판정) [M/L]: opus / high
- 보안·트랜잭션 정합성 판정 [*]: opus / xhigh
- 시험 실패 → 실패 사다리: sonnet/medium → sonnet/high → opus/high
  - 시간 초과·부하 연쇄 실패 → load 확인 뒤 같은 등급 1회만 재실행
  - 같은 원인 연속 2회 실패 → 한 칸 건너뜀
  - 사다리 끝 실패 또는 수정 시도 3회 도달 → 멈추고 blocked 보고
- 조사 = 영향에 비례
  - 값 하나·위치 찾기 → Workflow·agent 없이 grep 한 번
  - 조사·사용처 집계 → `node C:/Users/dksystems/project/dmes-standard/.claude/skills/coordinator/scripts/search.mjs "<질의>"`(이 PC 는 agy 없음 → opencode)
  - 검색 워커 실패 → grep 또는 sonnet/medium agent
  - model 이 search 인 단계 = 위 순서대로
- 항목마다 구현 → 리뷰 1회 → 지적 수정 → 수정분만 확인
  - 수정분 확인이 clean 이 아니면 그 항목에 의존하는 항목만 대기(다른 항목은 계속)
  - 보안 리뷰 수정분 확인 = opus 로 1회
  - 몇 줄 수정·리팩토링 이동 단계 = 리뷰는 마지막 1회
- 회차 모드 = token (토큰 우선)
- 병렬 배정(레인 안)
  - 착수 때 항목 표에 항목마다 `선행 항목`·`건드릴 파일` 기재(`C:/Users/dksystems/project/dmes-standard/.claude/skills/coordinator/references/decompose.md` §2.1 형식)
  - 선행이 끝났고 진행 중 항목과 건드릴 파일이 안 겹치는 항목 → 동시 상한까지 한꺼번에 시작
  - 동시 agent = 4개까지(구현·리뷰·수정 합). 사용량 띠 미확인이라 낮게 시작
  - 구현 agent = 자기 항목 파일만 수정, 커밋 금지
  - 컨트롤러 = 그 항목 파일만 `git add <파일…>` 로 커밋
    - 커밋 전 `git status` 로 소유 밖 변경 확인 → 있으면 그 항목 멈추고 보고
  - 구현 끝난 항목 → 바로 커밋·리뷰 시작. 다른 항목 구현은 계속
  - 리뷰가 clean 될 때까지 그 항목 파일 = 그 항목 소유
  - 웨이브(함께 진행한 묶음) 끝 → 컨트롤러가 모듈 시험 1회(heavy.mjs)
- agent 보고 = `task-<번호>-report.md` 파일(워크트리 밖 정본 메모 폴더) + 컨트롤러에게 5줄 이하 요약
- 비-Claude 풀(절차 `C:/Users/dksystems/project/dmes-standard/.claude/skills/coordinator/references/pool.md`)
  - 이 PC 풀 = opencode 만(GLM 없음)
  - token 모드: S/M 구현·조사·문서 → 풀 먼저(`node C:/Users/dksystems/project/dmes-standard/.claude/skills/coordinator/scripts/pool.mjs submit --lane chat-detail --id chat-detail-<항목> --size S|M --packet <파일> --cwd C:/Users/DKSYSTEMS/project/oranda-wt/chat-detail`)
  - 풀 결과 → 완료 조건 시험 1회 → Claude 리뷰
  - 풀 실패·리뷰 지적 → Claude agent 가 처리(풀에 다시 맡기지 않음)
  - 풀 금지: L·동시성·트랜잭션·보안·공유 API·원인 모를 결함
- 컨텍스트 1000000 토큰 넘음 → 조정자의 정본 갱신 요청에 답하고 `/clear` 재개 대기
- 셸 명령 = 짧게 나눔
  - heredoc·sh -c·변수·$(…) 섞은 복합 명령 금지
  - 파일 수정 = Edit·Write 도구
  - 확인 창이 뜨면 Workflow 가 멈춤
- 사람에게 묻는 선택 창(AskUserQuestion) 금지
  - 물을 것 → 조정자에게 「질문: 배경 / 선택지 / 기본안」
  - 기본안으로 계속할 수 있으면 계속
- 무거운 명령 1회 시간 상한 = 모듈당 15분, 전체 빌드 40분
  - 로그 5분 넘게 불변 + CPU 0% → 자기 트리만 TERM, blocked 보고
  - 출력이 긴 명령 = 출력은 파일로, 요약만 읽음

## 시험

- 범위 = 바뀐 것만(바뀐 파일·프로젝트 시험)
- 중간 단계 = 해당 프로젝트 빌드와 바뀐 시험 클래스만(`dotnet test <프로젝트> --filter FullyQualifiedName~<클래스>`)
- 리뷰 지적 수정 뒤 = 수정분 시험만
- 단순 시험(단일 시험 클래스, 한 프로젝트 빌드) = heavy.mjs 없이 세션이 직접
- 머지 요청 직전 1회 = 바뀐 프로젝트 시험 전체(heavy.mjs 경유)
  - 솔루션 전체 시험 = 조정자 지시 때만
- 새 시험 = 바뀐 동작당 1~2개
  - 경계값 전수·변이 시험·스냅샷 대량 추가 금지
- 실패 재실행 = 같은 명령 1회까지
- 내 변경과 무관한 기존 실패 = 고치지 않고 보고만
- 성능 시험 = 시간 상한 부착
- 띄운 백그라운드(시험 입력·앱 실행·감시) = 반드시 정리
- 항목당 시험 실행 시간 합계 15분 초과 → 멈추고 진행 보고(원인·남은 시험)
  - 머지 직전 프로젝트 전체 시험 1회 = 합계에서 제외
- Oracle 통합 시험 = 반드시 래퍼 경유(공통 규칙 §2): `pwsh -NoProfile -File C:/Users/DKSYSTEMS/.coord/ai-tuning/it-env.ps1 dotnet test tests/Oranda.Oracle.IntegrationTests -m:2 --filter <필터>`. 「건너뜀」 줄이 나오면 통과로 세지 않음

## 보고 방식

- 모든 보고 첫 줄 = `[chat-detail→조정] <종류>: 지시 chat-detail-1 / 동시 agent <n>` 꼴
- 보고 = SendMessage 로 `oranda-55` 에 보냄. 사용자 화면에 묻지 않음
- 보고 문체 = 한국어 뼈대 + tech term·identifier 영어 원문, 조사·인사·군말 생략, 명사로 끝냄. 숫자·부정·추정 여부 유지
  - 위 고정 문구(`진행 보고:`·`머지 요청:`·`머지 완료:`·`정리 완료:`)는 바꾸지 않음
- 상태 변화(항목 완료·막힘·대기) = 화면에 두지 않고 진행 보고로
- 항목 하나가 끝날 때마다: `진행 보고: 항목 id / 체크포인트 커밋(선택) / 시험 결과(명령과 통과·실패 수) / 진도율 N%(끝난 항목/전체) / 다음 항목`
  - 항목 id = 아래 「할 일」 의 id(예: c1). 조정자가 이 id 로 완료를 기록
  - 작업 중 commit = 체크포인트(자유). 통합 브랜치 모양 = 머지당 commit 1개, merge commit 없음(선형, 머지 = `git merge --ff-only`)
- 머지 직전 순서
  1. dev-jji-win144 최신을 브랜치에 합치고 충돌·시험 확인
  2. `node C:/Users/dksystems/project/dmes-standard/.claude/skills/_shared/node/squash-branch.mjs --onto dev-jji-win144 --subject "<type(scope): 머지 전체 요약>" --rebase` → `SQUASH_OK`·`SQUASH_ALREADY_ONE`·`SQUASH_REBASED` 확인(결과 = dev-jji-win144 tip + commit 1개)
     - `SQUASH_REBASE_CONFLICT` → 원래 상태로 복원됨. dev-jji-win144 를 브랜치에 합쳐 충돌을 풀고 시험 뒤 다시 실행
  3. 합친 commit 의 SHA 를 머지 요청에 적음. 합친 뒤 SHA 를 바꾸는 commit·합류 금지
- 머지 직전: `머지 요청: 세션 이름 / 원본 브랜치 / 대상 dev-jji-win144 / 커밋 수(1)·변경 요약 / 겹칠 수 있는 파일·모듈 / 머지 전 시험 결과(명령과 통과·실패 수) / 머지할 SHA(합친 커밋 1개, 7~12자)`
  - 커밋 수 ≠ 1 → 조정자가 `NOT_SQUASHED`, 기점 ≠ dev-jji-win144 tip → `NOT_REBASED` 로 되돌림
  - 요청 뒤 쌓인 다음 항목 커밋 = 다음 머지 몫. 이번 머지 = 그 SHA 까지만
  - 진행 보고·머지 요청·정리 완료 보고에 「남은 백그라운드 0」 병기
- 「머지 허가」 수신 뒤에만 머지
  - 머지 = 메인 저장소에서 `git -C C:/Users/DKSYSTEMS/project/oranda merge --ff-only <SHA>`. 그 사이 dev-jji-win144 가 앞서 가 실패하면 위 2번을 다시 하고 머지 요청 재전송
  - 머지 뒤 `머지 완료: 커밋 / 트리 / 머지 뒤 빌드·시험 결과(충돌·실패도 그대로)`
  - 마지막 머지 뒤만 정리 → `정리 완료: 지운 워크트리·브랜치 / 남긴 것과 이유`. 중간 머지 뒤에는 워크트리 유지
  - 「대기: 사유」 수신 → 병목이면 아래 병목 대기대로, 수정·첨부 요청이면 그 일 먼저
- 레인은 push 하지 않음
- 정본 메모: C:/Users/DKSYSTEMS/.coord/ai-tuning-4/lanes/chat-detail/memo.md. 지금 상태·남은 순서·결정·조정 세션 이름·다음 단계 유지
  - 「정본 갱신 요청」 수신 → 갱신 후 `정본 갱신 완료: 경로 / 남은 일 3줄` 답장
  - compact 뒤 「재개 확인」 수신 → 정본을 읽고 남은 일 3줄 답장
  - `/clear` 뒤 「재개:」 수신 → 문구가 가리키는 착수 지시 원문(이 파일)과 정본을 읽고 이어서 진행
- 병목 대기(다른 레인 머지·의존물·Oracle·heavy 슬롯·사용자 결정) → 무작정 기다리지 않음
  - 아래에서 스스로 찾아 진행, 진행 보고에 기재
  - (a) 범위 안 의존 없는 다음 항목
  - (b) dev-jji-win144 미리 합치고 컴파일까지 확인
  - (c) 병목이 풀린 뒤 할 단계 준비(리허설·스크립트·머지 요청 초안)
  - (d) 문서·후속 목록 정리
  - 넷 다 없을 때만 「대기」 보고
  - 다른 레인 소유 파일 수정 금지
  - 머지 요청 뒤 요청 SHA 를 바꾸는 합류 금지
  - 병목 레인을 돕는 일 = 조정자가 배정

## 추가 항목 (지시 chat-detail-2, 사용자 요청 2026-10-29 15:20 무렵)

4. c4 AI 응답을 짧고 핵심부터 (크기 S)
- 사용자: 「너무 주저리 주저리 글을 쓴 것 같아. 읽어보면 핵심이 뭔지 잘 모르겠어」
- 프롬프트(src/Oranda.App/Features/Ai/Tuning/AiTuningFlow.cs 의 요청 글)에 응답 형식을 정한다. 기본안:
  - 맨 앞 「결론」 1~2줄(병목이 무엇이고 무엇을 바꾸면 얼마나 좋아지는지)
  - 「근거」 글머리 3~5개(계획·통계 숫자 인용)
  - 「제안」 = 재작성·인덱스 각각 한 줄 설명(본문 SQL 은 제안 도구로만)
  - 전체 한국어, 군더더기 머리말·맺음말 금지, 같은 말 반복 금지, 표는 비교가 필요할 때만
- 이어 말하기 응답도 같은 규칙. 시험 = 프롬프트에 규칙이 들어가는지 단위 시험
5. c5 대화 창 Markdown 표시 품질 (크기 M)
- 사용자: 「AI 대화 창에 md 너무 허접한 것 같아」
- 지금 MarkdownView(Features/Ai/Chat)의 모양을 다듬는다: 제목 크기·굵기와 위아래 간격, 문단·목록 간격과 줄 높이, 글머리 기호·번호 정렬과 들여쓰기, 인라인 코드 배경·안쪽 여백, 코드 블록 배경·테두리·안쪽 여백·모서리, 표 테두리·머리글 배경·칸 여백·줄무늬, 인용(>)과 수평선(---) 지원, 링크 모양, 굵게 색. 밝음·어두움 테마 모두
- 코드 블록 SQL 강조(앞 회차 보류)는 가벼운 방법(렉서 토큰 색만, 스트리밍 중엔 일반 글 → 끝난 뒤 한 번 색칠)이면 넣는다
- Headless PNG 를 고치기 전·후로 찍어 비교해 보고한다(직접 봄)
6. c6 재작성 제안 「다시 보기」 (크기 S~M)
- 사용자: 「결과 쿼리 다시 보기도 있으면 좋겠어. 잘못 눌러서 닫았는데 다시 보려고 해도 내용이 없어서 다시 실행해야 하거든」
- 제안 비교 창을 거절·닫기한 뒤에도 대화 창에 그 제안 카드(라벨·한 줄 요약)를 남기고, 「다시 보기」 를 누르면 같은 비교 창을 다시 연다(되돌린 원래 값 그대로, 시작 탭 기준). 적용한 제안은 「적용함」 표시
- 인덱스 후보는 이미 탭으로 남으니 대상 아님
- 대화가 끝난 뒤에도 그 대화 창이 열려 있는 동안은 다시 볼 수 있게. 앱 재시작 뒤 유지는 이번엔 안 함(기본안)

순서·충돌 주의
- oranda-02 세션(메인 체크아웃 미커밋 15파일)이 AiChatView.axaml·AiChatViewModel.cs·AiCommandsTests·AiChatTests·literals-en.json 을 고친 상태이고 곧 커밋한다. 그 커밋이 dev 에 들어왔다는 내 알림 전에는 이 파일들을 크게 바꾸지 말고, 알림 뒤 합친 다음 c1·c5·c6 의 대화 창 부분을 한다. 그 전에는 c4(프롬프트)·c1 의 사람 말 표(순수 C#)·c5 의 MarkdownView 를 먼저 한다
- c3(머지 요청)은 c1·c4·c5·c6 를 모두 넣어 한 번에

## 추가 항목 (지시 chat-detail-3, 사용자 요청 15:40 무렵, 우선)

7. c7 사용자 정의 함수가 든 SQL 을 도구가 거부하지 않게 (크기 M, c1·c5·c6 보다 먼저)
- 사용자 캡처: AI 가 「SQL 전체는 사용자 정의 함수 때문에 도구가 거부했습니다」 → get_tuning_context 거부 → 계획·벤치마크 못 함. 사용자: 「도구가 거부하지 않게 해줘」
- 지금 규칙: AgentGate.CheckSqlAsync 의 CallsUserFunction(참조 추출기가 사용자 함수를 찾으면 sql_unsafe). c2 는 내장 낱말을 넓혔을 뿐, 진짜 사용자 함수(패키지 함수 pkg.f 포함)는 여전히 거부
- 바꿀 규칙(조정자 결정):
  - 실행하지 않는 도구(get_tuning_context·explain_plan·get_cursor_plan·get_index_info·get_table_stats 등 해석·조회만 하는 것)는 사용자 함수·패키지 함수가 있어도 허용. 단 get_tuning_context 가 안에서 문장을 실행하는 경로가 있으면 그 부분은 아래 실행 규칙을 따름
  - 실행하는 도구(benchmark_queries·run_query·compare_results)도 허용하되 실행을 읽기 전용 트랜잭션(SET TRANSACTION READ ONLY, 끝나면 COMMIT/ROLLBACK 으로 정리) 안에서 함. 함수 안 DML 은 Oracle 이 막음(ORA-01456 류 → 실패 이유로 보고)
  - PRAGMA AUTONOMOUS_TRANSACTION 이 든 함수(ALL_SOURCE 등으로 확인, 확인 못 하면 거부 = fail-closed)만 실행 도구에서 계속 거부. 이유 코드는 새로(예 user_function_autonomous)
  - 시퀀스 NEXTVAL·DB 링크(@)는 지금처럼 거부
  - 감사 요약에 사용자 함수가 있었음을 표시(예 user_functions=N)
- 시험: 사용자 함수 있는 SELECT 로 explain_plan·get_tuning_context 허용, benchmark 는 READ ONLY 안에서 실행(가짜 세션으로 순서 확인), 자치 트랜잭션 함수 거부, 확인 실패 거부, NEXTVAL·@링크 거부 유지. 26ai 통합 시험 1개(사용자 함수 만들어 explain·benchmark, 함수 안 INSERT 는 막히는지)
- 보안 판단이 바뀌는 항목이라 c3 의 opus/high 리뷰 대상에 넣음
