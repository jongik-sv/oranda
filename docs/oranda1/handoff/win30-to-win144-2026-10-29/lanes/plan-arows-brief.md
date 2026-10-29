[조정→plan-arows] 착수 지시 (지시 plan-arows-1) — 회차 ai-tuning-4

이 파일 = 이 레인의 할 일·소유 파일·금지 파일의 정본.
공통 규칙: C:/Users/DKSYSTEMS/.coord/ai-tuning/README.md (앞 회차 규칙 그대로, 레인 표만 이 회차 것으로 읽음). 설계 정본: C:/Users/DKSYSTEMS/.coord/ai-tuning-4/design.md. 충돌하면 이 지시가 우선.

## 레인

- 레인: plan-arows
- 브랜치: plan-arows
- 워크트리: C:/Users/DKSYSTEMS/project/oranda-wt/plan-arows(기준 = dev-jji-win144 최신 커밋 2bb161a, 이미 만들어 둠)
- 조정 세션: oranda-55(SendMessage 주소 oranda-55)
  - 질문·승인 요청·머지 요청 = SendMessage 로 여기에 보냄
  - 사용자 화면에 묻지 않음
- 첫 할 일: 공통 규칙·설계 정본·이 파일을 읽고 정본 메모(C:/Users/DKSYSTEMS/.coord/ai-tuning-4/lanes/plan-arows/memo.md)를 만든 뒤 착수. 착수하면 `[plan-arows→조정] 진행 보고: 지시 plan-arows-1 받음 / 첫 항목 <id>` 한 줄

## 할 일

1. a1 실제 계획에서 A-Rows 가 자동으로 보이게: 「실제 계획 때 늘 힌트 넣기」 기본 켬 + 자동 재실행 경로 점검 (크기 S~M)
2. a2 확인과 머지 요청 (크기 S)

배경(사용자: 「A-Row 가 자꾸 안 나와」 「자동으로 A-Rows 가 보이도록 해줘」)
- 실제 계획(Ctrl+Shift+E)은 새로 실행하지 않고 마지막 실행 커서를 DISPLAY_CURSOR 로 읽는다(`src/Oranda.App/Features/ExecutionPlan/ExecutionPlanCommands.cs`). 서버 statistics_level = TYPICAL(운영 대부분)이면 A-Rows 가 비고, 계획 창이 「A-Rows 없음」 띠를 보인다
- 이미 있는 것: 띠의 「힌트 넣어 다시 실행」, 설정 `execution.gatherPlanStatisticsHint`(「늘 힌트 넣기」, `src/Oranda.Core/Settings/AppSettings.cs:168`, 기본 false) → 켜져 있으면 A-Rows 가 없을 때 /*+ GATHER_PLAN_STATISTICS */ 사본으로 한 번 자동 재실행(`PlanViewModel.Boost.cs:483~489`, `RerunAsync`)

세부
- 기본값 true 로. 이미 저장된 사용자 설정(명시적으로 끈 값)은 존중
- 자동 재실행 경로 점검·보강:
  - SELECT(WITH … SELECT 포함)만 사본 재실행. DML·DDL·PL/SQL 블록은 자동 재실행하지 않고 지금처럼 띠만(실수로 데이터 바꾸는 일 금지). 이미 그렇게 되어 있는지 확인하고 시험으로 고정
  - 한 문장당 한 번만(이미 `_autoRerunTried`), 재실행 중임을 상태 줄에 보임, 취소 가능
  - 재실행은 모든 행을 끝까지 읽는다는 안내를 상태 줄에 둠(이미 있음). 운영 접속에서도 동작(사용자 결정: 자동으로)
  - 바인드 변수가 있는 문장은 처음 실행 때 값을 다시 씀(지금 동작 확인)
- 설정 창에서 이 칸이 어디 있는지 확인. 없거나 찾기 어려우면 「실행」 탭에 「실제 계획 때 A-Rows 가 없으면 힌트 사본으로 자동 다시 실행」 확인란이 보이게(이미 있으면 문구만 분명히)
- 시험: 기본값, 저장값 존중, SELECT 만 자동 재실행·DML 은 안 함, 한 번만. 새 한글 문구는 literals-en.json 영어 짝 같은 커밋
- a2: App.Tests Features.ExecutionPlan·Shell.Settings·Localization + Core.Tests Settings → squash-branch --onto dev-jji-win144 --rebase → 머지 요청

## 소유 파일·금지 파일

- 소유: `src/Oranda.App/Features/ExecutionPlan/**`, `tests/Oranda.App.Tests/Features/ExecutionPlan/**`, `src/Oranda.Core/Settings/AppSettings.cs`(GatherPlanStatisticsHint 기본값 줄만), `src/Oranda.App/Shell/Settings/**`(실행 탭 이 칸만), `tests/Oranda.App.Tests/Shell/Settings/**`, `tests/Oranda.Core.Tests/Settings/**`, `src/Oranda.App/Resources/literals-en.json`·`strings-*.json`(자기 줄 추가만)
- 금지: `src/Oranda.App/Features/Ai/**`(chat-detail 레인 몫), `src/Oranda.App/Features/SqlEditor/AvaloniaEditorDialogs.cs`(oranda-02 세션 몫), `src/Oranda.Oracle/**`, `Oranda.sln`, `Directory.Packages.props`
- 소유 밖을 꼭 고쳐야 하면 고치기 전에 질문

## 의존

- 없음. 즉시 착수. 회차는 time(빠르게) 모드
- 머지는 조정자가 직접 한다(메인 체크아웃에 다른 세션의 미커밋 변경이 있음). 메인 체크아웃에서 git merge 하지 않음. 머지 요청만 보냄
- 사용자가 띄운 앱(pid 36808)은 건드리지 않음

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
  - token 모드: S/M 구현·조사·문서 → 풀 먼저(`node C:/Users/dksystems/project/dmes-standard/.claude/skills/coordinator/scripts/pool.mjs submit --lane plan-arows --id plan-arows-<항목> --size S|M --packet <파일> --cwd C:/Users/DKSYSTEMS/project/oranda-wt/plan-arows`)
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

- 모든 보고 첫 줄 = `[plan-arows→조정] <종류>: 지시 plan-arows-1 / 동시 agent <n>` 꼴
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
- 정본 메모: C:/Users/DKSYSTEMS/.coord/ai-tuning-4/lanes/plan-arows/memo.md. 지금 상태·남은 순서·결정·조정 세션 이름·다음 단계 유지
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
