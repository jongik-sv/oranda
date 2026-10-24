# 명령 체계 설계 — TSK-02-02

> 근거: PRD 6.12(APP-04·08), EDIT-10, 7.5 / TRD 4.11. 짝 문서: [ui-shell.md](ui-shell.md), [keymaps/](keymaps/).
> 이 문서를 계약으로 쓰는 Task: TSK-02-05(레지스트리·키맵 로더 틀), TSK-03-01(메뉴·툴바·팔레트), TSK-04-07(프리셋 전환·사용자 정의), 명령을 가진 기능 Task 전부.

## 1. 명령 ID 규칙

| 규칙 | 내용 | 예 |
|---|---|---|
| 형식 | `oranda.<영역>.<동작>` — 소문자 영역, 동작은 lowerCamelCase. 하위 묶음이 필요하면 점을 하나 더(`<영역>.<묶음>.<동작>`) | `oranda.exec.runStatement`, `oranda.grid.edit.apply` |
| 영역 | `file` `conn` `edit` `nav` `format` `exec` `txn` `plan` `sqlplus` `grid` `view` `schema` `db` `plsql` `monitor` `tools` `window` `help` | |
| 동작 | 동사로 시작(`run`, `toggle`, `open`, `show`, `copy`…). 켜기/끄기 상태 명령은 `toggle…` 하나로 | `oranda.txn.toggleAutoCommit` |
| 도구 창 | 도구 창 ID `oranda.panel.<이름>`, 그것을 여는 명령 `oranda.view.<이름>` | `oranda.panel.history` ↔ `oranda.view.history` |
| 안정성 | ID 는 키맵·사용자 설정 파일에 저장되므로 **한 번 내보낸 ID 는 바꾸지 않는다**. 바꿔야 하면 옛 ID 를 별칭으로 남긴다(레지스트리 `Aliases`) | |
| 확장 | 1.0 이후 확장(AI 패널 등)은 `oranda.ext.<확장 이름>.<동작>` | `oranda.ext.aiChat.explainPlan` |
| 표시 이름 | 제목은 리소스 키(`Cmd.<ID>`)로 두고 한/영 번역(TSK-10-02). 팔레트에서는 「범주: 제목」으로 보인다 | `SQL: 현재 문장 실행` |

## 2. 명령 정의 항목

| 항목 | 뜻 | 누가 정하나 |
|---|---|---|
| `Id` | 위 규칙의 ID | 기능 Task |
| `Title` | 리소스 키 | 기능 Task |
| `Category` | 아래 3장 범주(= 메뉴 묶음, 팔레트 접두어) | 기능 Task |
| `Icon` | 벡터 아이콘 키(없으면 메뉴에 아이콘 없음) | 기능 Task |
| `Enablement` | 활성 조건식(아래 2.2 문맥 키). 거짓이면 메뉴·툴바 흐리게, 팔레트에서 회색, 단축키 무시 | 기능 Task |
| `Placements` | 메뉴·툴바·오른쪽 클릭 메뉴 위치 목록(`menu:sql/run@10`, `toolbar:main/run@10`, `context:grid/copy@30`) | 기능 Task |
| `ShowInPalette` | 팔레트 노출(기본 true) | 기능 Task |
| 기본 단축키 | **명령 정의에 넣지 않는다.** 키는 키맵 파일(keymaps/*.json)에만 있다 | TSK-02-02(이 문서) → TSK-04-07 |

### 2.1 연결 방식 (한 ID 를 네 곳이 같이 쓴다)

```
① 명령 레지스트리 (ICommandRegistry)
   기능 Task 가 [OrandaCommand("oranda.exec.runStatement", ...)] 특성으로 처리기 등록
   → 시작 때 어셈블리 스캔으로 자동 수집 (셸 파일 수정 없음, TSK-02-05)
        │  Id·Title·Category·Icon·Enablement·Placements
        ▼
② 메뉴·툴바·오른쪽 클릭 메뉴
   셸이 Placements 를 모아 위치·순서(@n)대로 메뉴 트리를 만든다.
   같은 묶음 안 순서 = @n, 묶음 사이 구분선 자동.
        │
        ▼
③ 키맵 (IKeymapService)
   프리셋 JSON(keymaps/<id>.json) 위에 사용자 키맵({data}/keymaps/{id}.json, basedOn + 차이)을 겹친다.
   키 → (when 이 맞는 바인딩) → 명령 ID → 레지스트리에서 실행.
   메뉴 항목 오른쪽 키 표시(InputGesture)는 키맵에서 거꾸로 찾아 넣는다.
        │
        ▼
④ 명령 팔레트 (Ctrl+Shift+P)
   레지스트리 전체(ShowInPalette)를 「범주: 제목」+ ID 로 검색, 현재 키 표시,
   Enablement 거짓이면 회색(실행 불가). 최근 실행 순 우선.
```

- 실행 경로는 하나: 메뉴 클릭·툴바·단축키·팔레트 모두 `ICommandRegistry.ExecuteAsync(id, 문맥)` 를 부른다. 그래서 「단축키로는 되는데 메뉴로는 안 됨」 같은 차이가 생기지 않는다.
- 키맵에 없는 명령도 메뉴·팔레트로 항상 실행할 수 있다(7.5 키보드 접근 = 메뉴 접근 키 + 팔레트).
- 키맵에 있는 ID 가 레지스트리에 없으면 그 바인딩은 무시하고 로그에 경고 1줄(확장을 지운 경우 등).

### 2.2 문맥 키

`when`(키맵)과 `Enablement`(명령)에서 쓰는 이름. 식 문법: 이름, `!`, `&&`, `||`, 괄호. 셸이 포커스·선택이 바뀔 때 다시 계산한다.

**포커스 키** — 한 순간에 하나의 「포커스 자리」만 참이다. 키맵 `when` 에는 이것만 쓴다.

| 키 | 참일 때 | 포커스 자리 |
|---|---|---|
| (없음 = 전역) | 언제나(대화상자가 열려 있을 때 제외) | 전부 |
| `sqlEditorFocus` | SQL 편집기 글자 영역 | sqlText |
| `gridFocus` | SQL 편집기 문서의 결과 그리드 | sqlGrid |
| `sqlDocFocus` | 위 둘 중 하나 | sqlText, sqlGrid |
| `plsqlEditorFocus` | PL/SQL 편집기 글자 영역 | plsql |
| `mybatisDocFocus` | MyBatis 문장 편집기 글자 영역 | mybatisText |
| `editorFocus` | SQL 또는 PL/SQL 또는 MyBatis 문장 편집기 글자 영역 | sqlText, plsql, mybatisText |
| `schemaFocus` | 스키마 브라우저 | schema |
| `monitorFocus` | 세션·SQL 모니터 문서 | monitor |

**상태 키** — `Enablement` 에 쓴다.

| 키 | 뜻 |
|---|---|
| `hasDocument`, `sqlDocActive`, `plsqlDocActive`, `sqlDocOpen` | 문서 있음 / 활성 문서 종류 / SQL 편집기가 하나 이상 열림 |
| `sqlWorkspaceActive` | 보고 있는 SQL 작업 공간이 있음(SQL 탭이 없어도 참) |
| `hasSession`, `sessionBusy`, `sessionBroken`, `readOnlySession` | 활성 문서 접속 상태(연결됨·실행 중·끊김·읽기 전용 프로필). `hasSession`·`sessionBroken` 은 공유 작업 세션, `sessionBusy`·`readOnlySession` 은 활성 SQL 탭의 세션 기준 |
| `txnPending` | 미커밋 트랜잭션 있음(활성 SQL 탭의 세션 기준: 전용 세션 탭이면 그 세션) |
| `dedicatedSession` | 활성 SQL 탭이 전용 세션 탭이고 그 세션이 끊기지 않음(D-017). 공유 접속이 없어도 실행 명령을 켠다 |
| `hasSelection`, `canUndo`, `canRedo`, `hasFindText`, `hasBookmark`, `readOnlyDoc`, `canNavigateBack`, `canNavigateForward` | 편집기 상태 |
| `hasExecError`, `hasLastSqlId` | 마지막 실행 결과 |
| `gridHasResult`, `gridHasSelection`, `gridHasMoreRows`, `gridQueryResult`, `gridEditing`, `gridDirty`, `gridNotEditable`, `singleTable`, `joinResult` | 그리드 상태(`gridQueryResult` = 질의 문장이 만든 결과, PL/SQL REF CURSOR 결과 제외 — 전체 행 수 세기·결과 거르기가 쓴다) |
| `schemaNodeSelected`, `plsqlNodeSelected`, `recyclebinNodeSelected` | 스키마 트리 선택 |
| `isPackageDoc`, `serverSourceChanged`, `plsqlRunnable` | PL/SQL 편집기 |
| `monitorSessionOpen`, `monitorSessionSelected`, `monitorSqlOpen`, `monitorSqlSelected` | 모니터 |
| `mybatisDocActive`, `mybatisDocDirty`, `mybatisStatementSelected`, `mybatisFolderOpen`, `mybatisFolderReady`, `mybatisConvertReady` | MyBatis 작업 공간: 활성 문서가 MyBatis 종류 / 저장 안 한 문장이 있음 / 고른 문장 또는 활성 문장 탭이 있음 / 매퍼 폴더가 열려 있음 / 매퍼 폴더 기능 준비(TSK-03-07 이 켬) / 바꾸기 기능 준비(TSK-03-05 가 켬)(근거 `docs/oranda1/design.md` 4.3) |
| `canKillSession`, `canTraceSessions` | 서버 점검 결과(`ServerCapabilities`) |
| `devBuild` | 개발 빌드 |

### 2.3 단축키 충돌 규칙

1. 한 프리셋 안에서 **같은 키 두 바인딩의 포커스 자리가 겹치면 충돌**이다(전역은 모든 자리와 겹친다). 예: `F5` 는 `sqlDocFocus`(스크립트 실행)·`schemaFocus`(새로 고침)·`plsqlEditorFocus`(컴파일)·`monitorFocus`(새로 고침)로 나눠 쓴다 — 겹치지 않으므로 허용.
2. 같은 검사를 macOS 로 바꾼 키(5장)에도 한다.
3. 사용자 키맵에서 충돌이 생기면 단축키 설정 화면(TSK-04-07)이 경고하고, 저장된 파일에서는 **뒤에 적힌 바인딩**이 이긴다.
4. 프리셋 4종은 이 규칙으로 검사했다(충돌 0). TSK-02-05 키맵 로더 시험에 같은 검사를 넣는다.
5. 편집기 기본 키(방향키·Home/End·Tab·Shift+Tab·Backspace·Delete·Alt+드래그 열 선택·Alt+클릭 커서 추가)는 AvaloniaEdit·다중 커서 확장(D-007) 몫이며 키맵에 넣지 않는다. 그리드 기본 키(방향키·PageUp/Down·F2 셀 편집·Esc)도 같다. 접속 관리자 대화상자 목록의 키(Enter·두 번 누르기 접속, Delete 삭제, F2 이름 칸, Esc 닫기, CONN-14)도 대화상자 몫이라 명령으로 등록하지 않고 키맵에 넣지 않는다.
6. 시스템이 쓰는 키는 바인딩하지 않는다: Windows `Alt+F4`, `Alt+Space`, `Ctrl+Esc`, `Win+…`, 메뉴 접근 `Alt+문자`(Orange 5 `Alt+C`·`Alt+B` 만 예외 — 메뉴 접근 키 C·B 없음), macOS 5.2 목록.
7. 스키마 브라우저 트리의 객체 열기 키(Enter·두 번 누르기 = 고정 문서로 열기, 방향키로 고르고 멈추기 = 미리보기, SCH-01)는 트리 보기 몫이라 명령으로 등록하지 않고 키맵에 넣지 않는다. 그래서 `schemaFocus` 에는 Enter 를 바인딩하지 않는다.

### 2.4 주 메뉴의 숨김과 회색 이유 (TSK-13-36)

주 메뉴 막대(창 안 메뉴·macOS 시스템 메뉴)만 쓰는 규칙이다. 오른쪽 클릭 메뉴·툴바·명령 팔레트는 지금 규칙 그대로다(팔레트는 회색으로 모두 보임).

| 경우 | 주 메뉴 |
|---|---|
| 명령의 `When` 이 가리키는 탭·패널이 지금 없음 | 숨김 |
| `When` 은 맞고 `Enablement` 가 거짓 | 회색. 마우스를 올리면 이유 툴팁 |
| 둘 다 참 | 보임 |

- `When` 은 키맵 기본 키 범위(포커스 키)라 「마지막 포커스」로 보지 않고 「그 탭·패널이 열려 있음」으로 바꿔 본다(`MenuVisibility.TabScopes`): `sqlEditorFocus`·`sqlDocFocus`·`gridFocus` → `sqlDocActive`, `editorFocus` → `sqlDocActive || plsqlDocActive || plsqlEditorFocus`, `plsqlEditorFocus` → `plsqlDocActive || plsqlEditorFocus`, `monitorFocus` → `monitorSessionOpen || monitorSqlOpen`, `schemaFocus` → 언제나(늘 있는 도구 창).
- MyBatis 문서의 대응(`docs/oranda1/design.md` 4.3): `mybatisDocFocus` → `mybatisDocActive`, `editorFocus` 는 위 식 뒤에 `|| mybatisDocActive`, `sqlEditorFocus`·`sqlDocFocus` 는 `sqlDocActive && !mybatisDocActive`(`gridFocus` 는 그대로).
- 숨긴 뒤 구분선이 맨 앞·끝에 남거나 두 줄 겹치지 않게 하고, 항목이 다 숨은 하위 메뉴도 숨긴다. 최상위 메뉴가 통째로 비면 메뉴는 남기고 회색 안내 한 줄(`Menu.<메뉴>.empty`, 없으면 `Menu.empty`)을 보인다.
- 이유 문구 = `Enablement` 식에서 거짓을 만든 첫 키의 사람 말. `&&` 는 거짓인 항을 앞에서부터, `||` 는 문구가 있는 첫 항, `!x` 가 거짓이면 `not.x`. 문구 표는 `strings-ko/en.json` 의 `Menu.disabled.<키>`·`Menu.disabled.not.<키>` 한 곳이고, 표에 없는 키는 툴팁을 달지 않는다.
- 문맥 키가 바뀔 때마다(탭 전환·접속·끊기·행 선택) 활성 상태와 함께 다시 계산한다(`MenuControls.RefreshEnablement`).

## 3. 범주 = 메뉴 묶음

| 범주 | 메뉴 | 오른쪽 클릭 메뉴 |
|---|---|---|
| 파일, 접속 | 파일 | 문서 탭 머리 |
| 편집, 탐색, 포맷 | 편집 | 편집기 |
| SQL | SQL | 편집기(실행 묶음), 실행 계획 트리(`context:plan` — 고정·비교) |
| 결과 | 결과 | 그리드 |
| 보기 | 보기 | — |
| 데이터베이스 | 데이터베이스 | 스키마 트리, PL/SQL 편집기 |
| 모니터 | 모니터 | 모니터 목록 |
| 도구 | 도구 | — |
| 창 | 창 | 문서 탭 머리 |
| 도움말 | 도움말 | — |

툴바(기본, ui-shell.md 1장): `conn.manage` · `file.newSqlEditor` `file.open` `file.save` · `exec.runStatement` `exec.runScript` `exec.cancel` · `txn.commit` `txn.rollback` · `plan.explain` · `format.document` · `view.commandPalette`. 접속 콤보는 셸 고유 컨트롤(선택 시 `conn.switchSession`).

## 4. 키맵 파일 형식

파일 형식의 정본은 [data-model.md 2.4](data-model.md)(TSK-02-01)다. 이 문서는 그 형식으로 키 표 내용과 `when` 값을 정하고, 아래 `mac` 필드 하나를 더 쓴다.

```json
{
  "schemaVersion": 1,
  "id": "orange5",
  "name": "Orange 5",
  "basedOn": null,
  "bindings": [
    { "command": "oranda.exec.runStatement", "keys": ["Primary+Enter", "Primary+K"], "when": "sqlDocFocus" },
    { "command": "oranda.exec.runFromCursor", "keys": ["Primary+M"], "when": "sqlDocFocus", "mac": ["Primary+Shift+M"] }
  ]
}
```

| 필드 | 필수 | 뜻 |
|---|---|---|
| `schemaVersion` | 예 | 1 |
| `id` | 예 | 프리셋 `default`, `orange5`, `plsqldev`, `sqldev` (= 파일 이름) |
| `name` | 예 | 표시 이름 |
| `basedOn` | 아니요 | 프리셋은 `null`. 사용자 키맵은 바탕 프리셋 id + 차이만 |
| `bindings[].command` | 예 | 명령 ID |
| `bindings[].keys` | 예 | 키 조합 목록(data-model 2.4 규칙: 수식키 `Primary+Ctrl+Alt+Shift+Meta+키`, 키는 Avalonia `Key` 이름). `[]` = 바탕 키 지움 |
| `bindings[].when` | 아니요 | 포커스 키(2.2). 없으면 전역 |
| `bindings[].mac` | 아니요 | **이 문서가 더하는 필드.** macOS 에서 `keys` 대신 쓰는 키 목록(같은 표기, `Primary`=Cmd, `Ctrl`=실제 Control, `Alt`=Option). 없으면 `keys` 를 그대로 쓴다 |

- 한 바인딩 줄 = (명령, `when`) 하나. 같은 명령이 다른 `when` 에서 키를 가지면 줄이 둘이다.
- 프리셋 파일에서 `Ctrl` 은 쓰지 않는다(Windows 의 Ctrl 은 모두 `Primary`). `Ctrl` 은 `mac` 안에서 실제 Control 키를 뜻할 때만 쓴다.
- 이 문서의 표(5.3, 6.1, 7장)는 읽기 쉽게 Windows 표기(`Ctrl+Enter`, `Ctrl+-`)로 적는다. 파일 표기와의 대응: `Ctrl`→`Primary`, `-`→`OemMinus`, `=`→`OemPlus`, `/`→`Oem2`, `[`→`OemOpenBrackets`, `]`→`OemCloseBrackets`, `,`→`OemComma`, `.`→`OemPeriod`, `'`→`OemQuotes`, `0`~`9`→`D0`~`D9`, `Esc`→`Escape`, `Backspace`→`Back`, `Break`→`Cancel`(Ctrl+Break 가 만드는 키).
- 겹치기: 앱에 든 프리셋(임베디드 리소스, 읽기 전용) → 사용자 키맵(`{data}/keymaps/{id}.json`, `basedOn` + 차이). 프리셋을 바꿔도 사용자 차이는 남는다.
- 프리셋 4종은 연속 키(chord)를 쓰지 않는다(형식은 허용).

## 5. macOS 키 규칙

### 5.1 기본 대응 (`mac` 이 없을 때)

| 파일 표기 | Windows | macOS |
|---|---|---|
| `Primary` | Ctrl | Cmd |
| `Alt` | Alt | Option |
| `Shift` | Shift | Shift |
| 기능 키·글자·기호 | 그대로 | 그대로(기능 키는 맥 키보드에서 `fn` 과 함께 누를 수 있음 — 사용자 문서에 적는다) |

다음 경우는 프리셋 파일에 `mac` 을 반드시 적는다(키맵 로더 시험이 검사):
- `Cancel`(Ctrl+Break) — 맥에 없음 → 관례대로 `Cmd+.`
- `Alt+글자/숫자/기호` — 맥에서 `Option+문자` 는 특수 문자 입력이라 쓸 수 없음 → 실제 Control 등으로 지정
- 맥에서 그대로 쓰면 5.2 예약 키가 되는 경우

### 5.2 macOS 예약 키 (바인딩 금지)

`Cmd+Q` `Cmd+H` `Cmd+Option+H` `Cmd+M` `Cmd+Option+M` `Cmd+Tab` `Cmd+Space` `Ctrl+Space`(입력 소스 전환 — 한글 사용자 필수) ``Cmd+` `` `Cmd+Option+Esc` `Cmd+Shift+3/4/5` `Ctrl+방향키`(Mission Control) `F11`(데스크톱 보기) `Cmd+Ctrl+F` `Cmd+Ctrl+Q` `Cmd+Ctrl+Space`. `Cmd+,`(설정)·`Cmd+Q`(끝내기)·`Cmd+H`(가리기)는 앱 메뉴가 OS 관례대로 맡는다.

### 5.3 `mac` 을 따로 적은 바인딩 (프리셋 전체)

| 명령 ID | Windows 키 | macOS 키 |
|---|---|---|
| `oranda.edit.redo` | `Ctrl+Y` | `Cmd+Shift+Z` |
| `oranda.edit.replace` | `Ctrl+H` | `Cmd+Option+F` |
| `oranda.edit.findNext` | `F3` | `Cmd+G` |
| `oranda.edit.findPrevious` | `Shift+F3` | `Cmd+Shift+G` |
| `oranda.edit.goToLine` | `Ctrl+G` | `Cmd+L` |
| `oranda.edit.triggerCompletion` | `Ctrl+Space` | `Option+Esc` |
| `oranda.nav.back` | `Alt+Left` | `Cmd+Ctrl+Left` |
| `oranda.nav.forward` | `Alt+Right` | `Cmd+Ctrl+Right` |
| `oranda.exec.runToNewResultTab` | `F11` | `Cmd+Option+Enter` |
| `oranda.exec.cancel` | `Ctrl+Break` | `Cmd+.` |
| `oranda.grid.rowView` | `Alt+Enter` | `Cmd+Option+R` |
| `oranda.grid.edit.addRow` | `Alt+Insert` | `Cmd+Option+N` |
| `oranda.grid.edit.deleteRow` | `Alt+Delete` | `Cmd+Option+Backspace` |
| `oranda.view.schemaBrowser` | `Alt+1` | `Ctrl+1` |
| `oranda.view.output` | `Alt+2` | `Ctrl+2` |
| `oranda.view.messages` | `Alt+3` | `Ctrl+3` |
| `oranda.view.plan` | `Alt+4` | `Ctrl+4` |
| `oranda.view.history` | `Alt+5` | `Ctrl+5` |
| `oranda.window.nextDocument` | `Ctrl+Tab` | `Ctrl+Tab` |
| `oranda.window.previousDocument` | `Ctrl+Shift+Tab` | `Ctrl+Shift+Tab` |
| `oranda.edit.goToLine` | `Ctrl+G` | `Cmd+Option+L` |
| `oranda.nav.describeObject` | `F4`, `Alt+C` | `F4`, `Option+C`, `Ctrl+Option+C` |
| `oranda.exec.bindVariables` | `Alt+B` | `Ctrl+Option+B` |
| `oranda.exec.runFromCursor` | `Ctrl+M` | `Cmd+Shift+M` |
| `oranda.txn.commit` | `F11` | `Cmd+F11` |
| `oranda.exec.runForEdit` | `F9` | `Ctrl+Enter`, `F9` |

`edit.triggerCompletion` 을 `Option+Esc` 로 둔 이유: 맥 기본 `Ctrl+Space` 는 한/영 입력 소스 전환이다. 사용자 키맵으로 바꿀 수 있다.

`nav.describeObject`(`F4`·`Alt+C`)는 커서 아래 이름이 표·뷰면 테이블 레이아웃 창(Orange 의 Alt+C)을, 아니면 요약 팝업을 연다(TSK-13-32). 창에서는 행을 클릭·Shift(범위)·`Cmd`/`Ctrl`+클릭(개별)·`Cmd`/`Ctrl`+`A`(전체)로 고르고, `Enter`·「넣기」·두 번 누르기(누른 행이 선택 안이면 선택 전체)로 편집기에 넣고, `Cmd`/`Ctrl`+`C` 로 같은 글을 복사한다. 글은 늘 표 순서로 한 줄 「COL1, COL2, …」(별칭·소문자·쉼표 넣기 반영)이고, 주석 넣기를 켜면 열마다 한 줄 「COL1, -- 주석」에 끝에도 줄을 바꾸며 둘째 줄부터 편집기 커서 열에 맞춰 들인다. 맥에서는 `Option+C` 와 기존 `Ctrl+Option+C` 가 둘 다 열고, 같은 `editorFocus` 에서 겹치는 키가 없다(`Cmd+Option+C` = `grid.copyRows` 는 `gridFocus`).

`grid.followForeignKey`(`F4`)·`grid.showReferencingRows`(`Shift+F4`)는 `gridFocus` 에서만 쓴다 — `F4`·`Shift+F4` 의 다른 바인딩(`nav.describeObject`)은 `editorFocus` 라 포커스 자리가 겹치지 않는다. DataGrip 「Related Rows」(F4)를 따랐고, 4개 프리셋 모두 두 열(Windows·mac)이 같다.

`grid.copyRows`(`Ctrl+Alt+C`)·`grid.edit.duplicateRow`(`Ctrl+Shift+D`)·`grid.edit.pasteRows`(`Ctrl+Shift+V`)는 `gridFocus` 에서만 쓴다. 4개 프리셋 모두 그리드와 겹치는 자리(전역·`sqlDocFocus`·`gridFocus`)에서 비어 있던 키이고, 맥에서는 `mac` 없이 `Cmd+Option+C`·`Cmd+Shift+D`·`Cmd+Shift+V` 다(5.2 예약 키가 아니다). 「행 복제」·「행 붙여넣기」는 편집 모드가 아닐 때도 불러 「편집 모드로 다시 실행」 안내 막대를 띄우므로 활성 조건이 `gridEditing` 이 아니다.

`nav.quickOpenObject`(`Ctrl+P`)·`schema.reveal`(`Alt+F1`)은 `mac` 없이 맥에서 `Cmd+P`·`Option+F1` 이다. `Cmd+P` 는 관례상 인쇄지만 Oranda 에는 인쇄 명령이 없고 5.2 예약 키도 아니다(VS Code 「Quick Open」도 맥에서 `Cmd+P`). `Alt+F1` 은 DataGrip 「Select In」 키를 따랐다. 4개 프리셋 모두 두 열에서 비어 있던 키다.

## 6. 키맵 프리셋 4종

| 파일 | 프리셋 | 키 수 | 기준 |
|---|---|---|---|
| [keymaps/default.json](keymaps/default.json) | 기본 | 88 | Orange 5 사용자에게 익숙한 실행 키(F5·Ctrl+Enter·F9·F8·Ctrl+E·F11) + 일반 편집기 관례(Ctrl+F/H/G, Ctrl+/) |
| [keymaps/orange5.json](keymaps/orange5.json) | Orange 5 | 95 | `docs/orange5-features.md` 29장 통합 단축키 표 |
| [keymaps/plsqldev.json](keymaps/plsqldev.json) | PL/SQL Developer | 91 | PL/SQL Developer 기본 키(F8 실행, F5 계획, Shift+Esc 중단, F10/Shift+F10 커밋/롤백, Ctrl+E 이전 문장 불러오기) |
| [keymaps/sqldev.json](keymaps/sqldev.json) | SQL Developer | 90 | SQL Developer 기본 키(Ctrl+Enter/F9 문장, F5 스크립트, F10 계획, F6 Autotrace, F11/F12 커밋/롤백, Ctrl+F7 포맷, F8 이력, Shift+F4 설명, Ctrl+' 대소문자) |

- 각 파일은 **완전한 표**다(다른 프리셋을 상속하지 않음). 로더가 겹치기 계산을 하지 않아도 되고, 파일 하나만 보고 충돌을 검사할 수 있다.
- 커밋·롤백은 기본·Orange 5 프리셋에서 단축키를 주지 않았다(실수 방지, 툴바·메뉴·팔레트로 실행). PL/SQL Developer·SQL Developer 프리셋은 원래 도구의 키를 따른다.
- PL/SQL Developer 프리셋의 `F10`(커밋)은 Windows 의 「F10 = 메뉴 막대로」 동작을 덮는다. 원래 도구와 같은 동작이므로 그대로 둔다(메뉴는 `Alt` 로 연다).
- PL/SQL Developer·SQL Developer 키는 공개 문서·일반 기본값 기준이다. TSK-04-07 이 실제 제품과 한 번 더 대조해 고친다(아래 「확인 필요」).

### 6.1 프리셋 사이에 다른 키

같은 명령의 키가 프리셋마다 다른 것만 모았다. 나머지 명령은 4종 모두 기본 프리셋과 같다.

| 명령 ID | 기본 | Orange 5 | PL/SQL Developer | SQL Developer |
|---|---|---|---|---|
| `oranda.file.newSqlEditor` | `Ctrl+N` | `Ctrl+N`, `Ctrl+T` | `Ctrl+N` | `Alt+F10`, `Ctrl+N` |
| `oranda.file.saveAll` | `Ctrl+Shift+S` | - | `Ctrl+Shift+S` | `Ctrl+Shift+S` |
| `oranda.file.reopenClosed` | `Ctrl+Shift+T` | - | `Ctrl+Shift+T` | `Ctrl+Shift+T` |
| `oranda.file.duplicateTab` | - | `Ctrl+Shift+T` | - | - |
| `oranda.conn.switchSession` | - | `F7` | - | - |
| `oranda.edit.commentLines` | - | `Ctrl+-` | - | - |
| `oranda.edit.uncommentLines` | - | `Ctrl+Shift+-` | - | - |
| `oranda.edit.cycleCase` | - | - | - | `Ctrl+'` |
| `oranda.edit.duplicateLine` | `Ctrl+D` | `Ctrl+D` | `Ctrl+D` | - |
| `oranda.nav.goToDefinition` | `F12` | `F12` | `F12` | - |
| `oranda.nav.describeObject` | `F4`, `Alt+C` | `F4`, `Alt+C` | `F4`, `Alt+C` | `F4`, `Shift+F4`, `Alt+C` |
| `oranda.exec.bindVariables` | - | `Alt+B` | - | - |
| `oranda.format.document` | `Ctrl+Shift+F` | `Ctrl+Shift+F` | `Ctrl+Shift+F` | `Ctrl+F7` |
| `oranda.mybatis.formatStatement` | `Ctrl+Shift+F` | `Ctrl+Shift+F` | `Ctrl+Shift+F` | `Ctrl+F7` |
| `oranda.exec.runStatement` | `Ctrl+Enter` | `Ctrl+Enter`, `Ctrl+K` | `Ctrl+Enter` | `Ctrl+Enter`, `F9` |
| `oranda.exec.runSelection` | - | `Ctrl+L` | - | - |
| `oranda.exec.runScript` | `F5` | `F5` | `F8` | `F5` |
| `oranda.exec.runFromCursor` | `Alt+F5` | `Ctrl+M` | `Alt+F5` | `Alt+F5` |
| `oranda.exec.runStep` | `Ctrl+Shift+Enter` | `Ctrl+J` | `Ctrl+Shift+Enter` | `Ctrl+Shift+Enter` |
| `oranda.exec.runToNewResultTab` | `F11` | `F11` | `F11` | - |
| `oranda.exec.runForEdit` | `F9` (macOS `Ctrl+Enter`, `F9`) | `F9` (macOS `Ctrl+Enter`, `F9`) | `F9` (macOS `Ctrl+Enter`, `F9`) | - |
| `oranda.exec.cancel` | `Ctrl+Break` | `Ctrl+Break` | `Ctrl+Break`, `Shift+Esc` | `Ctrl+Break` |
| `oranda.exec.toggleStatistics` | - | `Ctrl+Shift+S` | - | - |
| `oranda.txn.commit` | - | - | `F10` | `F11` |
| `oranda.txn.rollback` | - | - | `Shift+F10` | `F12` |
| `oranda.plan.explain` | `Ctrl+E` | `Ctrl+E` | `F5` | `F10` |
| `oranda.plan.actual` | `Ctrl+Shift+E` | `Ctrl+Shift+E` | `Ctrl+Shift+E` | `F6` |
| `oranda.view.history` | `Alt+5`, `F8` | `Alt+5`, `F8` | `Alt+5`, `Ctrl+E` | `Alt+5`, `F8` |
| `oranda.view.zoomOut` | `Ctrl+-` | - | `Ctrl+-` | `Ctrl+-` |
| `oranda.plsql.compile` | `F5` | `F5` | `F8` | `Ctrl+Shift+F9` |

### 6.2 확인 필요 (TSK-04-07)

| 프리셋 | 항목 |
|---|---|
| Orange 5 | 도움말 문서 불일치: Named Script 가 `F9`(결과 수정 모드와 충돌)로도 적혀 있음 → `Ctrl+R` 만 채택. 탭 닫기·전체 탭 실행 키는 문서에 없어 기본값 유지 |
| PL/SQL Developer | 편집기 「현재 문장 실행」 키, 대소문자 변환·주석 키, 찾기/바꾸기 키(`Ctrl+R` 여부) |
| SQL Developer | 실행 취소 키, PL/SQL 실행 키, `Ctrl+D`(편집기 지우기) — 지금은 줄 복제 바인딩만 빼 두었다 |

## 7. 1.0 명령 목록

- 아래 표가 1.0 의 **명령 ID 전체**다(219개). 기본 키는 `default.json` 기준, 다른 프리셋은 6.1.
- 「키 범위(when)」는 키맵 `when`, 「활성 조건」은 명령 `Enablement` 다.
- 소유 Task 가 처리기·제목·아이콘·Placements 를 만든다. 셸(TSK-03-01)은 이 표를 하드코딩하지 않는다 — 레지스트리 자동 수집만 한다.
- 새 명령이 필요하면 기능 Task 가 규칙(1장)대로 ID 를 만들고 이 표에 행을 더하는 요청을 낸다. 키를 주려면 4개 프리셋 모두 충돌 검사를 통과해야 한다.

### 파일

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.file.newSqlEditor` | 새 SQL 편집기 | `Ctrl+N` | 전역 | 항상 | TSK-04-02 |
| `oranda.file.newPlsqlEditor` | 새 PL/SQL 편집기 | `Ctrl+Shift+N` | 전역 | 항상 | TSK-08-01 |
| `oranda.file.newDedicatedSqlEditor` | 새 SQL 탭(전용 세션) | - | 전역 | `hasSession` | TSK-06-02 |
| `oranda.file.open` | 파일 열기 | `Ctrl+O` | 전역 | 항상 | TSK-04-02 |
| `oranda.file.save` | 저장 | `Ctrl+S` | 전역 | `hasDocument` | TSK-04-02 |
| `oranda.file.saveAs` | 다른 이름으로 저장 | - | 전역 | `hasDocument && !mybatisDocActive` | TSK-04-02 |
| `oranda.file.saveAll` | 모두 저장 | `Ctrl+Shift+S` | 전역 | `hasDocument` | TSK-04-02 |
| `oranda.file.close` | 탭 닫기 | `Ctrl+W` | 전역 | `hasDocument` | TSK-03-01 |
| `oranda.file.closeAll` | 모든 탭 닫기 | - | 전역 | `hasDocument` | TSK-03-01 |
| `oranda.file.reopenClosed` | 닫은 탭 다시 열기 | `Ctrl+Shift+T` | 전역 | 항상 | TSK-04-02 |
| `oranda.file.duplicateTab` | 탭 복사(새 탭에) | - | 전역 | `sqlDocActive` | TSK-04-02 |
| `oranda.file.saveWorkspace` | 작업 공간 저장(저장소) | - | 전역 | `hasDocument` | TSK-04-02 |
| `oranda.file.openWorkspace` | 작업 공간 열기(저장소) | - | 전역 | 항상 | TSK-04-02 |
| `oranda.file.renameTab` | 탭 이름 바꾸기 | - | 전역 | `sqlDocActive && !mybatisDocActive` | TSK-04-02 |
| `oranda.file.saveWorkspaceFile` | 작업 공간 저장(묶인 파일에 바로 씀, 없으면 다른 이름으로 저장) | - | 전역 | `hasDocument && !mybatisDocActive` | TSK-04-02 |
| `oranda.file.saveWorkspaceFileAs` | 작업 공간 다른 이름으로 저장… | - | 전역 | `hasDocument && !mybatisDocActive` | TSK-13-19 |
| `oranda.file.openWorkspaceFile` | 작업 공간 열기…(Markdown·MyBatis) | - | 전역 | 항상 | TSK-04-02 |
| `oranda.file.clearRecentFiles` | 목록 지우기(파일 > 최근 파일) | - | 전역 | 항상 | mybatis-ui-sync |
| `oranda.file.clearRecentWorkspaces` | 목록 지우기(파일 > 최근 작업 공간) | - | 전역 | 항상 | mybatis-ui-sync |
| `oranda.file.newWorkspace` | 새 작업 공간 | - | 전역 | 항상 | TSK-04-02 |
| `oranda.file.renameWorkspace` | 작업 공간 이름 바꾸기 | - | 전역 | `sqlWorkspaceActive` | TSK-04-02 |
| `oranda.file.closeWorkspace` | 작업 공간 닫기 | - | 전역 | `sqlWorkspaceActive` | TSK-04-02 |
| `oranda.file.exit` | 끝내기 | - | 전역 | 항상 | TSK-03-01 |

### 접속

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.conn.manage` | 접속 관리자 | `Ctrl+Shift+O` | 전역 | 항상 | TSK-03-04 |
| `oranda.conn.switchSession` | 편집기 접속 바꾸기 | - | 전역 | `sqlDocActive \|\| plsqlDocActive` | TSK-03-04 |
| `oranda.conn.disconnect` | 접속 끊기 | - | 전역 | `hasSession` | TSK-03-04 |
| `oranda.conn.reconnect` | 다시 접속 | - | 전역 | `sessionBroken` | TSK-03-04 |
| `oranda.conn.changePassword` | 비밀번호 변경 | - | 전역 | `hasSession` | TSK-03-04 |
| `oranda.conn.import` | 접속 가져오기 | - | 전역 | 항상 | TSK-03-05 |
| `oranda.conn.export` | 접속 내보내기 | - | 전역 | 항상 | TSK-13-13 |

### 편집

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.edit.undo` | 실행 취소 | `Ctrl+Z` | `editorFocus` | `canUndo` | TSK-04-02 |
| `oranda.edit.redo` | 다시 실행 | `Ctrl+Y` | `editorFocus` | `canRedo` | TSK-04-02 |
| `oranda.edit.cut` | 잘라내기 | `Ctrl+X` | `editorFocus` | `hasSelection` | TSK-04-02 |
| `oranda.edit.copy` | 복사 | `Ctrl+C` | `editorFocus` | `hasSelection` | TSK-04-02 |
| `oranda.edit.paste` | 붙여넣기 | `Ctrl+V` | `editorFocus` | 항상 | TSK-04-02 |
| `oranda.edit.pasteMyBatisLog` | MyBatis 로그 붙여 넣기 | - | `editorFocus` | `sqlDocActive` | TSK-13-03 |
| `oranda.edit.selectAll` | 모두 선택 | `Ctrl+A` | `editorFocus` | 항상 | TSK-04-02 |
| `oranda.edit.find` | 찾기 | `Ctrl+F` | `editorFocus` | 항상 | TSK-04-02 |
| `oranda.edit.replace` | 바꾸기 | `Ctrl+H` | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.findNext` | 다음 찾기 | `F3` | `editorFocus` | `hasFindText` | TSK-04-02 |
| `oranda.edit.findPrevious` | 이전 찾기 | `Shift+F3` | `editorFocus` | `hasFindText` | TSK-04-02 |
| `oranda.edit.goToLine` | 줄로 이동 | `Ctrl+G` | `editorFocus` | 항상 | TSK-04-02 |
| `oranda.edit.toggleLineComment` | 줄 주석 토글 | `Ctrl+/` | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.toggleBlockComment` | 블록 주석 토글 | `Ctrl+Shift+/` | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.commentLines` | 줄 주석 달기 | - | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.uncommentLines` | 줄 주석 풀기 | - | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.indent` | 들여쓰기 | - | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.outdent` | 내어쓰기 | - | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.upperCase` | 대문자로 | `Ctrl+Shift+U` | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.lowerCase` | 소문자로 | `Ctrl+U` | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.titleCase` | 단어 첫 글자 대문자 | - | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.sentenceCase` | 문장 첫 글자 대문자 | - | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.invertCase` | 대소문자 반전 | - | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.cycleCase` | 대소문자 순환 | - | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.duplicateLine` | 줄 복제 | `Ctrl+D` | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.deleteLine` | 줄 삭제 | `Ctrl+Shift+K` | `editorFocus` | `!readOnlyDoc` | TSK-04-02 |
| `oranda.edit.toggleBookmark` | 북마크 토글 | `Ctrl+F2` | `editorFocus` | 항상 | TSK-04-02 |
| `oranda.edit.nextBookmark` | 다음 북마크 | `F2` | `editorFocus` | `hasBookmark` | TSK-04-02 |
| `oranda.edit.previousBookmark` | 이전 북마크 | `Shift+F2` | `editorFocus` | `hasBookmark` | TSK-04-02 |
| `oranda.edit.fold` | 접기 | `Ctrl+Shift+[` | `editorFocus` | 항상 | TSK-04-02 |
| `oranda.edit.unfold` | 펼치기 | `Ctrl+Shift+]` | `editorFocus` | 항상 | TSK-04-02 |
| `oranda.edit.foldAll` | 모두 접기 | - | `editorFocus` | 항상 | TSK-04-02 |
| `oranda.edit.unfoldAll` | 모두 펼치기 | - | `editorFocus` | 항상 | TSK-04-02 |
| `oranda.edit.goToMatchingBracket` | 짝 괄호·BEGIN/END 로 이동 | `Ctrl+]` | `editorFocus` | 항상 | TSK-04-02 |
| `oranda.edit.addCursorAbove` | 위에 커서 추가 | `Ctrl+Alt+Up` | `editorFocus` | 항상 | TSK-04-02 |
| `oranda.edit.addCursorBelow` | 아래에 커서 추가 | `Ctrl+Alt+Down` | `editorFocus` | 항상 | TSK-04-02 |
| `oranda.edit.triggerCompletion` | 자동완성 열기 | `Ctrl+Space` | `editorFocus` | 항상 | TSK-04-05 |
| `oranda.edit.parameterHints` | 인자 도움말 | `Ctrl+Shift+Space` | `editorFocus` | `hasSession` | TSK-04-05 |
| `oranda.edit.insertSnippet` | 코드 조각 삽입 | `Ctrl+Shift+J` | `editorFocus` | `!readOnlyDoc` | TSK-04-07 |

### 탐색

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.nav.goToDefinition` | 정의로 이동 | `F12` | `editorFocus` | `hasSession` | TSK-04-06 |
| `oranda.nav.describeObject` | 객체 정보 보기 | `F4`, `Alt+C` | `editorFocus` | `hasSession` | TSK-04-06, TSK-13-32 |
| `oranda.nav.findReferences` | 참조 찾기(현재 파일) | `Shift+F12` | `editorFocus` | 항상 | TSK-04-06 |
| `oranda.nav.back` | 뒤로 이동 | `Alt+Left` | `editorFocus` | `canNavigateBack` | TSK-04-06 |
| `oranda.nav.forward` | 앞으로 이동 | `Alt+Right` | `editorFocus` | `canNavigateForward` | TSK-04-06 |
| `oranda.nav.documentOutline` | 문서 개요로 이동 | - | `editorFocus` | 항상 | TSK-04-02 |
| `oranda.nav.quickOpenObject` | DB 객체로 이동 | `Ctrl+P` | 전역 | `hasSession` | TSK-04-06 |
| `oranda.schema.reveal` | 스키마 브라우저에서 찾기 | `Alt+F1` | 전역 | `hasSession` | TSK-04-06 |

### 포맷

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.format.document` | 포맷(선택 영역 또는 문서) | `Ctrl+Shift+F` | `editorFocus` | `!readOnlyDoc && !mybatisDocActive` | TSK-05-03 |
| `oranda.format.statement` | 현재 문장 포맷 | - | `editorFocus` | `!readOnlyDoc && !mybatisDocActive` | TSK-05-03 |
| `oranda.format.profiles` | 포매터 프로필 | - | 전역 | 항상 | TSK-05-03 |

### SQL

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.exec.runStatement` | 현재 문장 실행(선택 시 선택 영역) | `Ctrl+Enter` | `sqlDocFocus` | `(hasSession \|\| dedicatedSession) && !sessionBusy && !mybatisDocActive` | TSK-06-02 |
| `oranda.exec.runSubquery` | 커서의 하위 쿼리 실행 | `Alt+Shift+Enter` | `sqlDocFocus` | `(hasSession \|\| dedicatedSession) && !sessionBusy && !mybatisDocActive` | TSK-06-02 |
| `oranda.exec.runSelection` | 선택 영역 실행 | - | `sqlDocFocus` | `(hasSession \|\| dedicatedSession) && !sessionBusy && hasSelection && !mybatisDocActive` | TSK-06-02 |
| `oranda.exec.runScript` | 스크립트 전체 실행 | `F5` | `sqlDocFocus` | `(hasSession \|\| dedicatedSession) && !sessionBusy && !mybatisDocActive` | TSK-06-02 |
| `oranda.exec.runFromCursor` | 커서 문장부터 끝까지 실행 | `Alt+F5` | `sqlDocFocus` | `(hasSession \|\| dedicatedSession) && !sessionBusy && !mybatisDocActive` | TSK-06-02 |
| `oranda.exec.runStep` | 순차 실행(실행 후 다음 문장으로) | `Ctrl+Shift+Enter` | `sqlDocFocus` | `(hasSession \|\| dedicatedSession) && !sessionBusy && !mybatisDocActive` | TSK-06-02 |
| `oranda.exec.runToNewResultTab` | 새 결과 탭으로 실행 | `F11` | `sqlDocFocus` | `(hasSession \|\| dedicatedSession) && !sessionBusy && !mybatisDocActive` | TSK-06-02 |
| `oranda.exec.runAllTabs` | 모든 탭 실행 | - | 전역 | `(hasSession \|\| dedicatedSession) && !sessionBusy && !mybatisDocActive` | TSK-06-02 |
| `oranda.exec.runForEdit` | 결과 편집 모드로 실행 | `F9` (macOS `Ctrl+Enter`) | `sqlDocFocus` | `hasSession && !sessionBusy && !readOnlySession && !mybatisDocActive` | TSK-06-04 |
| `oranda.exec.cancel` | 실행 취소(서버 중단) | `Ctrl+Break` | `sqlDocFocus` | `sessionBusy` | TSK-06-02 |
| `oranda.exec.bindVariables` | 바인드 변수 창 | - | `sqlDocFocus` | `sqlDocActive` | TSK-06-02 |
| `oranda.exec.pasteBindValues` | 바인드 값을 편집기에 붙여 넣기 | - | `sqlDocFocus` | `sqlDocActive` | TSK-06-02 |
| `oranda.exec.toggleDbmsOutput` | DBMS_OUTPUT 수집 켜기/끄기 | - | 전역 | `sqlDocActive` | TSK-06-02 |
| `oranda.exec.errorModeAsk` | 스크립트 오류 시: 묻기 | - | 전역 | `sqlDocActive` | TSK-06-02 |
| `oranda.exec.errorModeStop` | 스크립트 오류 시: 중지 | - | 전역 | `sqlDocActive` | TSK-06-02 |
| `oranda.exec.errorModeContinue` | 스크립트 오류 시: 계속 | - | 전역 | `sqlDocActive` | TSK-06-02 |
| `oranda.exec.goToError` | 오류 문장으로 이동 | - | `sqlDocFocus` | `hasExecError` | TSK-06-02 |
| `oranda.exec.toggleBlankLineDelimiter` | 빈 줄로 문장 나누기 켜기/끄기 | - | 전역 | `sqlDocActive` | TSK-06-02 |
| `oranda.exec.toggleStatistics` | 실행 통계 수집 켜기/끄기 | - | 전역 | `sqlDocActive` | TSK-06-06 |
| `oranda.txn.commit` | 커밋 | - | 전역 | `(hasSession \|\| dedicatedSession) && txnPending && !sessionBusy` | TSK-06-02 |
| `oranda.txn.rollback` | 롤백 | - | 전역 | `(hasSession \|\| dedicatedSession) && txnPending && !sessionBusy` | TSK-06-02 |
| `oranda.txn.toggleAutoCommit` | 자동 커밋 켜기/끄기 | - | 전역 | `(hasSession \|\| dedicatedSession) && !readOnlySession` | TSK-06-02 |
| `oranda.plan.explain` | 예상 실행 계획 | `Ctrl+E` | `sqlDocFocus` | `(hasSession \|\| dedicatedSession) && !sessionBusy && !mybatisDocActive` | TSK-07-01 |
| `oranda.plan.actual` | 실제 실행 계획(마지막 실행) | `Ctrl+Shift+E` | `sqlDocFocus` | `(hasSession \|\| dedicatedSession) && hasLastSqlId && !mybatisDocActive` | TSK-07-01 |
| `oranda.plan.displayCursorOptions` | DISPLAY_CURSOR 형식 옵션 | - | 전역 | `hasSession` | TSK-07-01 |
| `oranda.plan.pinForCompare` | 이 계획 고정(비교 기준) | - | 전역 | 항상 | TSK-07-01 |
| `oranda.plan.compare` | 고정한 계획과 비교 | - | 전역 | 항상 | TSK-07-01 |
| `oranda.sqlplus.runFile` | @파일 실행 | - | `sqlDocFocus` | `hasSession && !sessionBusy && !mybatisDocActive` | TSK-06-06 |

### 결과

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.grid.copy` | 복사(TSV) | `Ctrl+C` | `gridFocus` | `gridHasSelection` | TSK-06-03 |
| `oranda.grid.copyWithHeaders` | 머리글 포함 복사 | `Ctrl+Shift+C` | `gridFocus` | `gridHasSelection` | TSK-06-03 |
| `oranda.grid.copyRows` | 행 복사 | `Ctrl+Alt+C` | `gridFocus` | `gridHasSelection` | TSK-06-03 |
| `oranda.grid.copyRowsWithHeaders` | 머리글 포함 행 복사 | - | `gridFocus` | `gridHasSelection` | TSK-06-03 |
| `oranda.grid.copyAsCsv` | CSV 로 복사 | - | `gridFocus` | `gridHasSelection` | TSK-06-03 |
| `oranda.grid.copyAsInsert` | INSERT 문으로 복사 | - | `gridFocus` | `gridHasSelection` | TSK-06-03 |
| `oranda.grid.copyAsInList` | IN 목록으로 복사 | - | `gridFocus` | `gridHasSelection` | TSK-06-03 |
| `oranda.grid.find` | 결과에서 찾기 | `Ctrl+F` | `gridFocus` | `gridHasResult` | TSK-06-03 |
| `oranda.grid.fetchAll` | 모두 가져오기 | `Alt+End` | `gridFocus` | `gridHasMoreRows` | TSK-06-03 |
| `oranda.grid.toggleTextMode` | 텍스트 결과 모드 | - | `gridFocus` | `gridHasResult` | TSK-06-03 |
| `oranda.grid.freezeColumns` | 열 고정 | - | `gridFocus` | `gridHasResult` | TSK-06-03 |
| `oranda.grid.hideColumn` | 열 숨기기 | - | `gridFocus` | `gridHasResult` | TSK-06-03 |
| `oranda.grid.showAllColumns` | 숨긴 열 모두 보이기 | - | `gridFocus` | `gridHasResult` | TSK-06-03 |
| `oranda.grid.toggleRepeatHide` | 같은 값 숨김 | - | `gridFocus` | `gridHasResult` | TSK-06-03 |
| `oranda.grid.closeResultTab` | 결과 탭 닫기 | - | `gridFocus` | `gridHasResult` | TSK-06-03 |
| `oranda.grid.countRows` | 전체 행 수 세기 | - | `gridFocus` | `gridQueryResult && hasSession && !sessionBusy` | TSK-06-03 |
| `oranda.grid.toggleFilterBar` | 결과 거르기 줄 | - | `gridFocus` | `gridQueryResult` | TSK-06-03 |
| `oranda.grid.toggleQuickFilter` | 결과 안에서 거르기 | - | `gridFocus` | `gridHasResult` | TSK-13-26 |
| `oranda.grid.clearLocalFilters` | 결과 안 거르기 모두 지우기 | - | `gridFocus` | `gridHasResult` | TSK-13-26 |
| `oranda.grid.filterByValue` | 이 값으로 거르기 | - | `gridFocus` | `gridQueryResult && gridHasSelection && hasSession && !sessionBusy` | TSK-06-03 |
| `oranda.grid.filterExcludeValue` | 이 값 빼고 거르기 | - | `gridFocus` | `gridQueryResult && gridHasSelection && hasSession && !sessionBusy` | TSK-06-03 |
| `oranda.grid.filterNullRows` | NULL 인 행만 | - | `gridFocus` | `gridQueryResult && gridHasSelection && hasSession && !sessionBusy` | TSK-06-03 |
| `oranda.grid.pinResultTab` | 결과 탭 고정 | - | `gridFocus` | `gridHasResult` | TSK-06-03 |
| `oranda.grid.followForeignKey` | 참조하는 행 열기(부모) | `F4` | `gridFocus` | `gridQueryResult && gridHasSelection && hasSession && !sessionBusy` | TSK-06-03 |
| `oranda.grid.showReferencingRows` | 이 행을 참조하는 행 열기(자식) | `Shift+F4` | `gridFocus` | `gridQueryResult && gridHasSelection && hasSession && !sessionBusy` | TSK-06-03 |
| `oranda.grid.rowView` | 행 보기 | `Alt+Enter` | `gridFocus` | `gridHasSelection` | TSK-06-05 |
| `oranda.grid.viewValue` | 값 보기(LOB 편집기) | - | `gridFocus` | `gridHasSelection` | TSK-06-05 |
| `oranda.grid.export` | 내보내기 | - | `gridFocus` | `gridHasResult` | TSK-06-07 |
| `oranda.grid.exportExcel` | Excel 로 내보내기… | `Ctrl+Alt+E` | `gridFocus` | `gridHasResult` | TSK-06-07 |
| `oranda.grid.flashback.asOf` | N분 전 값 보기… | - | `gridFocus` | `gridHasSelection` | TSK-13-05 |
| `oranda.grid.flashback.history` | 이 행 변경 이력 (활성 행의 전체 변경 이력 질의를 새 SQL 탭에 넣고 실행. 일반 실행 결과도 단일 표·PK 가 있으면 동작) | - | `gridFocus` | `gridHasSelection` | TSK-13-05, TSK-13-31, TSK-13-33 |
| `oranda.grid.flashback.restore` | 이전 값으로 되돌리기… | - | `gridFocus` | `gridHasSelection && !sessionBusy` | TSK-13-05 |
| `oranda.grid.edit.addRow` | 행 추가 | `Alt+Insert` | `gridFocus` | `gridEditing` | TSK-06-04 |
| `oranda.grid.edit.duplicateRow` | 행 복제 | `Ctrl+Shift+D` | `gridFocus` | `gridHasSelection` | TSK-06-04 |
| `oranda.grid.edit.pasteRows` | 행 붙여넣기 | `Ctrl+Shift+V` | `gridFocus` | `gridHasResult` | TSK-06-04 |
| `oranda.grid.edit.deleteRow` | 행 삭제 | `Alt+Delete` | `gridFocus` | `gridEditing` | TSK-06-04 |
| `oranda.grid.edit.previewDml` | 반영할 DML 미리보기 | - | `gridFocus` | `gridDirty` | TSK-06-04 |
| `oranda.grid.edit.applyAndCommit` | 반영 후 커밋 | - | `gridFocus` | `gridDirty && !sessionBusy` | TSK-06-04 |
| `oranda.grid.edit.apply` | 반영(커밋 안 함) | - | `gridFocus` | `gridDirty && !sessionBusy` | TSK-06-04 |
| `oranda.grid.edit.resetRow` | 행 되돌리기 | - | `gridFocus` | `gridDirty` | TSK-06-04 |
| `oranda.grid.edit.resetAll` | 모두 되돌리기 | - | `gridFocus` | `gridDirty` | TSK-06-04 |
| `oranda.grid.edit.addRowidAndRerun` | ROWID 추가 후 다시 실행 | - | `gridFocus` | `gridNotEditable && singleTable` | TSK-06-04 |
| `oranda.grid.edit.chooseTargetTable` | 편집 대상 테이블 고르기 | - | `gridFocus` | `gridEditing && joinResult` | TSK-06-04 |
| `oranda.grid.edit.toggleOptimisticLock` | 낙관적 잠금 켜기/끄기 | - | `gridFocus` | `gridEditing` | TSK-06-04 |

### 보기

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.view.commandPalette` | 명령 팔레트 | `Ctrl+Shift+P` | 전역 | 항상 | TSK-03-01 |
| `oranda.view.schemaBrowser` | 스키마 브라우저 | `Alt+1` | 전역 | 항상 | TSK-07-02 |
| `oranda.view.output` | DBMS 출력 | `Alt+2` | 전역 | 항상 | TSK-06-02 |
| `oranda.view.messages` | 메시지 | `Alt+3` | 전역 | 항상 | TSK-06-02 |
| `oranda.view.plan` | 실행 계획 | `Alt+4` | 전역 | 항상 | TSK-07-01 |
| `oranda.view.history` | 실행 이력 | `F8`, `Alt+5` | 전역 | 항상 | TSK-06-06 |
| `oranda.view.scripts` | 이름 붙인 스크립트 | `Ctrl+R` | 전역 | 항상 | TSK-06-06 |
| `oranda.view.outline` | 문서 개요 | - | 전역 | 항상 | TSK-04-02 |
| `oranda.view.compileErrors` | 컴파일 오류 | - | 전역 | 항상 | TSK-08-01 |
| `oranda.view.templates` | 템플릿 | - | 전역 | 항상 | TSK-04-07 |
| `oranda.view.changeJournal` | 변경 일지 | - | 전역 | 항상 | TSK-13-07 |
| `oranda.view.rowView` | 행 보기 창 | - | 전역 | 항상 | TSK-06-05 |
| `oranda.view.extensionTest` | 확장 시험(향후 AI 적용 대비) 패널 | - | 전역 | `devBuild` | TSK-10-03 |
| `oranda.view.resetLayout` | 레이아웃 초기화 | - | 전역 | 항상 | TSK-03-01 |
| `oranda.view.themeLight` | 밝은 테마 | - | 전역 | 항상 | TSK-03-01 |
| `oranda.view.themeDark` | 어두운 테마 | - | 전역 | 항상 | TSK-03-01 |
| `oranda.view.themeSystem` | OS 테마 따르기 | - | 전역 | 항상 | TSK-03-01 |
| `oranda.view.zoomIn` | 글자 크게 | `Ctrl+=` | 전역 | 항상 | TSK-03-01 |
| `oranda.view.zoomOut` | 글자 작게 | `Ctrl+-` | 전역 | 항상 | TSK-03-01 |
| `oranda.view.zoomReset` | 글자 크기 원래대로 | `Ctrl+0` | 전역 | 항상 | TSK-03-01 |

### 데이터베이스

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.schema.refresh` | 새로 고침 | `F5` | `schemaFocus` | `hasSession` | TSK-07-02 |
| `oranda.schema.filter` | 객체 필터 | `Ctrl+F` | `schemaFocus` | `hasSession` | TSK-07-02 |
| `oranda.schema.editFilters` | 스키마 브라우저 필터 편집… (저장 필터: 스키마·객체 유형·이름 패턴) | - | - | - | TSK-13-10 |
| `oranda.schema.toggleFavorite` | 즐겨찾기 추가/빼기 | - | `schemaFocus` | `schemaNodeSelected` | TSK-07-02 |
| `oranda.schema.newSqlWithSelect` | 새 SQL (표·뷰 우클릭: SELECT 를 채운 새 SQL 탭) | - | `schemaFocus` | `tableNodeSelected` | TSK-13-29 |
| `oranda.schema.insertName` | 편집기에 이름 넣기 | - | `schemaFocus` | `schemaNodeSelected && sqlDocOpen` | TSK-07-02 |
| `oranda.schema.insertSelect` | 편집기에 SELECT 문 넣기 | - | `schemaFocus` | `schemaNodeSelected && sqlDocOpen` | TSK-07-02 |
| `oranda.schema.insertColumns` | 편집기에 열 목록 넣기 | - | `schemaFocus` | `schemaNodeSelected && sqlDocOpen` | TSK-07-02 |
| `oranda.schema.openDetail` | 객체 상세 열기 | - | `schemaFocus` | `schemaNodeSelected` | TSK-07-03 |
| `oranda.schema.showDdl` | DDL 보기 | - | `schemaFocus` | `schemaNodeSelected` | TSK-07-03 |
| `oranda.schema.reorderColumns` | 열 순서 바꾸기… | - | `schemaFocus` | `schemaNodeSelected && !readOnlySession` | TSK-13-01 |
| `oranda.schema.compileInvalid` | INVALID 객체 일괄 컴파일 | - | 전역 | `hasSession && !readOnlySession` | TSK-07-04 |
| `oranda.schema.recyclebinPurge` | 휴지통 비우기(Purge) | - | `schemaFocus` | `recyclebinNodeSelected && !readOnlySession` | TSK-07-04 |
| `oranda.schema.recyclebinFlashback` | 휴지통에서 되살리기 | - | `schemaFocus` | `recyclebinNodeSelected && !readOnlySession` | TSK-07-04 |
| `oranda.schema.objectSearch` | 객체 검색 | - | 전역 | `hasSession` | TSK-07-04 |
| `oranda.db.info` | DB 정보 | - | 전역 | `hasSession` | TSK-07-04 |
| `oranda.db.ddlExtract` | DDL 일괄 추출 | - | 전역 | `hasSession` | TSK-07-04 |
| `oranda.schema.tableSpec` | 테이블 정의서… | - | `schemaFocus` | `tableNodeSelected && hasSession` | TSK-13-06 |
| `oranda.plsql.openSource` | PL/SQL 소스 열기 | - | `schemaFocus` | `plsqlNodeSelected` | TSK-08-01 |
| `oranda.journal.showForObject` | 변경 일지 보기 | - | `schemaFocus` | `schemaNodeSelected` | TSK-13-07 |
| `oranda.plsql.compile` | 컴파일(저장) | `F5` | `plsqlEditorFocus` | `hasSession && !sessionBusy && !readOnlySession` | TSK-08-01 |
| `oranda.plsql.toggleSpecBody` | 명세/본문 전환 | `Ctrl+Shift+B` | `plsqlEditorFocus` | `isPackageDoc` | TSK-08-01 |
| `oranda.plsql.showServerDiff` | 서버 소스와 차이 보기 | - | `plsqlEditorFocus` | `serverSourceChanged` | TSK-08-01 |
| `oranda.plsql.run` | PL/SQL 실행 대화상자 | `Ctrl+Enter` | `plsqlEditorFocus` | `hasSession && !sessionBusy && plsqlRunnable` | TSK-08-02 |
| `oranda.plsql.runFromBrowser` | 프로시저·함수 실행 | - | `schemaFocus` | `plsqlNodeSelected && hasSession` | TSK-08-02 |

### 모니터

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.monitor.sessions` | 세션 모니터 | - | 전역 | `hasSession` | TSK-09-01 |
| `oranda.monitor.refresh` | 모니터 새로 고침 | `F5` | `monitorFocus` | `monitorSessionOpen` | TSK-09-01 |
| `oranda.monitor.toggleAutoRefresh` | 자동 새로 고침 켜기/끄기 | - | `monitorFocus` | `monitorSessionOpen` | TSK-09-01 |
| `oranda.monitor.toggleDelta` | 델타 모드 | - | `monitorFocus` | `monitorSessionOpen` | TSK-09-01 |
| `oranda.monitor.blockingTree` | 블로킹 트리 | - | `monitorFocus` | `monitorSessionOpen` | TSK-09-01 |
| `oranda.monitor.sessionDetail` | 세션 상세 | - | `monitorFocus` | `monitorSessionSelected` | TSK-09-01 |
| `oranda.monitor.killSession` | 세션 Kill | - | `monitorFocus` | `monitorSessionSelected && canKillSession` | TSK-09-01 |
| `oranda.monitor.disconnectSession` | 세션 Disconnect | - | `monitorFocus` | `monitorSessionSelected && canKillSession` | TSK-09-01 |
| `oranda.monitor.traceOn` | SQL 트레이스 켜기 | - | `monitorFocus` | `monitorSessionSelected && canTraceSessions` | TSK-09-01 |
| `oranda.monitor.traceOff` | SQL 트레이스 끄기 | - | `monitorFocus` | `monitorSessionSelected && canTraceSessions` | TSK-09-01 |
| `oranda.monitor.sql` | SQL 모니터(Top SQL) | - | 전역 | `hasSession` | TSK-09-02 |
| `oranda.monitor.sqlSearch` | Top SQL 검색 | - | `monitorFocus` | `monitorSqlOpen` | TSK-09-02 |
| `oranda.monitor.sqlShowPlan` | 선택 SQL 실제 계획 | - | `monitorFocus` | `monitorSqlSelected` | TSK-09-02 |
| `oranda.monitor.sqlShowBinds` | 선택 SQL 바인드 캡처 | - | `monitorFocus` | `monitorSqlSelected` | TSK-09-02 |
| `oranda.monitor.locks` | Lock 모니터 | - | 전역 | `hasSession` | TSK-05-03 |
| `oranda.monitor.locks.refresh` | Lock 새로 고침 | `F5` | `lockMonitorFocus` | `lockMonitorOpen` | TSK-05-03 |
| `oranda.monitor.locks.toggleAutoRefresh` | Lock 자동 새로 고침 켜기/끄기 | - | `lockMonitorFocus` | `lockMonitorOpen` | TSK-05-03 |
| `oranda.monitor.locks.killHolder` | 보유자 세션 종료 | - | `lockMonitorFocus` | `lockSessionSelected && canKillSession` | TSK-05-03 |
| `oranda.monitor.locks.showSql` | 보유자·대기자 SQL 보기 | - | `lockMonitorFocus` | `lockSessionSelected` | TSK-05-03 |
| `oranda.monitor.locks.showLockedRow` | 잠긴 행 보기 | - | `lockMonitorFocus` | `lockWaiterSelected` | TSK-05-03 |
| `oranda.monitor.locks.showDdlLocks` | DDL 잠금 보기 | - | `lockMonitorFocus` | `lockMonitorOpen && canQueryDdlLocks` | TSK-05-03 |
| `oranda.monitor.locks.goToSession` | 세션 모니터에서 보기 | - | `lockMonitorFocus` | `lockSessionSelected` | TSK-05-03 |
| `oranda.monitor.locks.exportCsv` | Lock 목록 CSV 내보내기 | - | `lockMonitorFocus` | `lockMonitorOpen` | TSK-05-03 |
| `oranda.monitor.locks.showHistory` | Lock 이력 보기 | - | `lockMonitorFocus` | `lockMonitorOpen` | TSK-05-04 |

### 도구

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.tools.settings` | 설정 | `Ctrl+,` | 전역 | 항상 | TSK-03-01 |
| `oranda.tools.keymap` | 단축키 설정 | - | 전역 | 항상 | TSK-04-07 |
| `oranda.tools.keymapPreset.default` | 단축키 프리셋: 기본 | - | 전역 | 항상 | TSK-04-07 |
| `oranda.tools.keymapPreset.orange5` | 단축키 프리셋: Orange 5 | - | 전역 | 항상 | TSK-04-07 |
| `oranda.tools.keymapPreset.plsqldev` | 단축키 프리셋: PL/SQL Developer | - | 전역 | 항상 | TSK-04-07 |
| `oranda.tools.keymapPreset.sqldev` | 단축키 프리셋: SQL Developer | - | 전역 | 항상 | TSK-04-07 |
| `oranda.tools.exportSettings` | 설정 내보내기 | - | 전역 | 항상 | TSK-04-07 |
| `oranda.tools.importSettings` | 설정 가져오기 | - | 전역 | 항상 | TSK-04-07 |
| `oranda.tools.import` | 데이터 가져오기(CSV·xlsx) | - | 전역 | `hasSession && !readOnlySession` | TSK-06-07 |
| `oranda.tools.tableSpec` | 테이블 정의서… | - | 전역 | `hasSession` | TSK-13-06 |
| `oranda.tools.language` | UI 언어 | - | 전역 | 항상 | TSK-10-02 |

### 창

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.window.nextDocument` | 다음 탭 | `Ctrl+Tab` | 전역 | `hasDocument` | TSK-03-01 |
| `oranda.window.previousDocument` | 이전 탭 | `Ctrl+Shift+Tab` | 전역 | `hasDocument` | TSK-03-01 |
| `oranda.window.splitRight` | 오른쪽으로 분할 | - | 전역 | `hasDocument` | TSK-03-01 |
| `oranda.window.splitDown` | 아래로 분할 | - | 전역 | `hasDocument` | TSK-03-01 |
| `oranda.window.saveLayoutAs` | 레이아웃 이름 붙여 저장 | - | 전역 | 항상 | TSK-03-01 |
| `oranda.window.applyLayout` | 저장한 레이아웃 적용 | - | 전역 | 항상 | TSK-03-01 |
| `oranda.window.deleteLayout` | 저장한 레이아웃 지우기 | - | 전역 | 항상 | TSK-03-01 |
| `oranda.window.focusDocument` | 문서로 포커스 | - | 전역 | `hasDocument` | TSK-03-01 |
| `oranda.window.toggleResultPane` | 결과 영역 보이기/숨기기 | `Ctrl+Shift+R` | `sqlDocFocus` | `sqlDocActive` | TSK-06-02 |

### MyBatis

근거 `docs/oranda1/design.md` 4.1. 처리기 몸은 TSK-03-0x 가 채우고 특성 문자열·제목·키는 동결이다. 기본 키는 `runScenario`(`sqldev` 는 `Primary+Enter`, `F9`)와 `formatStatement` 다. `formatStatement` 는 일반 포맷(`format.document`)과 같은 키를 나눠 쓴다: 키맵에서 일반 포맷은 `sqlEditorFocus`·`plsqlEditorFocus` 두 항목으로 두고, MyBatis 쿼리 편집기(`mybatisDocFocus`)에서는 이 명령이 받는다.

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.mybatis.newStatement` | 새 문장… | - | 전역 | `mybatisDocActive` | TSK-03-01 |
| `oranda.mybatis.renameStatement` | 문장 id 바꾸기… | - | 전역 | `mybatisDocActive && mybatisStatementSelected` | TSK-03-01 |
| `oranda.mybatis.deleteStatement` | 문장 지우기 | - | 전역 | `mybatisDocActive && mybatisStatementSelected` | TSK-03-01 |
| `oranda.mybatis.expandToSqlTab` | 펼친 SQL 을 새 SQL 탭으로 | - | 전역 | `mybatisDocActive && mybatisStatementSelected` | TSK-03-01 |
| `oranda.mybatis.expandMapper` | 매퍼 전체를 SQL 탭으로 펼치기 | - | 전역 | `mybatisDocActive` | TSK-03-01 |
| `oranda.mybatis.runScenario` | 시나리오 값으로 실행 | `F9` | `mybatisDocFocus` | `mybatisDocActive && mybatisStatementSelected && (hasSession \|\| dedicatedSession \|\| tabConnectionMissing) && !sessionBusy` | TSK-03-02 |
| `oranda.mybatis.explainScenario` | 펼친 SQL 실행 계획 | - | 전역 | `mybatisDocActive && mybatisStatementSelected && (hasSession \|\| dedicatedSession \|\| tabConnectionMissing) && !sessionBusy` | TSK-03-02 |
| `oranda.mybatis.copyExpanded` | 펼친 SQL 복사 | - | 전역 | `mybatisDocActive && mybatisStatementSelected` | TSK-03-02 |
| `oranda.mybatis.copyExpandedWithValues` | 펼친 SQL 복사(값 넣은 글) | - | 전역 | `mybatisDocActive && mybatisStatementSelected` | TSK-03-02 |
| `oranda.mybatis.save` | xml 에 저장 | - (`Ctrl+S` 는 `file.save` 경유) | 전역 | `mybatisDocActive && mybatisDocDirty && !mybatisFormatAllBusy` | TSK-03-03 |
| `oranda.mybatis.checkCombinations` | 조합 검사 | - | 전역 | `mybatisDocActive && mybatisStatementSelected` | TSK-03-04 |
| `oranda.mybatis.applyAutoFix` | 자동 고침(<set>·<where> 로 감싸기) | - | 전역 | `mybatisDocActive && mybatisStatementSelected && !readOnlyDoc` | TSK-03-04 |
| `oranda.mybatis.convertWorkspace` | MyBatis 작업 공간으로 바꾸기… | - | 전역 | `sqlWorkspaceActive && !mybatisDocActive && mybatisConvertReady` | TSK-03-05 |
| `oranda.mybatis.openFolder` | 매퍼 폴더 열기… | - | 전역 | `mybatisFolderReady` | TSK-03-07 |
| `oranda.mybatis.findStatement` | MyBatis 문장 찾기… | - | 전역 | `mybatisDocActive \|\| mybatisFolderOpen` | TSK-03-07 |
| `oranda.mybatis.formatStatement` | 쿼리 포맷 | `Ctrl+Shift+F` | `mybatisDocFocus` | `mybatisDocActive && mybatisStatementSelected && !readOnlyDoc` | mybatis-ui-sync |
| `oranda.mybatis.formatAll` | 매퍼 전체 포맷 | - | 전역 | `mybatisDocActive && !mybatisFormatAllBusy` | mybatis-ui-sync |
| `oranda.mybatis.undoFormatAll` | 매퍼 전체 포맷 되돌리기 | - | 전역 | `mybatisDocActive && !mybatisFormatAllBusy` | mybatis-ui-sync |

### 도움말

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.help.docs` | 사용자 문서 | `F1` | 전역 | 항상 | TSK-10-01 |
| `oranda.help.shortcuts` | 단축키 목록 | - | 전역 | 항상 | TSK-10-01 |
| `oranda.help.checkUpdate` | 업데이트 확인… | - | 전역 | 항상 | TSK-10-01 |
| `oranda.help.crashReport` | 오류 보고서 | - | 전역 | 항상 | TSK-10-01 |
| `oranda.help.openLogs` | 로그 폴더 열기 | - | 전역 | 항상 | TSK-10-01 |
| `oranda.help.about` | Oranda 정보 | - | 전역 | 항상 | TSK-03-01 |

### 튜닝

- 메뉴 「튜닝」(id `tuning`)은 「모니터」 다음 최상위 메뉴다. 명령 범주는 옮기지 않는다(`Sql`·`Monitor` 그대로). TSK-06-01.
- 메뉴 묶음 순서(@숫자): 계획 10~(`explain`@10·`actualPlan`@20·`displayCursorOptions`@30·`pinPlan`@40·`comparePlan`@50) / SQL 모니터 100~(`sql`@100·`sqlSearch`@110·`sqlShowPlan`@120·`sqlShowBinds`@130) / 통계·자식 커서 200~(TSK-06-03) / 계획 보강 300~(TSK-06-04) / 도우미 400~(TSK-06-05) / 대기·이력 500~(TSK-06-05).
- `oranda.plan.explain`·`oranda.plan.actual` 은 SQL 메뉴에도 남는다(Placements 두 줄). 단축키·툴바·컨텍스트 배치는 그대로.
- 아래 ID 는 상수(`TuningCommands.Ids`)만 있고 처리기는 해당 Task 가 만든다. 활성 조건·키는 그 Task 가 정한다(키는 4개 프리셋 충돌 검사 후).

| ID | 제목 | 기본 키 | 키 범위(when) | 활성 조건 | 소유 Task |
|---|---|---|---|---|---|
| `oranda.tuning.stats` | 통계 보기 | - | 전역 | `hasSession` | TSK-06-03 |
| `oranda.tuning.gatherStatsScript` | 통계 수집 스크립트 만들기 | - | 전역 | 통계 보기 열림 | TSK-06-03 |
| `oranda.tuning.childCursors` | 자식 커서 | - | 전역 | `hasSession` | TSK-06-03 |
| `oranda.tuning.traceViewer` | 트레이스 보기 | - | 전역 | `hasSession` | TSK-06-04 |
| `oranda.tuning.optimizerTrace` | 옵티마이저 트레이스(10053) | - | `sqlDocFocus` | `hasSession && !sessionBusy` | TSK-06-04 |
| `oranda.tuning.assistant` | 튜닝 도우미 | - | 전역 | `hasSession` | TSK-06-05 |
| `oranda.tuning.indexCandidates` | 인덱스 후보 | - | 전역 | 도우미 열림 | TSK-06-05 |
| `oranda.tuning.rewriteCandidates` | 재작성 후보 | - | 전역 | 도우미 열림 | TSK-06-05 |
| `oranda.tuning.benchmark` | 변형 SQL 벤치마크 | - | 전역 | 도우미 열림 | TSK-06-05 |
| `oranda.tuning.awrHistory` | AWR 계획 이력 | - | 전역 | `hasSession` | TSK-06-05 |
| `oranda.tuning.ashWaits` | ASH 대기 분해 | - | 전역 | `hasSession` | TSK-06-05 |
| `oranda.tuning.planControls` | SQL Profile·Baseline·Patch 보기 | - | 전역 | `hasSession` | TSK-06-05 |

패널 ID: `oranda.panel.tuningStats`·`oranda.panel.childCursors`·`oranda.panel.traceViewer`·`oranda.panel.tuningAssistant`(모두 문서 패널).
