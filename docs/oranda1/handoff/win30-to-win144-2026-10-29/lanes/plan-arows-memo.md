# plan-arows 정본 메모 (중단: 다른 PC 로 이동)

## 끝난 것
- a1 구현 전부 wip 커밋(브랜치 plan-arows): 기본 켬(AppSettings), PlanStatsRerunner.CanAutoRerun(SELECT·WITH 만, FOR UPDATE·DML·PL/SQL·불확실 분류 제외), PlanRerunNotStartedException(보내기 전 실패 → 다음 계획 열기에서 재시도), CancelRerun + 띠 「다시 실행 취소」 단추, 설정 창 실행 탭 확인란(Shell.settings.planHint*), 계획 창이 명령 때마다 설정값 재읽기(SetAlwaysHintFromSettings), 문구 ko/en/literals-en 짝
- 시험 작성: PlanBoostTests 추가분, App.Tests/Shell/Settings/PlanHintSettingTests.cs, Core.Tests/Settings/GatherPlanStatisticsHintSettingTests.cs

## 시험 상태
- dotnet build tests/Oranda.App.Tests: 마지막 빌드는 CA1861 1건 오류(시험 파일)였고 그 뒤 static 필드로 고쳤으나 재빌드 안 함. 시험은 한 번도 실행 안 함
- 기존 시험 PlanBoostTests Rerunner_reports_denied… 는 busy/없음 케이스를 PlanRerunNotStartedException 으로 바꿔 둠

## 남은 일
1. dotnet build tests/Oranda.App.Tests, Core.Tests -m:2 후 `--filter FullyQualifiedName~ExecutionPlan`, `~Shell.Settings`, `~Localization`, Core.Tests Settings 실행·수정
2. 상태 줄 문구가 literals-en 검사(Localization 시험)에 걸리면 영어 짝 추가
3. 시험 통과 뒤 dev-jji-win144 합류 → squash-branch --onto dev-jji-win144 --rebase → 머지 요청(a2)
