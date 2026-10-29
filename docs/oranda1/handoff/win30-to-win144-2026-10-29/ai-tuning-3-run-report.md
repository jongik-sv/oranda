# ai-tuning-3 회차 보고 (2026-10-29 오후, win30)

## 결과
사용자 요청 4건을 마쳤습니다. 로컬 `dev-jji-win144` = `2bb161a`(원격 c4e6ccd 위 커밋 2개,  35 files changed, 1363 insertions(+), 135 deletions(-)). 이 두 커밋은 push 하지 않았습니다.

| 머지 | 커밋 | 내용 |
|---|---|---|
| plan-cols | 484fe40 | 실행 계획 트리 열 너비 끌기·최소 너비·두 번 눌러 기본·plan-columns.json 저장 복원, 트리 접기·펼치기(▸/▾·숨은 단계 수·←/→·모두 펼치기/접기) |
| ui-defaults | 2bb161a | AI 튜닝 기본 켜짐(기능 = 조회 전용 역할 빼고 기본 켬, 드라이버 기본 Claude Code, 운영 기본 Query, 이름 확인은 직접 올릴 때만), 일반 탭 「패널·표 글자(pt)」(ui.panelFontSize) + 편집기·그리드 글꼴 탭 안내 링크, PRD 12.2 갱신 |

## push (회차 중 사용자 지시)
- 원격에 새 커밋 3개(6974d01 스키마 체크리스트, 5c7b64b mapper 재시도, 92a51ec 릴리스 노트)가 있어 앞 두 회차 커밋 12개를 그 위로 rebase(literals-en.json 끝 충돌 양쪽 보존) → main·dev-jji-win144·dev-jji-mac = c4e6ccd ff push
- 확인: 빌드 0/0, Core 742/742, App 필터 828/829(WorkspaceUiTests 1MB 매퍼 1초 시험 = 부하로 2~3초, rebase 전 코드도 실패)

## 확인
- 머지 2건 트리 대조 일치. 메인 체크아웃에 다른 세션(oranda-36·oranda-02 추정)의 미커밋 15파일이 있어 조정자가 stash → ff-only → pop 으로 머지(머지 전 git apply --check 로 얹힘 확인, pop 뒤 줄 대조 동일)
- ui-defaults: Core 742/742, App 4302/4304(2건 격리 재실행 통과), Headless PNG ko·en
- plan-cols: App 4290/4291 → 번역 4줄 추가 뒤 Localization·ExecutionPlan 207/0, Headless PNG

## 사용자 결정
- AI 튜닝: 옵션은 두고 기본을 모두 켬
- 글꼴: 배율(%) 유지 + 「패널·표 글자(pt)」 추가(결정 5)
- 메인 체크아웃 미커밋 변경: stash 후 맞추고 되돌림

## 후속
- 툴바: AI 단추 1개 늘어 1280 창 + 접속 이름이 아주 긴 콤보(상한 460)면 오른쪽 2단추(DB 정보·설정)가 「…」 로 접힘. ToolbarConnectionPlacementTests 첫 실행 창 시험은 콤보 폭 상한−100 으로 완화됨
- 가져온 운영 접속은 AiAccess=null 이라 기본 Query 가 됨(규칙 그대로)
- WorkspaceUiTests 1MB 매퍼 1초 시험이 부하에서 흔들림, RoleEdgeCaseTests·SaveFeedback 공유 상태 흔들림
- push-rebase 로컬 브랜치(= c4e6ccd) 남음, 지워도 됨
- 앞 회차 후속 = ~/.coord/ai-tuning-2/run-report.md

## 지표
METRICS ai-tuning-3 cost=19.56 per1k=- coord=35% lines=0 file=C:\Users\DKSYSTEMS\.coord\ai-tuning-3\metrics.md
