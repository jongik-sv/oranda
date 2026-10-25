# Decisions Log — project

> Append-only audit trail of autonomous decisions made during DDTR/feat/wbs cycles.
> Edit prior entries forbidden — record reversals as new entries instead.

## D-001 (2026-10-22T23:34:39Z)
- **Phase**: design
- **Decision needed**: MyBatis 매퍼를 Oranda 에서 편집·시험·저장하려면 화면과 저장 방식을 어떻게 둘지
- **Decision made**: 시안 A(문장 탭형). 매퍼 xml 이 정본. 문서 탭 하나 = 매퍼 하나, 왼쪽 「문장」 도구 창, 가운데 문장 원문 탭, 오른쪽 「파라미터 · 시나리오」, 아래 「펼친 SQL · 결과 · 조합 검사」. 저장은 바뀐 문장의 원문 범위만 바꿔 쓴다. 시나리오는 앱 데이터에 매퍼 경로 기준으로 둔다. 일반 작업 공간은 문서 탭 메뉴로 바꾸고 원래 파일은 그대로 둔다. 문서는 docs/oranda1 에 따로 두고 상위 PRD 는 고치지 않는다
- **Rationale**: 시안 B(파일 통째 편집형)는 커서로 문장을 정해야 하고 저장 때 파일 전체가 다시 쓰일 위험이 크다. A 는 지금의 작업 공간 구조(문서 탭 → SQL 탭, 탭별 바인드, 문서별 도구 창)를 넓히고 문장 단위 저장과 맞는다
- **Reversible**: yes
- **Source**: 사용자 결정 2026-10-23 「A가 좋아보이네」 「기존 PRD 문서는 놔두고 새로운 폴더 하나 만들어서 진행하자」, 시안 캔버스 https://claude.ai/artifact/JeoMQCGye8RCEqGmGVPKEa

## D-002 (2026-10-25T02:55:41Z)
- **Phase**: design
- **Decision needed**: Role 을 하나만 고르게 할지, 겹쳐 고르게 할지, 기능 단위 조정을 허용할지
- **Decision made**: Role = 기능 묶음 프리셋. 여러 개를 겹쳐 고르고 켜진 기능은 고른 Role 기본값의 합집합이다. 그 위에 기능 단위 직접 켬·직접 끔을 덮어쓴다(끔이 이김). Role 6개(sqlDev·plsqlDev·myBatisDev·tuner·dba·viewer) + 공통. 기능 id 25개 = 조사 3절 매트릭스의 행에서 D3 를 dataExport·dataImport 로 나눈 것. ◎·○ = 기본 켬, △·– = 기본 끔. A4 DB 도구는 1단계에서 나누지 않는다(조사 매트릭스의 부분 표기는 기능 세부 조정으로 대신)
- **Rationale**: 한 사람이 여러 Role 을 겸한다(PL/SQL + 튜너 등). 합집합이면 Role 을 더할수록 기능이 늘기만 해서 예측하기 쉽다. D3 는 조회 전용에서 내보내기만 켜야 해서 나눈다
- **Reversible**: yes
- **Source**: 사용자 확정 2026-10-25(권장안 승인), docs/oranda1/research/role-features.md 2·3·6.1·6.2절

## D-003 (2026-10-25T02:55:41Z)
- **Phase**: design
- **Decision needed**: 1단계 목표를 메뉴 단순화로 둘지 메모리 절감(어셈블리 분리)으로 둘지
- **Decision made**: 1단계 = 메뉴 단순화 + 시작 초기화(시작 작업·상태 칸·타이머·추가 세션) 빼기. 어셈블리 분리는 하지 않는다. 기능을 끈 구성(V1 R1·V2 R3·V3 R4+R5·V4 R6)과 지금(V0)을 tests/Oranda.Perf 계열로 견줘 결과서(docs/oranda1/itest/TSK-07-07.md)만 남긴다. 판단 기준: V0 와 가장 작은 구성의 유휴 전용 메모리 차가 15 MB 이상 또는 10 % 이상이면 「분리 검토」 결론과 후보를 적고 새 WP 로 올린다. 아니면 「분리하지 않음」
- **Rationale**: 모든 기능이 Oranda.App 한 어셈블리이고 처리기·ViewModel 이 지연 생성이라 메뉴에서 빼는 것만으로는 메모리 이득이 작을 것으로 본다(조사 4.3, 추측). 분리는 일이 커서 측정값으로 정한다. 기준값은 macOS 유휴 중앙값 131.6 MB 의 약 10 %
- **Reversible**: yes
- **Source**: 사용자 확정 2026-10-25, 조사 4.3·6.4, docs/perf/macos-2026-10-20.md

## D-004 (2026-10-25T02:55:41Z)
- **Phase**: design
- **Decision needed**: 기능 꼬리표를 어디에 붙일지, 실행 계획(T1)을 어떻게 나눌지
- **Decision made**: 네임스페이스 기반. Oranda.App.Features.<폴더> 접두 → 기능 id 표 + 명령·패널 id 덮어쓰기 + 형 전체 이름 덮어쓰기(TRD 10.2). 특성 281개는 고치지 않는다. 꼬리표는 등록 항목(명령·도구 창·상태 칸·시작 작업)에만 붙고 공용 형(계획 모델·뷰, 모니터 공통, 컴파일 오류 형)은 거르지 않는다. PlsqlWorkspace 네임스페이스는 plsqlEditor 에 매단다(편집 탭이 그 문서 패널 안에 산다). T1 은 예상·실제 계획·계획 창을 공통으로 두고 비교·고정·커서 옵션·그래프 탭·SQL Monitor 보고서 탭만 planAdvanced 로 둔다
- **Rationale**: 특성에 Feature 속성을 더하면 281곳을 고치고 시작 작업·서비스에는 따로 붙여야 한다. 네임스페이스는 이미 기능 폴더와 일치한다. 실행 계획은 Lock·SQL 모니터·튜닝·MyBatis 가 같이 쓰므로(조사 4.1 「다」) 모델·뷰는 공통에 남겨야 한다
- **Reversible**: yes
- **Source**: 사용자 확정 2026-10-25(꼬리표 = 네임스페이스), 조사 4.1·5.2-4·6.8

## D-005 (2026-10-25T02:55:41Z)
- **Phase**: design
- **Decision needed**: 레지스트리가 설정을 읽기 전에 만들어지는(App.axaml.cs:64 vs :65) 문제를 어떻게 풀지, 어디서 거를지
- **Decision made**: 레지스트리 단계에서 거른다(명령의 When 은 쓰지 않는다). 조사 5.2-5 의 (b): 레지스트리가 IFeatureGate 로 거른 스냅숏을 내고, 게이트는 생성 때 전부 켬 → ShellController.LoadAsync 가 설정을 읽은 직후·레이아웃을 짓기 전에 한 번 해석(= 앱 시작 때 값) → Initialize·Attach·시작 작업이 해석된 값을 본다. 실행 중에는 SettingsChanged("features") → 게이트 → CommandsChanged/PanelsChanged·feature.<id> 문맥 키. 시작 때 도는 기능(myBatis·plsqlWorkspace·blockerAlert·changeJournal)은 실제 켬 = 시작 때 켬 ∧ 지금 켬
- **Rationale**: When 은 주 메뉴만 숨긴다(MenuVisibility.cs:20). (a) 조립 전 동기 읽기는 실행 중 바꿈을 못 한다. 시작 때 도는 기능을 실행 중에 켜면 배선 없는 명령이 생기므로 재시작 전까지 막는다
- **Reversible**: yes
- **Source**: 조사 4.2·5.2-5, App.axaml.cs:56-91, ShellController.cs:176-181

## D-006 (2026-10-25T02:55:41Z)
- **Phase**: design
- **Decision needed**: 조회 전용(R6) 실행 차단을 R6 만 고른 경우에 적용할지, R6 이 포함되면 적용할지
- **Decision made**: R6 만 고른 경우(roles 가 정확히 ["viewer"])에만 적용한다. 그때 정책 관문 ExecutionPolicy 에 규칙 ReadOnlyRole(읽기 전용 프로필 바로 뒤, 같은 IsReadOnlySafe 기준)로 질의·트랜잭션 제어·SQL*Plus·EXPLAIN 외 문장을 거부하고, readOnlySession 문맥 키를 참으로 해 쓰기 명령을 흐린다. 쓰기 전용 기능(gridEdit·dataImport·plsqlEditor·plsqlWorkspace·plsqlRun·columnReorder)은 직접 켜도 꺼진 것으로 본다. 다른 Role 과 함께 고르면 차단하지 않고 설정 창이 그 사실을 알린다
- **Rationale**: Role 은 합집합 프리셋이라 Role 을 더하면 할 수 있는 일이 늘기만 해야 일관된다(D-002). R6 + 튜너처럼 고른 사람은 ALTER SESSION·벤치마크 등 쓰기성 동작이 필요하다. Role 은 보안 정책이 아니라 편의이고, 강제 읽기 전용의 정본은 접속 프로필 읽기 전용과 DB 권한이다(조사 6.9). 차단은 같은 정책 관문에서 해서 엔진을 거치는 모든 경로에 걸린다
- **Reversible**: yes
- **Source**: 사용자 확정 2026-10-25(메뉴 숨김 + 실제 차단, 적용 범위는 위임), ExecutionPolicy.cs 1번 규칙, OracleExecutionService.cs:146-153

## D-007 (2026-10-25T02:55:41Z)
- **Phase**: design
- **Decision needed**: PL/SQL 편집기를 끈 상태에서 PL/SQL 단위 「소스 열기」를 어떻게 할지
- **Decision made**: 서버 소스를 일반 SQL 편집기 탭에 읽기 전용으로 연다(제목 「[읽기 전용] OWNER.NAME」). 분기는 SchemaObjectOpener 한 곳에 두고 정의로 이동·객체 검색·변경 일지·oranda.plsql.openSource 처리기가 이를 부른다. oranda.plsql.openSource 는 꼬리표를 공통으로 덮어써 PL/SQL 을 꺼도 메뉴에 남는다
- **Rationale**: 사용자 확정. 숨기면 소스를 볼 길이 없고, 객체 상세(DDL 보기)로 돌리면 분기가 흩어진다. SQL 편집기 탭에 읽기 전용 상태가 이미 있다(EditorContext.cs:51)
- **Reversible**: yes
- **Source**: 사용자 확정 2026-10-25, 조사 4.1 「다」 P1·6.6

## D-008 (2026-10-25T02:55:41Z)
- **Phase**: design
- **Decision needed**: MyBatis 를 끈 상태에서 매퍼(.xml)를 어떻게 열지
- **Decision made**: MyBatis 작업 공간 이전 방식(WorkspaceFiles.ReadMyBatis: 동적 태그 「모두 켬」 펼침, #{x} → :x)으로 문장마다 일반 SQL 작업 공간 탭을 만든다. 저장은 작업 공간(.md)으로만 하고 매퍼 파일에는 쓰지 않는다. 처음 한 번 상태 줄 안내. 루트가 mapper 가 아닌 xml 은 지금과 같다
- **Rationale**: 훅 구조라 배선(MyBatisWorkspaceWiring)을 돌리지 않으면 훅이 MyBatisHooks.Empty 로 남고 기존 펼침 경로로 간다(조사 4.1 「다」 M1). 새 코드 경로를 만들지 않고 시험된 기능을 재사용한다. 원문 파일을 안 건드리므로 안전하다. 조사가 이 경로를 「추측」으로 남겼으므로 TSK-07-06 이 시험으로 확인한다
- **Reversible**: yes
- **Source**: 사용자 확정 2026-10-25(보고서 근거에 맞게), 조사 4.1·6.7, WorkspaceFiles.MyBatis.cs:16-40

## D-009 (2026-10-25T02:55:41Z)
- **Phase**: design
- **Decision needed**: 블로커 알림을 개발자 Role 에서 켤지, Lock 모니터가 꺼졌을 때 칸 클릭을 어떻게 할지
- **Decision made**: R1~R5 에서 기본 켬, R6 은 끔. 미커밋이 있을 때만 모니터 세션을 하나 더 여는 지금 동작은 그대로. lockMonitor 가 꺼져 있으면 칸을 눌렀을 때 Lock 모니터 대신 「Lock 모니터 기능이 꺼져 있습니다. 설정 > 기능에서 켤 수 있습니다.」와 「설정 열기」를 보인다
- **Rationale**: 내 세션이 남을 막는다는 개발자 안전장치다(BlockerWatcher.cs:56-61). 칸을 그대로 두면 눌러도 무반응이 된다(조사 4.1 「다」 A2↔A3)
- **Reversible**: yes
- **Source**: 사용자 확정 2026-10-25, 조사 3절 메모·6.5

## D-010 (2026-10-25T02:55:41Z)
- **Phase**: design
- **Decision needed**: Role 을 바꾼 뒤 무엇을 즉시 반영하고 무엇을 재시작에 맡길지, 꺼진 기능의 레이아웃
- **Decision made**: 메뉴·툴바·우클릭·팔레트·단축키·화면 안 버튼은 즉시. 시작 작업·상태 칸·타이머(myBatis·plsqlWorkspace·blockerAlert·changeJournal)는 「재시작하면 적용됩니다: …」 안내 후 다음 실행. 실행 중 끈 기능의 이미 열린 도구 창·문서는 닫지 않고 다음 실행에 빠진다. 다시 켠 도구 창은 기본 위치로 돌아온다(사용자가 옮긴 자리는 잃음)
- **Rationale**: 사용자 확정(즉시/재시작 구분). 열린 문서를 강제로 닫으면 고친 내용을 잃을 수 있다. 숨긴 도구 창 자리를 기억하려면 레이아웃 저장 형식을 바꿔야 해서 1단계에서는 받아들인다(조사 6.11)
- **Reversible**: yes
- **Source**: 사용자 확정 2026-10-25, 조사 4.1 「가」·6.10·6.11, LayoutPlanner.cs:81-90

## D-011 (2026-10-25T02:55:41Z)
- **Phase**: design
- **Decision needed**: 기본값·첫 실행 안내·설정 창 자리
- **Decision made**: features 섹션이 없거나 roles 가 null 이면 모든 기능 켬(지금과 같음). 첫 실행에 창을 띄워 묻지 않는다. Role 을 고르지 않은 동안 시작 화면에 「Role 고르기…」 한 줄 링크를 보이고 도구 메뉴에 「Role·기능 고르기…」(oranda.tools.roles)를 둔다. Role 선택 UI 는 일반 탭이 아니라 새 「기능」 탭(일반 다음)
- **Rationale**: 기존 사용자의 동작이 바뀌지 않아야 하고, 첫 실행 모달은 접속 전 첫 경험을 막는다. Role 6개 + 기능 25개 + 안내 띠는 일반 탭(SettingsWindow.axaml:75-116)에 넣기에 크다
- **Reversible**: yes
- **Source**: 사용자 확정 2026-10-25(기본값 전부 켬, 첫 실행 안내는 위임), 조사 5.2-2·5.2-3·6.3

## D-012 (2026-10-25T02:55:41Z)
- **Phase**: design
- **Decision needed**: 튜너 Role 과 라이선스 팩 선언을 묶을지, WP-07 공유 파일을 누가 고칠지
- **Decision made**: 묶지 않는다. 튜너를 골라도 ASH·AWR·SQL Monitor 는 지금처럼 프로필 팩 선언(SAFE-07)으로만 열리고, 설정 「기능」 탭에 「Diagnostics·Tuning Pack 보유 선언은 접속 프로필에서 합니다」 한 줄만 보인다. 문구 파일 4종(strings-ko/en·literals-en·CommandTitles)·AppSettings·SettingsStore·AppComposition 의 게이트 등록은 계약 Task(TSK-07-01)가 먼저 고치고, 기능 Task 는 TRD 10.9 편집 지점 표의 자기 파일만 고친다
- **Rationale**: 팩 보유는 라이선스 사실이라 Role 로 자동 선언하면 안 된다(조사 6.12). 문구 파일은 LocalizationTests 때문에 모든 레인이 건드리게 되어 병렬 머지 충돌의 주원인이 된다(dflow-wbs 공유 파일 규칙 C1·C7)
- **Reversible**: yes
- **Source**: 조사 6.12, src/Oranda.Core/Connection/ConnectionProfile.cs:26,65, TRD 10.8·10.9
