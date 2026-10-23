# 설계 - Oranda MyBatis 작업 공간

> version: 1.0 (TSK-01-01)
> date: 2026-10-23
> 입력: `docs/oranda1/PRD.md`·`TRD.md`·`decisions.md`(D-001 시안 A)·`mockups/`(Main = 시안 A, Convert·SaveDiff·Combos = 흐름)
> 조사 기준: dev-jji-mac 8c19d3b. 상위 `docs/PRD.md`·`docs/TRD.md`·`docs/design/` 은 고치지 않는다(예외: TSK-01-02 의 키맵 4개·`commands.md` MyBatis 줄, 2026-10-23 사용자 결정 Q1).
> 이 문서가 정하는 것: 화면 구성, 문서 종류, 도구 창 보임 규칙, 명령·단축키·문맥 키, 저장 흐름, 시나리오 위치, ReadMyBatis 이관 범위, 공유 지점과 확장 지점. 인터페이스의 정확한 시그니처는 TSK-01-02 가 코드로 동결한다(이 문서 9장은 이름·위치·역할까지).

## 1. 화면 구성 (시안 A — 문장 탭형)

문서 탭 하나 = 매퍼 하나 = SQL 작업 공간 하나(`SqlWorkspaceViewModel`, Dock 문서 정의 ID `oranda.panel.sqlEditor`). 문장 하나 = 그 작업 공간의 안쪽 탭 하나(`SqlEditorViewModel`). 지금 작업 공간 구조(문서 탭 → SQL 탭, 탭별 접속·바인드, 문서별 도구 창)를 그대로 넓힌다(D-001).

| 영역 (시안 Main) | 구성 | 소유 Task | PRD |
|------------------|------|-----------|-----|
| 주 도구 막대 | 지금 그대로(접속 콤보 포함). MyBatis 전용 단추는 넣지 않는다(4.4) | - | - |
| 문서 도구 막대(문서 탭 아래 한 줄) | 아이콘 단추: 시나리오 값으로 실행(F9) · 실행 계획 · 조합 검사 · xml 에 저장(Ctrl+S) · 펼친 SQL 복사(▾ 값 넣은 글). 툴팁에 단축키. MyBatis 문서에서만 보인다. 오른쪽 끝에 「펼친 SQL」 분할 단추(세로 · 가로 | 보이기 토글, 켜진 쪽 표시) | 자리 = 03-01, 단추별 명령 = 표 4.1, 분할 단추 = 03-10 | MB-05·06·07 |
| 문서 탭 머리 | `EmpMapper` + 「MyBatis」 표시 + 저장 안 함 점. 지금 `DisplayTitle`(「* 이름 (MyBatis)」) 규칙을 그대로 쓴다 | 03-01(표시만) | MB-03 |
| 왼쪽 「매퍼 쿼리」 도구 창(구 「문장」, 공용 격자 양식 03-08: 종류·id·수정·조합 오류·쓰는 곳 열) | 매퍼 경로·namespace, 묶음(select·insert·update·delete·sql 조각·resultMap), 항목 표시(● 수정·새 문장·조합 오류 n·「n곳에서 씀」·「(그대로 둠)」), 「+ 새 문장」, 우클릭 메뉴, 아래 「로그 붙여 넣기」 안내 띠 | 창 본체 03-01, 조합 오류 배지 03-04, 폴더 트리 모드 03-07 | MB-03·07·09·10 |
| 가운데 문장 탭 줄 | 안쪽 탭 줄 재사용. 탭 머리 = 종류 표시(SEL·INS·UPD·DEL·SQL) + id + 수정 점 | 03-01 | MB-03 |
| 문장 머리 줄 | 「EmpMapper.xml › selectEmpList」 · 동적 태그 n · 켜진 조건 n/m · 「흐린 줄 = 이번 값으로 빠지는 부분」 | 머리 줄 03-01, 켜진 조건 수 03-02 | MB-03·05 |
| 쿼리 탭 안 분할(TSK-03-10) | 원문 편집기 \| 펼친 SQL. 세로 분할(펼친 SQL 이 오른쪽) 기본, 가로 분할(아래)·숨김으로 바꾼다. 방향·보임·편집기 비율(0.15~0.85)은 설정 `MyBatis.ExpandedHorizontal`·`ShowExpanded`·`ExpandedRatio` 에 기억. 펼친 SQL 은 그 탭의 문장만 보인다 — 「매퍼 쿼리」 창에서 고르기만 하고 탭을 열지 않으면 없다. 분할 머리 줄 = 시나리오 콤보(추가·이름 바꾸기·복제·지우기) + 실행 계획·복사(그대로 / 값 넣은 글), 아래 한 줄 = 설명 · 켜진 조건 n/m. 바인드·치환 입력 목록은 없다(값은 「바인드 변수」 창) | 03-10 (`Editor/MyBatisExpandedSplit.cs`, `Expanded/**`) | MB-04·05 |
| 문장 원문 편집기 | 그 문장 요소의 원문(여는 태그 ~ 닫는 태그). XML·`#{}`·`${}` 강조 + SQL 구간 기존 강조, 흐린 블록·여백 켬/끔 점·물결 밑줄, 빠른 고침 띠 | 강조 03-01, 계산 꾸밈 03-02, 자동 고침 03-04 | MB-03·05·07 |
| 오른쪽 「바인드 변수」 도구 창(기존) | MyBatis 문서에서는 문장 파라미터 전부가 행(이름·값·형식, 빈 칸 = null, `''` = 빈 글자, 목록 = `['A', 'B']`). 시나리오 고르기·추가·이름 바꾸기·복제·지우기는 「펼친 SQL」 분할 머리 줄(2026-10-23 사용자 결정: 별도 「파라미터 · 시나리오」 창 없앰). `${}` 치환 값도 이 창의 `$이름` 행에서 넣는다(03-10) | 03-02 | MB-04 |
| 아래 패널 | 「결과」(기존) · 「조합 검사」 · 「메시지」(기존). 「펼친 SQL」 아래 패널은 없앴다(TSK-03-10, 2026-10-23 사용자 요청 「펼친 SQL 이 조회결과를 덮어서 불편」 → 쿼리 탭 안 분할) | 조합 검사 03-04 | MB-05·07 |
| 상태 표시줄 | 매퍼 파일 · 저장 안 한 문장 n개 · 파일 감시 중 · 인코딩·줄 끝 | 03-03(구간 1개, `[OrandaStatusBarItem]`) | MB-06 |
| 흐름 화면 | 바꾸기 대화 상자(Convert) · 저장 확인 「바뀌는 부분」(SaveDiff) · 밖에서 바뀜 알림 띠 | 03-05 · 03-03 · 03-03 | MB-08·06 |

UI 기준: 아이콘 도구 막대(툴팁 필수), 촘촘한 목록, 우클릭 메뉴, 구획된 상태 표시줄. 글자만 있는 큰 단추와 큰 텍스트 상자를 쓰지 않는다. 기존 `.toolbar`·`.tool` 단추 스타일과 아이콘 리소스, 테마 리소스 키(색 값 직접 쓰기 금지)를 쓴다. 다크 테마도 같은 수준으로 맞춘다.

## 2. 문서 종류 `MyBatis`

### 2.1 판정

- `SqlWorkspaceViewModel` 에 문서 종류 `DocumentKind`(`Sql` | `MyBatis`)를 더한다. 판정 = 작업 공간에 MyBatis 세션(`IMyBatisWorkspaceSession`, 9장)이 붙어 있으면 `MyBatis`.
- 세션은 03-01 의 열기 훅만 붙인다. 그래서 TSK-01-02 머지 뒤에도 지금의 「.xml → 읽기 전용 SQL 펼침」 작업 공간(원본 표시 `Kind` = 「MyBatis」 글자)은 문서 종류가 `Sql` 이고 동작이 바뀌지 않는다.
- 원본 표시 `Kind`(md·sqd·MyBatis, `WorkspaceFiles.KindOf`)는 문서 탭 머리 글자로만 남긴다. 도구 창·명령·문맥 키는 `DocumentKind` 만 본다.
- 종류가 바뀌면(세션이 붙거나 떨어짐) `SqlWorkspaceViewModel` 이 `DocumentKind` 변경을 알린다. 같은 Dock 문서 안의 변화라 Dock 활성 문서 이벤트가 오지 않으므로, 이 알림이 도구 창 보임 규칙의 「전환 신호」다(3.3).

### 2.2 문장 탭

- 문장 탭 = `SqlEditorViewModel`. `Text` = 그 문장 요소의 원문(여는 태그 ~ 닫는 태그), `Title` = 문장 id, `FilePath` = null.
  - `FilePath` 를 매퍼 경로로 두지 않는다: 복원(`Restore`)이 FilePath 가 있고 Content 가 없는 탭을 파일 전체로 다시 읽기 때문이다.
- 문장 ↔ 탭 대응, 연 때 원문·해시, 고친 문장 목록은 세션이 들고 있다. 탭의 `IsDirty` 는 「이 문장이 연 때 원문과 다름」이다.
- 안쪽 탭 줄의 「+」(새 SQL 탭)는 MyBatis 문서에서 「+ 새 문장」(`oranda.mybatis.newStatement`)으로 바뀐다(03-01 이 탭 머리 자리로 처리).
- 문장 탭을 닫아도 고침은 세션에 남는다(탭 닫기 저장 확인을 묻지 않는다). 그래서 「저장 안 함」은 탭이 아니라 **작업 공간·세션 기준**으로 본다:
  - `SqlWorkspaceViewModel.IsDirty` = 고친 탭이 있음 **또는** 세션 dirty(문서 탭 머리 점·닫기 확인이 이것을 본다).
  - 작업 공간 닫기는 탭 수와 상관없이 세션 dirty 면 저장 처리기의 닫기 확인(`IMyBatisSaveHandler.ConfirmCloseAsync`, 03-03)을 부른다(`TryCloseWorkspaceAsync` 의 MyBatis 분기, 01-02). 지금 `CanCloseAsync` 는 탭이 0개면 가드를 부르지 않으므로 이 분기가 따로 필요하다.
  - Ctrl+S·모두 저장은 활성 탭이 아니라 활성·열린 작업 공간의 세션을 본다(문장 탭을 모두 닫아도 저장된다).
  - 자동 저장은 탭이 0개여도 세션 dirty 면 남긴다(지금은 탭 0개 자동 저장을 지운다).
- MyBatis 작업 공간에는 문장 탭만 둔다. 문장이 아닌 탭이 들어오는 경로(Ctrl+N·새 SQL·탭 복제·닫은 탭 다시 열기·스키마 브라우저 삽입·변경 일지·탐색·Flashback·PL/SQL 보내기·전용 세션 탭·03-01 의 「펼친 SQL 을 새 SQL 탭으로」)는 활성 작업 공간이 MyBatis 면 **일반 작업 공간으로 보낸다**(마지막으로 쓴 일반 작업 공간, 없으면 새로 만듦). 분기는 `SqlEditorHost.Tabs.cs` 의 탭 넣기 한 곳(01-02).
- 문장 탭은 닫은 탭 기록(다시 열기)에 넣지 않는다. 탭 이름 바꾸기(F2·`file.renameTab`·탭 우클릭)는 MyBatis 문장 탭에서 꺼진다. id 바꾸기는 `oranda.mybatis.renameStatement` 만 쓴다.

### 2.3 편집기 포커스 자리

- 문장 편집기는 기존 `SqlEditorView` 를 감싼다(03-01 의 `MyBatisStatementEditorView`). 감싸는 쪽이 `<se:SqlEditorView keymaps:FocusSlot.Name="mybatisText" />` 처럼 인스턴스 속성으로 자리를 덮어쓴다(지금 `PlsqlEditorView` 가 `plsql` 로 덮어쓰는 방식과 같다. 인스턴스에 단 값이 View 자기 XAML 의 `sqlText` 보다 나중에 들어간다).
- 새 포커스 자리 `mybatisText`. 포커스 키(4.3)는 `mybatisDocFocus` = [mybatisText], `editorFocus` = [sqlText, plsql, **mybatisText**]. `sqlEditorFocus`·`sqlDocFocus` 에는 넣지 않는다.
  - 그래서 편집 명령(되돌리기·복사·찾기·주석)은 문장 편집기에서 그대로 되고, `sqlDocFocus` 단축키(F9 runForEdit·Ctrl+Enter 문장 실행·F5 스크립트 등)는 문장 편집기에서 잡히지 않는다.

## 3. 도구 창 보임 규칙

### 3.1 새 도구 창·패널 3개

| ID | 제목(ko / en) | 위치 | Order | 소유(내용) |
|----|---------------|------|-------|------------|
| `oranda.panel.mybatisStatements` | 매퍼 쿼리 / Mapper Queries | Left | 30 | 03-01 |
| ~~`oranda.panel.mybatisParameters`~~ | (없앰, 2026-10-23 사용자 결정 — 「바인드 변수」 창이 대신) | - | - | 03-02 |
| ~~`oranda.panel.mybatisExpanded`~~ | (없앰, TSK-03-10 — 쿼리 탭 안 분할로 옮김. 저장된 배치의 이 ID 는 `LayoutPlanner.Reconcile` 이 버린다) | - | - | 03-10 |
| `oranda.panel.mybatisCombos` | 조합 검사 / Combinations | Bottom | 7 | 03-04 |

- 등록 = `[OrandaToolPanel]`(자동 수집). TSK-01-02 가 ViewModel·View 자리(빈 화면, 안내 한 줄)를 만들고, 내용은 위 Task 가 같은 파일을 채운다(편집 지점 표 10장).
- 아래 패널 순서는 「결과 · 조합 검사 · 메시지」다(펼친 SQL 은 03-10 부터 탭 안). 지금 Order 는 결과 5·메시지 8·출력 10·실행 계획 30 이라 조합 검사 7 로 둔다. 왼쪽은 스키마 브라우저 10·스크립트 20 뒤에 문장 30.

### 3.2 보임 표

| 도구 창 | 활성 문서 없음(시작 화면) | 일반 SQL 문서 | MyBatis 문서 | PL/SQL 편집기 | 그 밖 문서(객체 상세·모니터 등) |
|---------|---------------------------|---------------|--------------|---------------|-------------------------------|
| MyBatis 3개 | 숨김 | 숨김 | 보임 | 숨김 | 숨김 |
| 결과·메시지·출력·실행 계획·행 보기·실행 이력·변경 일지 | 지금 상태 유지 | 보임 | 보임 | 보임 | 숨김 |
| 개요·템플릿·컴파일 오류·확장 시험(바인드 변수는 MyBatis 문서에서도 보임, 03-02) | 지금 상태 유지 | 보임 | **숨김** | 보임 | 숨김 |
| 표에 없는 창(스키마 브라우저·스크립트 등) | 보임 | 보임 | 보임 | 보임 | 보임 |

- MyBatis 문서는 정의 ID 가 `oranda.panel.sqlEditor` 라 지금 규칙으로는 SQL 전용 창이 모두 보인다. 시안 Main 의 아래 패널은 결과·메시지를 쓰므로 그 묶음은 보이고, 바인드 변수도 보인다(문장 파라미터를 행으로 보임, 03-02). 개요(xml 문장에는 맞지 않음)·템플릿·컴파일 오류·확장 시험은 숨긴다.
- 「활성 문서 없음」에서 MyBatis 3개는 늘 숨긴다(지금 규칙의 「null 이면 그대로」를 MyBatis 창에는 쓰지 않는다). 시작 화면에 빈 MyBatis 창이 새지 않게 하기 위해서다(위험 R11).
- 숨김은 지금처럼 배치를 다시 짓지 않고 묶음에서 잠시 빼며, layout.json 에는 원래 자리로 저장한다(`Capture`).
- 사용자가 보기 메뉴로 직접 연 창(`ShowTool`)과 기능이 직접 앞으로 가져온 창(`ActivateTool`)은 지금 규칙대로 그 문서에 있는 동안 보인다.

### 3.3 구현 방향 (TSK-01-02 B5)

- 판정을 문서 ID 하나에서 문서로 넓힌다: 새 `IsToolShownFor(toolId, PanelDocument? doc)`. 문서의 Panel 이 Shell 인터페이스 `IToolScopeDocument`(`ToolScope` 글자 `"mybatis"`, `ToolScopeChanged` 사건)를 구현하면 그 값을 함께 본다. Shell 은 Features 를 모르므로 인터페이스는 `Shell/Docking` 에 둔다. 지금 시험이 쓰는 문자열 판정 `IsToolShownFor(toolId, documentId)` 는 남긴다.
- 지금은 「SQL 전용 전부 숨김 / 기록 전부 되보임」 두 갈래뿐이다. MyBatis 문서 ↔ 일반 SQL 문서 전환에는 일부만 되넣고 일부만 숨기는 차집합이 필요하므로, 목표 숨김 집합을 계산해 **빠질 창만 숨기고 들어올 창만 되넣는다**. 숨김 기록(`ScopedHide`)·`Capture`(숨긴 창을 원래 자리로 저장)는 그대로 쓰고, 비율 기록은 묶음 단위로 지우고 되돌린다.
- 활성 문서 null 경로: 갱신을 건너뛰지 않고 MyBatis 3개만 숨긴다(SQL 전용 창은 지금처럼 그대로). 배치 적용 직후(`EndScopedApply`)도 같은 판정으로 다시 숨긴다(시작 화면).
- 전환 신호: 활성 문서의 Panel 이 `IToolScopeDocument` 면 `ToolScopeChanged` 를 구독하고(문서가 바뀌면 옛 구독 해제), 신호가 오면 `RefreshScopedTools()`(같은 문서를 다시 판정하도록 `_scopedDocument` 를 비우고 `ScheduleScopedUpdate`). `SqlWorkspaceViewModel` 은 `MyBatis` 세션이 붙거나 떨어질 때 이 사건을 낸다. 작업 공간 사이 전환은 Dock 문서 전환이라 지금 신호(`ActiveDocumentChanged`)로 충분하다(A1 조사 결과: 초안 §2.2 B5 의 「전환 신호 없음」은 같은 문서 안 종류 변화에만 해당).

### 3.4 배치(layout.json)

- 레이아웃 이주는 필요 없다. `LayoutPlanner.Reconcile` 이 저장된 배치에 없는 새 도구 창을 기본 위치 묶음에 더한다(문장 → 왼쪽 묶음, 조합 검사 → 아래 묶음). 등록이 없어진 ID(파라미터·펼친 SQL)는 버린다.
- 새로 만드는 기본 배치(`CreateDefault`)는 Right 창을 기본으로 숨긴다(바인드 변수·템플릿만 연다). MyBatis 전용 오른쪽 창은 없다(파라미터 창을 없앰, 03-02). 이주 표지(`rightToolsOpen`)는 새로 만들지 않는다.
- 숨김에서 되돌아올 때 그 창은 묶음의 활성 탭이 되지 않는다(숨기기 직전에 활성이었을 때만). 그래서 03-01 은 매퍼를 처음 열 때 `ActivateTool` 로 「문장」·「바인드 변수」를 앞으로 가져온다.

## 4. 명령 · 단축키 · 문맥 키

### 4.1 전체 명령 목록

TRD 6 의 7개에 기능 Task 가 더 필요로 하는 명령을 모두 넣었다(위험 R2). TSK-01-02 가 아래 전부를 빈 처리기·제목(ko·en)으로 먼저 등록하고 동결한다. 기능 Task 는 처리기 파일의 몸만 채우고, 특성 문자열·제목 파일·키맵은 고치지 않는다.

| ID | 제목(ko) | 범주 | Enablement | 자리(Placements) | 키 | 처리기 파일(소유) |
|----|----------|------|------------|------------------|-----|-------------------|
| `oranda.mybatis.newStatement` | 새 쿼리… | Edit | `mybatisDocActive` | `context:mybatisStatements/new@10`, 문장 창 「+」·탭 줄 「+」 | - | `Statements/MyBatisStatementCommands.cs` (03-01) |
| `oranda.mybatis.renameStatement` | 쿼리 id 바꾸기… | Edit | `mybatisDocActive && mybatisStatementSelected` | `context:mybatisStatements/rename@20` | - | 같음 (03-01) |
| `oranda.mybatis.deleteStatement` | 쿼리 지우기 | Edit | `mybatisDocActive && mybatisStatementSelected` | `context:mybatisStatements/delete@30` | - | 같음 (03-01) |
| `oranda.mybatis.expandToSqlTab` | 펼친 SQL 을 새 SQL 탭으로 | Sql | `mybatisDocActive && mybatisStatementSelected` | `context:mybatisStatements/expand@40` | - | 같음 (03-01) |
| `oranda.mybatis.expandMapper` | 매퍼 전체를 SQL 탭으로 펼치기 | Sql | `mybatisDocActive` | `context:mybatisStatements/expandAll@50`(매퍼 머리 우클릭) | - | 같음 (03-01) |
| `oranda.mybatis.runScenario` | 시나리오 값으로 실행 | Sql | `mybatisDocActive && mybatisStatementSelected && (hasSession \|\| dedicatedSession \|\| tabConnectionMissing) && !sessionBusy` | `toolbar:mybatis/run@10`, `menu:sql/mybatisRun@15` | F9 (mac Ctrl+Enter, F9) | `Run/MyBatisRunCommands.cs` (03-02) |
| `oranda.mybatis.explainScenario` | 펼친 SQL 실행 계획 | Sql | 위와 같음 | `toolbar:mybatis/explain@20`, `menu:sql/mybatisExplain@52` · 분할 머리 줄 단추(03-10) | - | 같음 (03-02) |
| `oranda.mybatis.copyExpanded` | 펼친 SQL 복사 | Edit | `mybatisDocActive && mybatisStatementSelected` | `toolbar:mybatis/copy@50`, `context:mybatisExpanded/copy@10`(쿼리 탭 안 펼친 SQL 분할 우클릭, 03-10) · 분할 머리 줄 단추 | - | 같음 (03-02) |
| `oranda.mybatis.copyExpandedWithValues` | 펼친 SQL 복사(값 넣은 글) | Edit | 위와 같음 | `toolbar:mybatis/copy@51`, `context:mybatisExpanded/copyValues@20`(같음) · 분할 머리 줄 단추 | - | 같음 (03-02) |
| `oranda.mybatis.save` | xml 에 저장 | File | `mybatisDocActive && mybatisDocDirty` | `toolbar:mybatis/save@40`, `menu:file/mybatisSave@32` | (Ctrl+S 는 `file.save` 경유, 4.4) | `Save/MyBatisSaveCommands.cs` (03-03) |
| `oranda.mybatis.checkCombinations` | 조합 검사 | Sql | `mybatisDocActive && mybatisStatementSelected` | `toolbar:mybatis/combos@30`, `menu:sql/mybatisCombos@25` | - | `Combos/MyBatisComboCommands.cs` (03-04) |
| `oranda.mybatis.applyAutoFix` | 자동 고침(<set>·<where> 로 감싸기) | Edit | `mybatisDocActive && mybatisStatementSelected && !readOnlyDoc` | `context:mybatisEditor/autoFix@10`(문장 편집기 우클릭, 03-01 이 그림) | - | 같음 (03-04) |
| `oranda.mybatis.convertWorkspace` | MyBatis 작업 공간으로 바꾸기… | File | `sqlWorkspaceActive && !mybatisDocActive && mybatisConvertReady` | `menu:file/mybatisConvert@97`, `context:documentTab/mybatisConvert@40` | - | `Conversion/MyBatisConvertCommands.cs` (03-05) |
| `oranda.mybatis.openFolder` | 매퍼 폴더 열기… | File | `mybatisFolderReady` | `menu:file/mybatisOpenFolder@91` | - | `Folder/MyBatisFolderCommands.cs` (03-07) |
| `oranda.mybatis.findStatement` | MyBatis 쿼리 찾기… | Navigate | `mybatisDocActive \|\| mybatisFolderOpen` | `menu:navigate/mybatisFind@70` | - | 같음 (03-07) |

- 아이콘(문서 도구 막대·메뉴): run·explainPlan·copy·save·compile(조합 검사)·add·rename·delete·fileSql·docEdit·swap·folder·find(기존 아이콘 키, 특성에 넣어 동결).
- 처리기의 `When`: 단축키가 있는 `runScenario` 만 `When = "mybatisDocFocus"`(주 메뉴에서는 `mybatisDocActive` 로 봄, 4.3). 나머지는 `When` 없음(주 메뉴에서 숨기지 않고 Enablement 로 회색).
- 대상 문장: 셸의 우클릭 메뉴 공급(`ShellContextMenus.Factory(표면)`)은 매개변수를 넘기지 못한다. 그래서 「문장」 창은 우클릭 메뉴를 띄우기 전에 `IMyBatisWorkspaceSession.SelectStatement(id)` 를 부르고, 처리기는 `SelectedStatementId` 를 대상으로 쓴다(없으면 활성 문장 탭의 문장). `mybatisStatementSelected` = 고른 문장 **또는** 활성 문장 탭이 있음. 문서 탭 우클릭(`context:documentTab`) 처리기는 우클릭한 문서의 Panel(`DockTabMenu.TargetPanel`)을 대상으로 쓴다.
- 「MyBatis 로그 붙여 넣기」는 새 명령을 만들지 않는다. 기존 `oranda.edit.pasteMyBatisLog`(Features/MyBatisLog, 03-06 소유)의 처리기가 MyBatis 문서일 때 분기한다(MB-09).
- 자리 이름과 그리는 곳:
  - `menu:*`: 셸 주 메뉴. 번호는 지금 메뉴 번호와 겹치지 않게 골랐다(파일 30 save·40 saveAs·90·92·96, SQL 14·16·50, 탐색 60 사이).
  - `context:<표면>`: 셸의 `ShellContextMenus.Factory(표면)` 으로 그린다. `mybatisStatements`(문장 창, 03-01)·`mybatisExpanded`(쿼리 탭 안 펼친 SQL 분할, 03-02 → 03-10)·`mybatisEditor`(문장 편집기, 03-01)는 그 View 가 Factory 를 부른다. `documentTab` 은 01-02 가 문서 탭 우클릭 메뉴(`DockTabMenu`)에 Factory 항목을 덧붙여 그린다(2026-10-23 조정자 허가).
  - `toolbar:mybatis`: 셸 도구 막대는 `main` 만 그리므로, 문서 도구 막대(03-01 의 MyBatis 문서 머리)가 레지스트리에서 `toolbar:mybatis` 자리 명령을 읽어 아이콘 단추를 만든다(실행은 `ICommandRegistry.ExecuteAsync` 한 경로).
  - 모르는 자리 이름은 셸이 무시한다(`Placement.TryParse`, 예외 없음).
- 패널 안 단추(시나리오 추가·복제·지우기, 알림 띠의 「차이 보고 고르기 / 파일 것으로 / 내 것으로」, 조합 검사 「다시 검사」·「시나리오로 저장」·「편집기에서 보기」)는 그 패널 ViewModel 의 RelayCommand 다. 레지스트리 명령으로 올리지 않는다.

### 4.2 단축키

| 명령 | default · orange5 · plsqldev | sqldev | when |
|------|------------------------------|--------|------|
| `oranda.mybatis.runScenario` | `F9`, mac `Ctrl+Enter`·`F9` | `Primary+Enter`·`F9` | `mybatisDocFocus` |

- 지금 F9 는 `sqlDocFocus` 의 `runForEdit`(sqldev 는 `runStatement`)다. `mybatisDocFocus` 자리(mybatisText)는 `sqlDocFocus` 자리(sqlText·sqlGrid)와 겹치지 않으므로 키 충돌 규칙(commands.md 2.3)·충돌 시험을 그대로 통과한다. 전역 바인딩과의 충돌 검사가 새 자리에도 걸리도록 `FocusScopes.AllSlots` 에 `mybatisText` 를 넣는다.
- 다른 MyBatis 명령에는 기본 키를 두지 않는다(메뉴·문서 도구 막대·팔레트). 프리셋 4개에 위 한 줄씩 더하고, commands.md 에 MyBatis 줄을 더한다(TSK-01-02, Q1).
- 결과 그리드(sqlGrid 자리)에 포커스가 있을 때의 F9 는 지금처럼 `runForEdit` 쪽이다. 4.4 에서 일반 실행 명령은 MyBatis 문서에서 꺼지므로 아무것도 하지 않는다.
- 문장 편집기에서 Ctrl+S 는 `file.save`(전역)로 간다. `KeyBindingTable` 은 when 이 맞는 것 중 범위 있는 바인딩을 전역보다 먼저 고르는데, Primary+S 에는 범위 바인딩이 없다.

### 4.3 문맥 키

| 키 | 종류 | 참일 때 | 누가 정하나 |
|----|------|---------|-------------|
| `mybatisDocFocus` | 포커스 키 | 문장 편집기 글자 영역(자리 mybatisText) | FocusScopes(01-02), 자리 = 03-01 문장 편집기 View |
| `mybatisDocActive` | 상태 키 | 활성 작업 공간의 `DocumentKind == MyBatis` | `MyBatisWorkspaceWiring`(01-02) |
| `mybatisDocDirty` | 상태 키 | 활성 MyBatis 문서의 세션 dirty | 같음 |
| `mybatisStatementSelected` | 상태 키 | 「문장」 창에서 고른 문장 또는 활성 문장 탭이 있음 | 같음(세션의 `SelectedStatementId`) |
| `mybatisFolderOpen` | 상태 키 | 매퍼 폴더가 열려 있음 | 03-07(자기 Folder/ 서비스가 `ContextKeyService.Set`) |
| `mybatisFolderReady` · `mybatisConvertReady` | 상태 키 | 매퍼 폴더 기능 · 바꾸기 기능이 들어왔음(R24: 기능 레인 머지 전까지 빈 처리기 메뉴를 누를 수 없게) | 03-07 · 03-05 가 자기 폴더의 시작 서비스에서 참으로 켬 |

- 갱신 시점: `MyBatisWorkspaceWiring`(시작 작업, 소유 폴더 `Hooks/`)이 `SqlEditorHost.ActiveWorkspaceChanged`·`ActiveChanged`, 활성 작업 공간의 `DocumentKind` 변경, 세션 `StateChanged` 를 받아 `ContextKeyService.Set` 으로 위 세 키를 바로 넣는다. 지금 `SqlEditorDockWiring.PushKeys` 는 탭 사건에만 돌고 작업 공간 전환에는 돌지 않으므로 그것에 기대지 않는다(`SqlEditorDockWiring.cs` 는 고치지 않는다).
- 주 메뉴 숨김 규칙(`MenuVisibility.TabScopes`): `mybatisDocFocus` → `mybatisDocActive`, `editorFocus` → 지금 식 `|| mybatisDocActive`, `sqlEditorFocus`·`sqlDocFocus` → `sqlDocActive && !mybatisDocActive`. `gridFocus` 는 그대로(MyBatis 문서도 결과 그리드를 쓴다). commands.md 2.4 의 대응 문장도 같이 고친다(2026-10-23 조정자 허가).
- `sqlDocActive` 는 MyBatis 문서에서도 참으로 둔다. 탭 접속·결과·메시지·접속 콤보가 그대로 동작해야 하기 때문이다.

### 4.4 MyBatis 문서에서 기존 명령

명령의 실행 가능 여부는 Enablement 만 보고 When 은 보지 않는다(`CommandRegistry`). 그래서 xml 원문을 SQL 로 다루는 명령은 Enablement 에 `&& !mybatisDocActive` 를 더해 끈다(특성 문자열만 고침, MyBatis 문서가 없으면 언제나 참이라 지금 동작 그대로).

| 기존 명령 묶음 | MyBatis 문서에서 | 손잡이 |
|----------------|------------------|--------|
| SQL 실행(`exec.runStatement`·`runSubquery`·`runSelection`·`runScript`·`runFromCursor`·`runStep`·`runToNewResultTab`·`runAllTabs`), `exec.runForEdit` | 주 메뉴 숨김, 도구 막대·팔레트 회색 | `ExecutionCommands.cs`·`GridEditCommands.cs` Enablement(2026-10-23 조정자 허가) |
| 실행 계획(`plan.explain`·`plan.actual`), 서식(`format.document`·`format.statement`), `sqlplus.runFile` | 회색 | `ExecutionPlanCommands.cs`·`FormatterProfilesCommands.cs` 등 Enablement(2026-10-23 조정자 허가) |
| 그 밖 SQL 편집 전용(`exec.bindVariables`·`pasteBindValues`·`goToError` 등 `When = sqlDocFocus`) | 주 메뉴에서 숨김, 키는 포커스 자리로 안 잡힘 | `MenuVisibility.TabScopes`, `FocusScopes` |
| 편집(`edit.*`, `When = editorFocus`) | 그대로 됨 | `editorFocus` 에 mybatisText |
| `file.save` (Ctrl+S) | 활성 작업 공간이 MyBatis 면 저장 훅(= `oranda.mybatis.save` 처리기). 활성 탭이 없어도 된다 | `SqlEditorHost.SaveAsync`·`SqlEditorCommandHandler` 의 `file.save` 분기(01-02), 구현 03-03 |
| `file.saveAll` | MyBatis 작업 공간마다 저장 훅을 한 번 부른다(dirty 탭 순회와 따로, 세션 dirty 기준) | `SaveAllAsync` 분기 |
| `file.saveAs`, `file.renameTab`, `file.saveWorkspaceFile(As)` | 회색. 매퍼 다른 이름 저장은 범위 밖. Markdown 작업 공간 저장은 문장 고침을 「저장됨」으로 지워 버리므로 막는다 | `SqlEditorCommandHandler.cs` Enablement(2026-10-23 조정자 허가) |
| `file.duplicateTab`, `file.reopenClosed`, `file.newSqlEditor` | 일반 작업 공간으로 간다(2.2) | `SqlEditorHost.Tabs.cs` 분기 |
| `edit.pasteMyBatisLog` | 03-06 분기 | 03-06 |
| 결과·그리드·접속·트랜잭션 | 그대로 | - |

Ctrl+S 결정(2026-10-23 조정자 답, Q1 일부 변경): `Primary+S` 는 `file.save` 의 전역 바인딩이고, 충돌 규칙(commands.md 2.3 규칙 1)과 `KeymapLoaderTests.Preset_has_no_key_conflict_in_overlapping_focus_scope` 가 「전역은 모든 자리와 겹침」으로 본다. 그래서 `oranda.mybatis.save` 에 키를 주지 않는다. 대신 Ctrl+S → `file.save` → 저장 훅 → `oranda.mybatis.save` 와 같은 처리기다(03-03 이 훅을 구현). 문서 도구 막대 「xml 에 저장」 툴팁은 `file.save` 의 키를 보인다.

## 5. 저장 흐름 (MB-06, TSK-03-03)

### 5.1 경로

```
Ctrl+S / 「xml 에 저장」 / 모두 저장
  └─ IMyBatisSaveHandler.SaveAsync(작업 공간)                         [03-03]
       1. 세션의 고친 문장 목록 → MapperEdit 목록(문장 본문 바꾸기·새 문장·지우기·id 바꾸기)
       2. 디스크 해시를 다시 읽어 연 때 해시와 비교 → 다르면 5.3 병합 먼저
       3. IMyBatisMapperDocument.Apply(편집) → 새 원문                   [02-01]
       4. Check(새 원문, 디스크 해시) → MapperWriteCheck(xml 형식·id 겹침·밖에서 바뀜)
       5. 「바뀌는 부분」 창(설정 「저장할 때마다 이 창 보기」가 켜졌으면)
          - 문장 체크(일부만 저장 → 체크 안 한 문장은 편집 목록에서 빼고 3·4 를 다시)
          - 한 줄·나란히 diff, 검사 결과, 「조합 검사 안 함」(선택) 표시
       6. IMapperFileWriter.WriteAsync(경로, 새 원문, 원래 인코딩·BOM·줄 끝)  [02-01 구현]
       7. 세션 기준을 새 원문·새 해시로 바꾸고 고친 표시를 지운다
```

- 파일 전체를 다시 쓰는 경로를 만들지 않는다. 바꾸지 않은 부분은 Apply 가 바이트까지 그대로 둔다(MB-01).
- 연 때 문장 원문과 같아진 문장은 고친 목록에서 뺀다(되돌린 고침은 쓰지 않는다).

### 5.2 원자적 쓰기 (`IMapperFileWriter`, 계약 01-02 · 구현 02-01)

- 같은 폴더의 임시 파일(`<이름>.oranda-tmp`, 지금 `.sql` 저장과 같은 규칙)에 쓰고 `File.Replace`(대상이 있을 때) 또는 `File.Move`(없을 때)로 바꾼다. 실패하면 임시 파일을 지우고 원본은 그대로 둔다.
- 인코딩: 연 때 읽은 xml 선언·BOM 유무를 그대로 쓴다. 줄 끝·끝 줄바꿈은 Apply 가 원문 조각 단위로 지킨다(새 문장은 문서의 줄 끝을 따른다).
- 읽기 전용·쓰기 오류: 예외를 저장 처리기가 받아 알리고 고침을 남긴다(세션 상태 그대로).
- 03-05(바꾸기)도 새 매퍼·기존 매퍼 추가를 이 인터페이스로 쓴다(위험 R6). 그래서 계약은 01-02, 구현은 02-01 이다.

### 5.3 밖에서 바뀜 · 3방향 문장 병합

- 파일 감시: `FileSystemWatcher`(디바운스 300 ms)는 매퍼 파일 하나를 본다. 감시 상태는 상태 표시줄에 보인다.
- 세 원문: 기준(연 때 또는 마지막 저장) · 디스크 · 내 것(세션). 셋 다 Parse 해서 문장 id 로 맞댄다.

| 경우 | 처리 |
|------|------|
| 내가 고친 문장 없음 | 디스크를 다시 읽고 탭을 바꾼 뒤 알림 한 줄 |
| 고친 문장이 서로 다름 | 알림 띠: 내가 안 고친 문장은 디스크 것으로 반영, 내 고침은 남김 |
| 같은 문장을 양쪽이 고침 | 알림 띠에서 그 문장만 「차이 보고 고르기 / 파일 것으로 / 내 것으로」 |
| 디스크에서 지워짐·옮겨짐 | 알리고 고침을 남긴다. 저장하면 03-03 의 저장 창이 「같은 경로에 다시 쓰기 / 경로 골라 쓰기」를 묻는다(`file.saveAs` 는 쓰지 않는다) |
| 디스크에서 문장이 새로 생김·지워짐 | 기준·디스크 쪽 변화로 보고 반영(내 고침과 겹치면 같은 문장 경우로) |

- 병합 결과는 다시 「기준 = 디스크」로 두고(세션 `Rebase`) 내 고침만 고친 목록에 남긴다. 그래서 다음 저장은 5.1 그대로다.
- 내 저장도 감시에 걸린다(임시 파일 → 바꾸기). 그래서 감시 사건이 오면 디스크 해시를 읽어 **세션 기준 해시와 같으면 무시**한다(저장 직후 기준을 새 해시로 바꾸므로 자기 쓰기는 늘 같다).

### 5.4 저장 전 검사 (`MapperWriteCheck`)

xml 형식(XmlReader, DTD 무시·resolver null), 문장 id 겹침, 연 때 해시 ≠ 디스크 해시. 조합 검사 여부는 검사가 아니라 표시만 한다(03-04 결과가 있으면 「조합 검사 통과/오류 n」).

### 5.5 루트가 mapper 가 아님 · 깨진 xml

열 때 알리고 작업 공간을 만들지 않는다(03-01). 저장 때 새 원문이 깨지면 4번 검사가 쓰기를 막는다.

### 5.6 닫기 · 종료 · 복구

- 매퍼 작업 공간 닫기: 세션 dirty 면 「저장 / 저장 안 함 / 취소」 한 번. `TryCloseWorkspaceAsync` 의 MyBatis 분기(01-02)가 탭 수와 상관없이 `IMyBatisSaveHandler.ConfirmCloseAsync`(03-03)를 부른다. 기존 가드 `ConfirmSaveAsync` 는 MyBatis 문장 탭을 묻지 않는다(`NeedsSaveConfirm` 분기, 01-02).
- 앱 종료·비정상 종료: 묻지 않고 복구에 남긴다. 복구 상태 = 작업 공간 저장본(`workspaces/{id}.json`·`autosave/{id}.json`, 문장 탭의 Content)에 더해, 세션 상태를 같은 폴더의 곁 파일 `{id}.mybatis-{Key}.json`(복구 참여자마다 하나, 9장 `IMyBatisRecoveryParticipant`)에 둔다.
  - Core `Workspace` 레코드에는 종류·MyBatis 상태 칸이 없다. 칸을 더하면 Core 계약(`Oranda.Core.Workspaces`, 모든 작업 공간 저장·복원 경로)이 바뀌고 01-02 소유 밖이다. 곁 파일이면 MyBatis 쪽 파일만 늘고 기존 작업 공간 파일 모양은 그대로다.
  - 곁 파일 내용: 매퍼 경로, 연 때 해시·원문(기준), 문장 탭 ↔ 문장 id·종류, 새 문장·지운 문장·id 바꾸기 목록, 고르던 문장.
  - 수명: 저장본(`workspaces/`)과 자동 저장본(`autosave/`)을 따로 둔다. 쓰기·지우기·이름 바꾸기는 작업 공간 저장본과 같은 시점이다. 호스트의 자동 저장 삭제(복구 거절·빈 자동 저장·종료·닫기)와 「작업 공간 저장」의 ID 바꾸기마다 복구 훅의 `Delete`·`Rename` 을 함께 부른다(Autosave·Persistence·Workspaces 훅, 01-02). 경로는 `IAppDataPaths` 의 작업 공간·자동 저장 폴더를 쓴다.
  - 자동 저장 표지(`ChangedSinceAutosave`)에 세션 표지를 더해, MyBatis 쪽만 바뀌어도 자동 저장이 돈다.
  - 시작 복원(`Restore`) 뒤 `SourcePath` 가 .xml 이고 곁 파일이 있으면 복구 훅이 세션을 다시 붙인다. 매퍼 파일이 바뀌었으면 5.3 병합 규칙을 쓴다.

## 6. 시나리오 저장 위치 (MB-04, TSK-02-03)

- 앱 로컬 데이터 `oranda.db`(SQLite, 지금 이력·변경 일지와 같은 DB). 매퍼 폴더에는 파일을 만들지 않는다(D-001).
- 표 `mybatis_scenario` — 마이그레이션 `0003_mybatis_scenario.sql`(01-02, 빈 표):

| 열 | 형 | 뜻 |
|----|----|----|
| id | INTEGER PRIMARY KEY | |
| mapper_path | TEXT NOT NULL | 정규화한 절대 경로(Windows 는 대문자로 바꿔 비교용 키로도 씀 → 02-03 이 정함) |
| namespace | TEXT | 매퍼 namespace(없으면 NULL) |
| statement_id | TEXT NOT NULL | 문장 id |
| name | TEXT NOT NULL | 시나리오 이름(한글 가능) |
| values_json | TEXT NOT NULL | 파라미터 이름 → 값(문자열·숫자·null·목록). 비밀 이름(pass·pwd·secret·token 이 든 이름) 값은 넣지 않는다 |
| sort_order | INTEGER NOT NULL | 목록 순서 |
| created_at · updated_at | TEXT NOT NULL | ISO 8601 UTC |

- 색인: (mapper_path, statement_id, sort_order), (namespace, statement_id). 마이그레이션은 지금 0002 처럼 `IF NOT EXISTS` 로 멱등하게 쓴다.
- 찾기: 경로 + 문장 id → 없으면 namespace + 문장 id(매퍼를 옮긴 경우). id 바꾸기는 `MoveStatement(mapperPath, oldId, newId)` 가 따라간다.
- 계약(Core, `src/Oranda.Core/MyBatis/`): `IMyBatisScenarioStore` = List·Save·Rename·**Duplicate**·Delete·MoveStatement(TRD 4 + wbs 02-03 의 Duplicate, 위험 R10). Core 는 다른 Oranda 어셈블리를 참조하지 않으므로 값은 Language 형이 아니라 이름 → 값 사전(`MyBatisScenarioValue`)으로 둔다. 쓰기는 비동기이고 구현이 백그라운드에서 한다.

## 7. ReadMyBatis 이관 범위 (PRD 7, TSK-02-02)

| 지금 (`WorkspaceFiles.cs` 372~563행) | 이관 뒤 |
|-------------------------------------|---------|
| `ReadMyBatis(xml, name)` — XDocument 로 읽어 문장마다 탭 | **남긴다**(Q8 기본안 (a)). 위치는 `WorkspaceFiles.MyBatis.cs`(01-02 가 partial 로 나눔, 이후 02-02 소유). 몸은 02-01 Parse + 02-02 계산기 「모두 켬」으로 바뀐다 |
| `MyBatisText`(include·if·where·set·trim·choose·foreach·bind 단순화, `Trim`, `Params`) | **지운다**. 같은 일을 계산기(`MyBatisEvaluator`)가 한다 |
| 표기 변환: `#{a.b}` → `:a_b`, `${x}` → `&x`, 조건을 `/* if … */` 주석으로 | ReadMyBatis 안의 얇은 어댑터로 남긴다(계산기 결과의 블록 목록에서 주석을 붙인다). 계산기 자체의 바인드 이름 규칙은 MB-02(`:x`, foreach `:item_1`)다 |
| 머리 줄 `-- MyBatis ns.id <kind>`, 알림(동적 태그 n개) | 그대로 |

- 「모두 켬」 = 값 없이 부를 때 if·when 은 켬, choose 는 첫 when, foreach 는 본문 한 번(지금 `(:id)` 모양), bind 는 주석. 기존 `WorkspaceFilesTests` 의 MyBatis 시험 2개가 그대로 통과해야 한다(02-02 acceptance).
- 호출하는 곳: 지금은 `WorkspaceFiles.Load`(.xml) ← `OpenWorkspaceFileAsync`. 03-01 뒤에는 .xml 열기가 열기 훅으로 가므로 직접 부르는 곳은 03-01 의 `oranda.mybatis.expandMapper`(「매퍼 전체를 SQL 탭으로 펼치기」, 문장 창 매퍼 머리 우클릭 → 결과를 새 일반 작업 공간으로 연다)다. 문장 하나의 「펼친 SQL 을 새 SQL 탭으로」는 계산기를 바로 쓴다.
- `WorkspaceFiles.MyBatis.cs` 에 Language 형 static 필드를 두지 않는다. `KindOf`·`IsMyBatis` 는 시작 복원 때 불리므로, 그때 `Oranda.Language` 가 로드되면 안 된다(Language 는 ReadMyBatis 몸 안에서만 쓴다).
- 03-01 이 .xml 을 MyBatis 작업 공간으로 열면 `IsReadOnlySource` 의 .xml 판정과 `WorkspaceFilePath` 규칙은 그대로 둔다(MyBatis 문서는 Markdown 작업 공간 파일 경로를 갖지 않는다).

## 8. MB-01 ~ MB-10 대응표

| MB | 화면 | 명령 | 데이터·계약 | Task |
|----|------|------|-------------|------|
| MB-01 매퍼 원문 모델 | (화면 없음) 문장 탭 원문 = 요소 범위 | - | `IMyBatisMapperDocument`·`MyBatisElement`·`MapperEdit`·`MapperWriteCheck`(Language), `IMapperFileWriter` | 01-02 계약 · 02-01 |
| MB-02 동적 SQL 계산 | 편집기 흐림·점·물결(03-02), 펼친 SQL 패널 | - | `IMyBatisEvaluator`·`EvalResult`·`MyBatisParameter`·`ConditionCombination`(Language), `IMyBatisFragmentResolver`·`MyBatisFragment`(Core) | 01-02 계약 · 02-02 |
| MB-03 작업 공간 화면 | 문서 탭·문장 창·문장 탭·머리 줄·강조, 도구 창 보임 규칙(3장) | newStatement·renameStatement·deleteStatement·expandToSqlTab·expandMapper | 문서 종류 `MyBatis`, `IMyBatisWorkspaceSession`, 열기·복구 훅 | 01-02 자리 · 03-01 |
| MB-04 파라미터 · 시나리오 | 「바인드 변수」 창(기존, 파라미터 행) + 「펼친 SQL」 머리 줄 시나리오 콤보 | (패널 안 단추) | `IMyBatisScenarioStore`·`MyBatisScenario`(Core), 표 `mybatis_scenario` | 01-02 계약 · 02-03 · 03-02 |
| MB-05 펼친 SQL · 실행 | 쿼리 탭 안 펼친 SQL 분할(03-10), 켜진 조건 수, 결과 창 | runScenario(F9)·explainScenario·copyExpanded·copyExpandedWithValues | `EvalResult`, 기존 `IExecutionService`(정책 관문·탭 접속) | 03-02 |
| MB-06 매퍼에 저장 | 바뀌는 부분 창, 알림 띠, 상태 표시줄 구간 | mybatis.save, file.save 경유(Ctrl+S) | `MapperEdit`·`MapperWriteCheck`·`IMapperFileWriter`, 저장·복구 훅 | 03-03 |
| MB-07 조합 검사 | 조합 검사 패널, 문장 창 오류 배지, 빠른 고침 띠 | checkCombinations·applyAutoFix | `ConditionCombination`, L2 파서(`PlSqlAnalyzer`) | 03-04 |
| MB-08 일반 → MyBatis | 바꾸기 대화 상자 | convertWorkspace(파일 메뉴·문서 탭 우클릭) | `IMapperFileWriter`, `IMyBatisScenarioStore` | 03-05 |
| MB-09 로그로 문장 찾기 | (후보 고르기 상자) | 기존 pasteMyBatisLog 분기 | `MyBatisLogParser`(재사용), `IStatementLocator`, `IMyBatisScenarioStore` | 03-06 |
| MB-10 매퍼 폴더 열기 | 문장 창 폴더 트리 모드 | openFolder·findStatement | 폴더 `IMyBatisFragmentResolver`, `IStatementLocator` 폴더 구현 | 03-07 |

## 9. 확장 지점 (TSK-01-02 가 만들고 동결)

공유 파일을 여러 기능 Task 가 고치지 않게, 01-02 가 아래 인터페이스와 그 호출 자리를 만든다. 기능 Task 는 `Features/MyBatisWorkspace/<자기 폴더>/` 안에 구현 클래스를 두고 `[OrandaService]` 로 등록한다(공유 진입점에 줄을 더하지 않는다). 위치는 모두 `src/Oranda.App/Features/MyBatisWorkspace/Hooks/` 이다(계산기·문서 계약은 Language, 시나리오·조각 해석기·쓰기는 Core).

| 인터페이스 | 부르는 곳(01-02 가 넣음) | 구현(소유) | 개수 |
|------------|--------------------------|-----------|------|
| `IMyBatisWorkspaceSession` | `SqlWorkspaceViewModel.MyBatis`(붙으면 `DocumentKind = MyBatis`, 떼는 때 = 작업 공간 닫기) | 03-01 `Workspace/` | 작업 공간당 1 |
| `IMyBatisOpenHandler` | `SqlEditorHost.OpenWorkspaceFileAsync` 의 .xml 분기 앞, `OpenFileAsync` 에 새로 두는 .xml 분기(Ctrl+O 로 고른 .xml), 명령줄·끌어 놓기(`DroppedWorkspaceFiles.Pick` 이 .xml 도 고름). 처리하면 끝, 아니면 지금 경로 | 03-01 | 0~1 |
| `IMyBatisSaveHandler` | `SaveAsync`·`SaveAllAsync`·`file.save`(활성 작업 공간이 MyBatis 면 넘김, 탭 없어도), `TryCloseWorkspaceAsync`(세션 dirty 면 `ConfirmCloseAsync`) | 03-03 `Save/` | 0~1 |
| `IMyBatisRecoveryParticipant` | `Persistence`(Restore 뒤·저장 때·ID 바꾸기), `Autosave`(자동 저장·삭제·종료), `Workspaces`(닫기), `SqlWorkspaceViewModel.ChangedSinceAutosave`(표지) | 03-01 `Workspace/Recovery*`(열린 매퍼·문장·고침), 03-03 `Save/`(감시 기준) | 여럿 |
| `IMyBatisEditorViewFactory` | `SqlWorkspaceView.axaml.cs` 의 탭별 View 생성(MyBatis 문서면 이것, 아니면 `new SqlEditorView`), 탭 머리 자리 | 03-01 `Editor/` | 0~1 |
| `IMyBatisEditorDecorator` | 03-01 문장 편집기 View 가 열 때 전부 붙임(`TextView` 의 LineTransformer·BackgroundRenderer·여백) | 03-01 `Editor/Highlighting/`, 03-02 `Editor/EvalDecorator*`, 03-04 `Combos/AutoFixDecorator*` | 여럿 |
| `IMyBatisStatementBadgeProvider` | 03-01 「문장」 창 항목 그리기 | 03-04 `Combos/StatementBadge*` | 여럿 |
| `IMyBatisStatementsViewMode` | 03-01 「문장」 창의 보기 모드 전환(파일 하나 / 폴더 트리) | 03-07 `Folder/FolderTree*` | 여럿 |
| `IStatementLocator` | 03-06 로그 붙여 넣기의 문장 찾기, 03-07 문장 찾기 | 03-06 `LogPaste/`(열린 매퍼), 03-07 `Folder/`(폴더) | 여럿 |

- 세션 최소 멤버(01-02 가 동결, 03-02·03-03·03-04·03-06 이 03-01 파일을 고치지 않고 쓰게):
  - 읽기: `MapperPath`·`MapperNamespace`·`Document`(지금 기준 `IMyBatisMapperDocument`)·`BaselineHash`·`IsDirty`·`SelectedStatementId`, `StatementIdOf(tab)`·`ElementOf(statementId)`(고친 원문이 반영된 `MyBatisElement`)·`Fragments`(같은 매퍼 조각 해석기)·`PendingEdits()`(→ `MapperEdit` 목록)
  - 쓰기: `Rebase(newText, writtenStatementIds)`(저장 뒤 기준 바꾸기, 03-03)·`ApplyDiskText(diskText, keepMine)`(병합 결과 반영, 03-03)·`SelectStatement(statementId)`
  - 사건: `StateChanged`(dirty·문장 목록·고른 문장)
  - 정확한 시그니처는 B6 이 코드로 정한다.
- `IStatementLocator` 는 초안(§2.11 K3)에서 03-06 이 만들기로 했던 것을 01-02 로 옮겼다. 그러면 03-07 이 03-06 머지를 기다리지 않고 구현을 꽂을 수 있다(위험 R4 완화. 띄우는 순서는 조정자가 정함).
- 훅 해석과 순서: `SqlEditorHost` 는 DI 보다 먼저 만들어진다(`SqlEditorHost.Current`). 그래서 01-02 의 시작 작업 `MyBatisWorkspaceWiring`(`IStartupTask`, `AfterFirstFrame`)이 훅 자리(`SqlEditorHost.MyBatisHooks`)를 넣는다. 복구(`InitializeAsync`)보다 먼저 서야 하므로 `RunAsync` 안에서 **바로(UI 스레드, Post 없이)** 넣는다. `SqlEditorDockWiring` 은 같은 단계에서 `Dispatcher.UIThread.Post` 로 복구를 미루므로, 시작 작업들의 `RunAsync` 가 차례로 끝난 뒤에 복구가 돈다(B6 이 시작 순서 시험 1개로 확인).
  - 훅 자리는 DI 에서 구현을 **처음 쓸 때** 꺼낸다(`Lazy`). 구현이 없으면 비어 있고 지금 경로 그대로 간다(01-02 머지 직후의 상태).
- 시작 시험(상위 decisions D-006) 제약 — 시작 때 `Oranda.Language` 가 로드되면 안 된다:
  - App 의 형이 Language 인터페이스를 **구현**하면 셸의 어셈블리 훑기(`ShellContributions.Scan`·`ServiceContainer`)가 그 형을 읽을 때 Language 를 로드한다. 그래서 App 이 구현하는 계약은 Core 나 App 에 둔다: 조각 해석기 `IMyBatisFragmentResolver` 는 Core(9.1), 훅은 App.
  - 셸 훑기(`GetTypes`)는 App 형의 값 형식 필드 형도 읽는다. 그래서 시작 때 훑기는 App 형(특히 `[OrandaToolPanel]` ViewModel)에 Language 의 enum·구조체(`MyBatisElementKind`·`MyBatisParameterUsage`·`TextRange` 는 Core 라 괜찮음) 필드를 두지 않는다. 참조 형식 필드·지역 변수·메서드 시그니처는 괜찮다(B6 확인: 세션 인터페이스를 App 형이 구현해도 시작 때 Language 가 로드되지 않음).
  - `[OrandaToolPanel]` ViewModel, `[OrandaCommand]` 처리기, 훅 구현은 매개변수 없는 생성자 또는 등록된 Shell 서비스·`IServiceProvider` 만 받는 생성자를 둔다. Language 형(계산기·매퍼 파서)은 처음 쓸 때 해석한다(`OutlineViewModel` 주석과 같은 이유).
- 문장 편집기 View: `SqlWorkspaceView` 는 지금 탭별 View 를 `Dictionary<SqlEditorViewModel, SqlEditorView>` 로 들고 `FocusEditor()` 를 부른다. 01-02 는 이것을 `Control` + 포커스 인터페이스(`IEditorFocusTarget`, `SqlEditorView` 도 구현)로 바꾼다. 03-01 의 강조는 `SqlEditorView` 가 DataContext 마다 거는 `SqlSyntaxColorizer`(문서 전체, 구간 지정 없음)를 Attach 때 떼고(`Detach`) 자기 MyBatis 강조기로 그린다. SQL 구간은 L1 렉서(`Oranda.Language.Lexing`)를 다시 써서 같은 색 키로 칠한다(`SqlSyntaxColorizer.cs` 는 고치지 않는다).
- 시험 이음새: 실제 MyBatis 문서는 03-01 뒤에 생긴다. 01-02 의 렌더 점검(B10)과 보임 규칙 시험은 가짜 세션(`FakeMyBatisWorkspaceSession`)을 작업 공간에 붙여 MyBatis 문서를 만든다. 세션 인터페이스가 App 형이고 `Oranda.Testing` 은 Core 만 참조하므로, 가짜 세션은 `tests/Oranda.App.Tests/Features/MyBatisWorkspace/Fakes/`(01-02 소유, 동결)에 둔다. 붙이는 자리는 `internal` 이고 App.Tests 는 `InternalsVisibleTo`(Shell/ShellInternals.cs)로 접근한다. `Oranda.Testing/MyBatis/` 에는 Core·문자열만 쓰는 것(표본 매퍼 xml, 가짜 시나리오 저장소)을 둔다.

### 9.1 계약 형 (Language · Core, 이름과 핵심만)

- 오프셋 기준(위험 R8):
  - `MyBatisElement.ElementRange`·`BodyRange` = **매퍼 원문** 기준(`Oranda.Core.Text.TextRange`).
  - `EvalResult`·`MyBatisParameter`·`ConditionCombination` 의 모든 범위와 `Evaluate` 의 덮어쓰기(`blockOverrides`) 열쇠 = **문장 원문**(`MyBatisElement.Text`, 여는 태그의 `<` 가 0) 기준. 문장 탭 편집기 글과 같은 기준이라 꾸밈이 바로 쓴다.
- `MyBatisElement` 는 원문 문자열만으로 만들 수 있다: `Text`(요소 원문)·`Kind`·`Id`·`Attributes` 만 있으면 계산기가 쓴다. `ElementRange`·`BodyRange` 는 매퍼 안 위치(02-01 이 채움, 없으면 기본값). 그래서 02-02 는 02-01 을 기다리지 않는다.
- `Evaluate(statement, fragments, values, overrides)`(TRD 4) + 선택 인자 `MyBatisEvalOptions`(모두 켬·바인드 표기). 값 없이 「모두 켬」을 부르는 ReadMyBatis 어댑터가 쓴다.
- 문서 계약(TRD 4 의 Parse·Apply·Check)은 `IMyBatisMapperDocument`(인스턴스: 원문·인코딩 정보·namespace·요소 목록·해시, `Apply`·`Check`)와 파서 진입점 `IMyBatisMapperParser.Parse(string)` 로 나눈다(인스턴스 메서드 Parse 는 닭과 달걀이라).
- 조각 해석기는 Core: `IMyBatisFragmentResolver.Resolve(refid, currentNamespace)` → `MyBatisFragment?`(Core 형: Id·Namespace·Text). 03-07 의 폴더 구현이 App 에 있으므로 Language 에 두면 시작 때 Language 가 로드된다(9장). 계산기는 받은 조각 원문으로 `MyBatisElement` 를 만든다. wbs 01-02 의 「IMyBatisFragmentResolver(Language)」에서 위치만 바꾼다.
- `IMapperFileWriter` 는 Core(`src/Oranda.Core/MyBatis/`)에 둔다: 경로·새 원문·인코딩 정보(인코딩 이름·BOM 유무)만 받으므로 Language 형이 필요 없고, 03-03·03-05(App)와 구현(02-01, `src/Oranda.Platform/MyBatis/`)이 모두 볼 수 있다.
- 바인드 이름 규칙(`MyBatisBind`): `#{x}` → `:x`, 점 경로 `#{user.name}` → `:user_name`(Oracle 바인드 이름에 점 불가), foreach 안 `#{item}` → `:item_1`…(반복 순서), 겹치면 `_2`… 를 붙인다.
- 값 사전은 중첩 사전과 점이 든 평평한 열쇠(`"user.name"`, 시나리오 저장 모양)를 모두 받는다. 둘 다 있으면 평평한 열쇠가 이긴다.
- 블록은 원문 태그마다 하나다(foreach 반복·바깥 반복 안의 if 도 하나, 한 번이라도 켜졌으면 켬). 덮어쓰기: if·when = 켬·끔, foreach = 끔이면 빈 목록, 켬이면 값 목록 그대로(값 없음·빈 목록이면 항목 하나).
- include 한 조각 안에서 나온 블록·바인드·치환·문제는 `Range`(또는 `Source`) = 문장 원문의 `<include>` 태그 범위이고 `Fragment`(refid)·`FragmentRange`(조각 원문 안 범위)가 붙는다. 조각 안 블록은 따로 덮어쓸 수 없고 조합 검사 조건이 아니다.
- `MyBatisEvalOptions(AllOn, LegacyNotation)`: `LegacyNotation` 은 지금 ReadMyBatis 표기(`:a_b`, `${x}` → `&x`, 조건 주석)로 `Sql` 을 만든다. 02-02 의 ReadMyBatis 어댑터가 `AllOn + LegacyNotation` 으로 부른다(7장).
- 해시: `MapperHash.Compute(text)`(Core) = BOM 없는 글자를 UTF-8 로 SHA-256. 문서 `Hash`·디스크 해시·자기 쓰기 무시가 모두 이 함수다.
- `MapperEdit.InsertElement(Id, NewText, AfterId)`. 한 요소에는 편집 하나: 원문을 고치고 id 도 바꿨으면 `ReplaceElement` 하나(Id = 옛 id). 세션 `Rebase` 의 저장 문장 목록은 새 id.
- `IMyBatisScenarioStore.Changed`(문장 경로·id): 03-06·03-04·03-05 가 만든 시나리오가 03-02 창에 바로 보이게 한다(백그라운드 스레드에서 날 수 있음).
- 세션 추가 멤버: `OpenStatement(id)`(탭 열기·앞으로, 03-04·03-06·03-07), `BeginNewStatementAsync`(안쪽 탭 줄 「+」), `Relocate(newPath)`(03-03 경로 골라 쓰기). `Fragments` = 같은 매퍼 → `MyBatisHooks.FragmentResolvers`(03-07 폴더 등록) 순.
- 「문장」 창 보임: `DockWorkspace.SetToolAlwaysShown(toolId, bool)` — 03-07 이 매퍼 폴더를 열면 「문장」 창을 문서 종류와 상관없이 보이게 한다(폴더 트리 모드).
- Core `MyBatisScenario`: Id·MapperPath·Namespace·StatementId·Name·Values(이름 → `MyBatisScenarioValue`)·SortOrder·CreatedAt·UpdatedAt.

## 10. 공유 지점 목록 (TSK-01-02 편집 지점 표의 입력)

### 10.1 지금 코드에서 바꿀 공유 지점

| 파일 | 지금 역할 | 바꿀 점 | 01-02 처리 | 이후 |
|------|-----------|---------|------------|------|
| `Features/SqlEditor/WorkspaceFiles.cs` | md·sqd·xml 읽기·쓰기 | MyBatis 구역(372~563행)과 공통 `Dedent` | MyBatis 구역을 `WorkspaceFiles.MyBatis.cs` partial 로 옮김(동작 불변) | 본체 = 01-02 만, `.MyBatis.cs` = 02-02 소유 |
| `Features/SqlEditor/SqlEditorHost.Files.cs` | 파일·작업 공간 파일 열기·저장 | .xml 열기 분기, MyBatis 탭 저장 | 열기 훅(`OpenWorkspaceFileAsync`·`OpenFileAsync` 의 새 .xml 분기)·저장 훅(`SaveAsync`·`SaveAllAsync`) 호출 자리 | 01-02 전용, 기능 레인 금지 |
| `Features/SqlEditor/SqlEditorHost.Persistence.cs`·`.Autosave.cs` | 저장소 저장·복원·자동 저장·종료 | MyBatis 곁 파일, 탭 0개 MyBatis 자동 저장 유지 | 복구 훅 호출 자리(저장·복원·삭제·ID 바꾸기) | 01-02 전용 |
| `Features/SqlEditor/SqlEditorHost.Workspaces.cs` | 작업 공간 열기·닫기 | 닫힐 때 세션 정리 | 세션 떼기 자리(`RemoveWorkspace`) | 01-02 전용 |
| `Features/SqlEditor/SqlEditorHost.cs` | 활성 작업 공간·문맥 키 | 훅 자리 `MyBatisHooks` | 훅 자리만(문맥 키는 `MyBatisWorkspaceWiring` 이 넣으므로 `GetContextKeys` 는 그대로) | 01-02 전용(2026-10-23 조정자 허가) |
| `Features/SqlEditor/SqlEditorHost.CloseGuard.cs` | 탭·작업 공간 닫기 확인 | MyBatis 문장 탭을 묻지 않음, 작업 공간 닫기는 세션 dirty 기준 | `NeedsSaveConfirm` 분기, `TryCloseWorkspaceAsync` 분기 | 01-02 전용(같은 허가) |
| `Features/SqlEditor/SqlEditorHost.Tabs.cs` | 탭 넣기·복제·닫은 탭 다시 열기 | 문장이 아닌 탭을 일반 작업 공간으로, 문장 탭은 닫은 탭 기록에서 뺌 | 분기 | 01-02 전용(2026-10-23 조정자 허가) |
| `Features/SqlEditor/SqlEditorCommandHandler.cs` | 파일·편집 명령 처리기 | `file.save` 를 활성 작업 공간 기준으로, saveAs·renameTab·saveWorkspaceFile(As) 끔 | `file.save` 분기 1개, Enablement 문자열 | 01-02 전용(2026-10-23 조정자 허가) |
| `Features/SqlEditor/DroppedWorkspaceFiles.cs` | 명령줄·끌어 놓기에서 작업 공간 파일 고르기 | .xml 도 고름 | 1줄 | 01-02 전용(2026-10-23 조정자 허가) |
| `Features/SqlEditor/SqlWorkspaceViewModel.cs` | 작업 공간 VM | `DocumentKind`·`MyBatis` 세션·`IToolScopeDocument`, `IsDirty` 에 세션 dirty, 자동 저장 표지, 탭 이름 바꾸기 막음 | 속성·사건·표지 | 01-02 전용 |
| `Features/SqlEditor/SqlWorkspaceView.axaml(.cs)` | 안쪽 탭 줄·편집기 View 바꿔 끼우기 | 문장 편집기 View, 탭 머리 종류 표시, 「+」 | View 생성 훅(사전 형을 `Control` + `IEditorFocusTarget` 로), 탭 머리 자리(내용 없는 ContentControl), MyBatis 탭 우클릭 「이름 바꾸기」 숨김 | 01-02 전용(같은 허가) |
| `Shell/Docking/DockWorkspace.ScopedTools.cs`·`DockWorkspace.cs` | SQL 전용 도구 창 숨기기 | 3장 규칙, `IToolScopeDocument`, `RefreshScopedTools` | 규칙 확장 | 01-02 전용 |
| `Shell/Layout/LayoutPlanner.cs` | 기본 배치·새 창 끼우기 | `OpenRightTools` 에 파라미터 창 | 한 줄 | 01-02 |
| `Shell/Keymaps/FocusScopes.cs` | 포커스 자리 | `mybatisText`(`AllSlots` 포함), `mybatisDocFocus`, `editorFocus` 확장 | 표 수정 | 01-02 뒤 동결 |
| `Shell/Docking/DockTabMenu.cs` | 문서 탭 우클릭(고정 메뉴) | `context:documentTab` 레지스트리 항목 덧붙이기 | `ShellContextMenus.Factory("documentTab")` 자리 1개 | 01-02 전용(2026-10-23 조정자 허가) |
| `Shell/Menus/MenuVisibility.cs` | 주 메뉴 숨김 | 4.3 TabScopes | 표 수정 | 01-02 뒤 동결 |
| `Shell/Resources/CommandTitles.json`·`-en.json` | 명령 제목 | 4.1 의 15개 | 전부 미리 넣음 | 동결(새 명령은 조정자 승인) |
| `Resources/strings-ko.json`·`strings-en.json`·`literals-en.json` | 패널 제목·화면 글 | 패널 3개 제목, 자리 화면 글 | 자기 키만 정렬 위치에 | 공유(Q2, 자기 키만) |
| `docs/design/keymaps/*.json` 4개 · `docs/design/commands.md` | 키맵 프리셋·명령 문서 | 4.2 F9 한 줄, MyBatis 명령·문맥 키·포커스 자리 줄, 2.4 TabScopes 문장 | MyBatis 줄만(Q1) + 2.4 문장 1개(2026-10-23 조정자 허가) | 동결 |
| `Features/Execution/ExecutionCommands.cs`·`Features/GridEdit/GridEditCommands.cs` | SQL 실행 명령 | Enablement 에 `&& !mybatisDocActive` | 특성 문자열만(2026-10-23 조정자 허가) | 금지(기능 레인) |
| `Features/ExecutionPlan/ExecutionPlanCommands.cs`·`Features/FormatterProfiles/FormatterProfilesCommands.cs`(·`sqlplus.runFile` 처리기) | 실행 계획·서식 명령 | Enablement 에 `&& !mybatisDocActive` | 특성 문자열만(2026-10-23 조정자 허가) | 금지(기능 레인) |
| `Features/SqlEditor/SqlEditorView.axaml` | 편집기 View, 자리 sqlText | 고치지 않음(03-01 이 인스턴스 속성으로 덮어씀) | - | - |
| `Features/SqlEditor/SqlSyntaxColorizer.cs` | SQL 강조 | 고치지 않음(03-01 이 떼고 자기 강조기로, 9장) | - | 금지 |
| `Features/SqlEditor/SqlEditorDockWiring.cs` | 작업 공간 ↔ 도킹, 문맥 키 옮기기 | 고치지 않음(MyBatis 키는 `MyBatisWorkspaceWiring`) | - | - |
| `Features/MyBatisLog/**` | 일반 탭 로그 붙여 넣기 | MyBatis 문서 분기 | - | 03-06 |
| `Platform/Migrations/OrandaDb/` | oranda.db 스키마 | `0003_mybatis_scenario.sql` | 빈 표 | 열 추가는 0004(조정자 승인) |
| `tests/Oranda.Testing/` | 공용 가짜(Core 만 참조) | `MyBatis/` 표본 매퍼·가짜 시나리오 저장소 | 새 폴더 | 동결 |
| `tests/Oranda.App.Tests/Oranda.App.Tests.csproj` | App 시험 | Oranda.Testing 참조 | 1줄 | - |

### 10.2 새 폴더 구조 (`src/Oranda.App/Features/MyBatisWorkspace/`)

| 폴더 | 내용 | 소유 |
|------|------|------|
| `Hooks/` | 9장 인터페이스, `MyBatisHooks`, `MyBatisWorkspaceWiring` | 01-02(동결) |
| `Statements/` | 「문장」 창 VM·View, 문장 명령 처리기 | 03-01 |
| `Workspace/` | 열기 훅·세션·복구 구현 | 03-01 |
| `Editor/` | 문장 편집기 View·View 팩토리·머리 줄·탭 머리, `Highlighting/` | 03-01 (단 `Editor/EvalDecorator*` 는 03-02) |
| `Parameters/` | 바인드 변수 창 동기화·계산 상태·시나리오 콤보 | 03-02 |
| `Expanded/` | 「펼친 SQL」 분할 영역 VM·View(쿼리 탭 안, 03-10) | 03-02 · 03-10 |
| `Run/` | 실행·계획·복사 명령 처리기 | 03-02 |
| `Save/` | 저장 훅·바뀌는 부분 창·병합·감시·닫기 가드·저장 명령·상태 표시줄 구간 | 03-03 |
| `Combos/` | 조합 검사 패널·실행기·자동 고침·배지·명령 | 03-04 |
| `Conversion/` | 바꾸기 엔진·대화 상자·명령 | 03-05 |
| `LogPaste/` | 로그 붙여 넣기 MyBatis 분기·열린 매퍼 `IStatementLocator` | 03-06 |
| `Folder/` | 폴더 훑기·폴더 조각 해석기·트리 모드·찾기 명령 | 03-07 |

## 기능 Task 편집 지점

11장. TSK-01-02 가 실제 파일로 확정했다(2026-10-23). 기능 Task 는 「소유 파일」 열의 파일·폴더만 고치고, 「시험 파일」 열의 틀(TSK-01-02 가 Skip 시험 하나로 만들어 둠)을 채운다. 두 Task 가 한 파일을 나눠 갖지 않는다(아래 표의 경로는 서로 겹치지 않는다). 등록은 자동 수집(`[OrandaCommand]`·`[OrandaToolPanel]`·`[OrandaService]`)이라 공유 진입점에 줄을 더하지 않는다.

| Task | 소유 파일(쓰기) | 시험 파일 |
|------|-----------------|-----------|
| 02-01 | `src/Oranda.Language/MyBatis/MapperDocument*.cs`·`MapperParser*.cs`(새 파일, `IMyBatisMapperParser`·`IMyBatisMapperDocument` 구현), `src/Oranda.Platform/MyBatis/MapperFileWriter.cs`(새 파일, `IMapperFileWriter` 구현) | `tests/Oranda.Language.Tests/MyBatis/MapperDocumentTests.cs`(틀), 그 밖 `tests/Oranda.Language.Tests/MyBatis/MapperDocument*Tests.cs`·`tests/Oranda.Platform.Tests/MyBatis/**` 새 파일 |
| 02-02 | `src/Oranda.Language/MyBatis/MyBatisEvaluator*.cs`·`OgnlLite*.cs`·`ParameterExtractor*.cs`·`Combinations*.cs`(새 파일, `IMyBatisEvaluator` 구현), `src/Oranda.App/Features/SqlEditor/WorkspaceFiles.MyBatis.cs`(TSK-01-02 가 나눈 partial) | `tests/Oranda.Language.Tests/MyBatis/MyBatisEvaluatorTests.cs`(틀), `tests/Oranda.Language.Tests/MyBatis/{Ognl,Golden}*`·`MyBatis/Golden/**` 새 파일, `tests/Oranda.App.Tests/Features/SqlEditor/WorkspaceFilesTests.cs` 의 MyBatis 시험 2개만 |
| 02-03 | `src/Oranda.Platform/Storage/SqliteMyBatisScenarioStore.cs`(새 파일, `IMyBatisScenarioStore` 구현) | `tests/Oranda.Platform.Tests/Storage/MyBatisScenarioStoreTests.cs`(틀) |
| 03-01 | `src/Oranda.App/Features/MyBatisWorkspace/Statements/**`(TSK-01-02 의 `MyBatisStatementsViewModel`·`View`·`MyBatisStatementCommands.cs` 포함), `src/Oranda.App/Features/MyBatisWorkspace/Workspace/**`(열기 훅·세션·복구 구현), `src/Oranda.App/Features/MyBatisWorkspace/Editor/**`(`EvalDecorator*` 제외: 문장 편집기 View·View 팩토리·머리 줄·탭 배지·문서 도구 막대·`Highlighting/`) | `tests/Oranda.App.Tests/Features/MyBatisWorkspace/WorkspaceUiTests.cs`(틀) |
| 03-02 | `src/Oranda.App/Features/MyBatisWorkspace/Parameters/**`(바인드 창 동기화·계산 상태. 「파라미터 · 시나리오」 창 `MyBatisParametersViewModel`·`View` 는 지움), `src/Oranda.App/Features/MyBatisWorkspace/Expanded/**`(`MyBatisExpandedViewModel`·`View` 포함, 머리 줄 시나리오 콤보), `src/Oranda.App/Features/MyBatisWorkspace/Run/**`(`MyBatisRunCommands.cs` 포함), `src/Oranda.App/Features/MyBatisWorkspace/Editor/EvalDecorator*.cs`, `tests/Oranda.Oracle.IntegrationTests/MyBatis/MyBatisRun*`. 2026-10-23 조정자 허가 예외: `ExecutionController.cs`(`RunPreparedAsync` 하나), `ExecutionCommands.cs`(pasteBindValues Enablement), `DockWorkspace.ScopedTools.cs`·`LayoutPlanner.cs`(파라미터 창·바인드 창 목록), `Workspace/MyBatisOpenHandler.cs`(앞으로 가져올 창) | `tests/Oranda.App.Tests/Features/MyBatisWorkspace/ParamsRunTests.cs`(틀)와 `ParamsRun*Tests.cs`, 허가 예외: `DockMyBatisScopedToolsTests.cs`·`WorkspaceUiTests.cs`(기대값) |
| 03-03 | `src/Oranda.App/Features/MyBatisWorkspace/Save/**`(`MyBatisSaveCommands.cs` 포함: 저장 훅·바뀌는 부분 창·병합·감시·닫기 확인·감시 기준 복구 참여자·상태 표시줄 구간) | `tests/Oranda.App.Tests/Features/MyBatisWorkspace/SaveMergeTests.cs`(틀) |
| 03-04 | `src/Oranda.App/Features/MyBatisWorkspace/Combos/**`(`MyBatisCombosViewModel`·`View`·`MyBatisComboCommands.cs` 포함: 검사 실행기·자동 고침·`AutoFixDecorator*`·`StatementBadge*`), `tests/Oranda.Oracle.IntegrationTests/MyBatis/MyBatisCombo*` | `tests/Oranda.App.Tests/Features/MyBatisWorkspace/ComboCheckTests.cs`(틀) |
| 03-05 | `src/Oranda.App/Features/MyBatisWorkspace/Conversion/**`(`MyBatisConvertCommands.cs` 포함), `tests/Oranda.Oracle.IntegrationTests/MyBatis/MyBatisConvert*` | `tests/Oranda.App.Tests/Features/MyBatisWorkspace/ConvertTests.cs`(틀) |
| 03-06 | `src/Oranda.App/Features/MyBatisLog/**`, `src/Oranda.App/Features/MyBatisWorkspace/LogPaste/**`(열린 매퍼 `IStatementLocator` 구현 포함) | `tests/Oranda.App.Tests/Features/MyBatisLog/**`, `tests/Oranda.App.Tests/Features/MyBatisWorkspace/LogPasteTests.cs`(틀) |
| 03-07 | `src/Oranda.App/Features/MyBatisWorkspace/Folder/**`(`MyBatisFolderCommands.cs` 포함: 폴더 훑기·폴더 `IMyBatisFragmentResolver`·`IMyBatisStatementsViewMode` 트리·폴더 `IStatementLocator`·`mybatisFolderOpen` 키) | `tests/Oranda.App.Tests/Features/MyBatisWorkspace/MapperFolderTests.cs`(틀) |
| 04-01 | `tests/Oranda.App.Tests/Integration/MyBatisFlow*`, `tests/Oranda.Oracle.IntegrationTests/MyBatis/MyBatisFlow*`, `docs/oranda1/itest/TSK-04-01.md` | 같음 |

- TSK-01-02 전용(기능 Task 금지, 고칠 일이 있으면 조정자 승인):
  - 계약: `src/Oranda.Language/MyBatis/` 의 `MyBatisElement.cs`·`IMyBatisMapperDocument.cs`·`MapperEdit.cs`·`MapperWriteCheck.cs`·`EvalResult.cs`·`MyBatisParameter.cs`·`ConditionCombination.cs`·`IMyBatisEvaluator.cs`, `src/Oranda.Core/MyBatis/**`, `src/Oranda.App/Features/MyBatisWorkspace/Hooks/**`
  - 훅 자리: `src/Oranda.App/Features/SqlEditor/` 의 `SqlEditorHost*.cs`·`SqlWorkspaceViewModel.cs`·`SqlWorkspaceKind.cs`·`SqlWorkspaceView.axaml(.cs)`·`WorkspaceFiles.cs`(본체)·`DroppedWorkspaceFiles.cs`·`SqlEditorCommandHandler.cs`(MyBatis 분기·Enablement), `src/Oranda.App/Shell/Docking/DockWorkspace*.cs`·`DockTabMenu.cs`
  - 동결: `src/Oranda.App/Shell/Resources/CommandTitles*.json`, `src/Oranda.App/Shell/Keymaps/**`, `src/Oranda.App/Shell/Menus/MenuVisibility.cs`, `src/Oranda.App/Shell/Layout/LayoutPlanner.cs`, `docs/design/keymaps/*.json`, `docs/design/commands.md` 의 MyBatis 줄, `src/Oranda.Platform/Migrations/OrandaDb/0003_mybatis_scenario.sql`(열 추가 = 0004, 조정자 승인)
  - 공용 시험: `tests/Oranda.Testing/MyBatis/**`(표본 `MapperSamples`·`FakeMyBatisScenarioStore`), `tests/Oranda.App.Tests/Features/MyBatisWorkspace/Fakes/**`(`FakeMyBatisWorkspaceSession`·`FakeMapperDocument`), `tests/Oranda.App.Tests/Features/MyBatisWorkspace/MyBatisHookSitesTests.cs`·`MyBatisCommandEnablementTests.cs`, `tests/Oranda.App.Tests/Shell/Docking/DockMyBatisScopedToolsTests.cs`, `tests/Oranda.Platform.Tests/Storage/MyBatisScenarioMigrationTests.cs`
- 공유(자기 키만 정렬 위치에, 충돌은 합집합): `src/Oranda.App/Resources/literals-en.json`·`strings-ko.json`·`strings-en.json`.
- 패널 자리·처리기 파일(TSK-01-02 가 빈 몸으로 만든 VM·View·처리기)은 위 표대로 그 Task 소유가 된다. 특성 문자열(ID·Enablement·Placements·When)은 동결이고 몸만 채운다.
- 시험 틀은 `Placeholder` Skip 시험 하나다. 채울 때 틀 시험을 지우거나 바꿔도 된다.

## 12. 위험과 남은 판단

| # | 내용 | 대응 |
|---|------|------|
| R1 | WorkspaceFiles.cs 를 02-02·03-01 이 함께 고침 | partial 분할(10.1), 03-01 은 열기 훅으로만 |
| R2 | 명령·키·제목을 기능 6건이 고침 | 4.1 전체 목록을 01-02 가 미리 등록·동결 |
| R3 | 편집기 꾸밈 3건 | `IMyBatisEditorDecorator` Task 별 파일 |
| R4 | 03-07 이 03-06 코드를 고침 | `IStatementLocator` 를 01-02 로 옮김 |
| R5 | 「문장」 창 3건 | 배지 공급자·보기 모드 인터페이스 |
| R6 | 매퍼 쓰기 2건(03-03·03-05) | `IMapperFileWriter` 계약 01-02, 구현 02-01 |
| R7 | 03-01 의 펼치기가 계산기(02-02)를 씀, depends 에 없음 | `IMyBatisEvaluator` 로 짜고 시험은 가짜 계산기 |
| R8 | 02-02 가 02-01 을 기다림 | 9.1 원문 문자열만으로 만드는 `MyBatisElement` |
| R9 | 복구 2건(03-01·03-03) | `IMyBatisRecoveryParticipant` 여럿 |
| R10 | Duplicate, F9·Ctrl+Enter | B2 에 Duplicate, 4.2 표 |
| R11 | 시작 화면에 빈 MyBatis 창 | 3.2 「활성 문서 없음 = 숨김」, B10 렌더 점검 |
| R16 | 주 도구 막대·팔레트의 일반 실행이 xml 을 실행 | 4.4 Enablement `!mybatisDocActive` |
| R17 | 새 도구 창 등록이 등록 수·기본 배치 모양을 단정하는 기존 시험을 깰 수 있음 | 01-02 B11 범위(App.Tests 전체, Integration·Perf 제외) |
| R18 | 문장 탭을 다 닫은 매퍼의 고침이 닫기·저장·자동 저장에서 빠짐(호스트 흐름이 탭 기준) | 2.2 작업 공간·세션 기준 규칙, `IsDirty` 에 세션 dirty, 닫기·저장·자동 저장 분기(01-02) |
| R19 | 문장이 아닌 탭이 MyBatis 작업 공간에 섞임 | 2.2 일반 작업 공간으로 보내기(`SqlEditorHost.Tabs.cs`) |
| R20 | 훅이 복구보다 늦게 서면 복구된 매퍼가 세션 없이 열려 그 상태가 자동 저장으로 굳음 | 9장 훅 순서(Post 없이 바로), B6 시작 순서 시험 |
| R21 | 시작 때 `Oranda.Language` 로드(시작 시험 실패) | App 이 구현하는 계약은 Core·App 에, 생성자 제약, `WorkspaceFiles.MyBatis.cs` 에 Language static 금지. 세션처럼 시그니처에 Language 형이 든 App 인터페이스를 App 형이 구현할 때 시작 훑기가 Language 를 로드하는지는 B6 이 시작 시험으로 확인한다(App 에 임시 구현 1개를 넣고 확인 뒤 지움). 로드하면 그 멤버를 Core·App 형으로 바꾸고 design.md 를 고친다 |
| R22 | 0003 이 oranda.db 를 v3 으로 올리면 0003 이 없는 예전 빌드(다른 브랜치·이전 설치본)는 oranda.db 를 읽기 전용으로 연다(`OrandaDatabase` 미래 버전 규칙) | 개발 PC 에서 브랜치를 오갈 때 알려진 영향. 01-02 머지 요청에 적어 조정자·사용자에게 알림 |
| R23 | 내 저장이 파일 감시에 걸려 「밖에서 바뀜」이 뜸 | 5.3 디스크 해시 = 기준 해시면 무시 |
| R24 | 01-02 머지 뒤 기능 레인 머지 전까지 아무것도 하지 않는 메뉴 항목(「매퍼 폴더 열기…」·「MyBatis 작업 공간으로 바꾸기…」·문서 탭 우클릭·팔레트) | 보이되 누를 수 없게 둔다(2026-10-23 조정자 답): Enablement 에 준비 키 `mybatisFolderReady`·`mybatisConvertReady`(4.3), 03-07·03-05 가 켬. 나머지 MyBatis 명령은 `mybatisDocActive` 가 03-01 전까지 거짓이라 이미 꺼짐 |
| R25 | `Convert` 폴더 이름이 `System.Convert` 를 가림 | 폴더·이름공간을 `Conversion` 으로 |

## 13. 결정 초안 (decisions.md 에 조정자가 덧붙임)

- D-002 MyBatis 문서 판정 = 세션이 붙은 작업 공간(`DocumentKind`). 지금의 읽기 전용 .xml 펼침은 MyBatis 문서가 아니다(01-02 머지 뒤 동작 불변).
- D-003 Ctrl+S 는 `file.save` 그대로, 저장 훅이 MyBatis 저장으로 넘긴다. F9 만 `mybatisDocFocus` 로 키맵에 넣는다(키 충돌 규칙 유지, 2026-10-23 조정자 답).
- D-004 MyBatis 문서에서 일반 SQL 실행 명령은 Enablement `!mybatisDocActive` 로 끈다(01-02 가 특성 문자열만 고침).
- D-005 MyBatis 복구 상태는 Core `Workspace` 를 바꾸지 않고 곁 파일 `{id}.mybatis-{Key}.json`(참여자마다 하나)에 둔다.
- D-006 `IStatementLocator` 는 01-02 가 정의한다(03-07 이 03-06 을 기다리지 않음).
- D-007 ReadMyBatis 는 남기고(Q8 (a)) 몸을 계산기로 바꾼다. 표기 변환은 얇은 어댑터.
- D-008 `IMyBatisFragmentResolver` 는 Core 에 두고 `MyBatisFragment`(Core 형)를 돌려준다(wbs 01-02 의 「Language」에서 위치 변경). App(03-07)이 구현하므로 Language 에 두면 시작 때 Language 가 로드된다.
- D-009 MyBatis 작업 공간에는 문장 탭만 둔다. 저장 안 함 판정·닫기 확인·Ctrl+S·자동 저장은 탭이 아니라 작업 공간 세션 기준이다.
