# 로컬 데이터 모델·설정 스키마 (TSK-02-01)

> 근거: TRD 4.12·5·6(ICredentialStore)·9, PRD CONN·FMT·EXEC-03/07·APP-01/08·SAFE-06·7.x(복구)·9.2·C-05, decisions D-004.
> 이 문서가 정본이다. TSK-02-03(Core 계약)은 6장 이름·형식·기본값을 그대로 C# record 로 옮긴다. TRD 5장 예제와 다른 곳은 9장에 적었다.

---

## 1. 저장 위치와 파일 목록

### 1.1 데이터 폴더(이하 `{data}`)

| OS | 경로 | 얻는 방법 |
|---|---|---|
| Windows | `%APPDATA%\Oranda` (Roaming) | `Environment.GetFolderPath(SpecialFolder.ApplicationData)` + `Oranda` |
| macOS | `~/Library/Application Support/Oranda` | `$HOME` + `Library/Application Support/Oranda` 로 **직접 조립**한다(.NET 의 `SpecialFolder` 값은 버전마다 macOS 매핑이 달라 쓰지 않는다) |
| 시험·휴대용 | 환경 변수 `ORANDA_DATA_DIR` 가 있으면 그 폴더 | 시험이 실제 사용자 폴더를 건드리지 않게 한다 |

폴더가 없으면 첫 실행 때 만든다. 경로 계산은 `Oranda.Platform` 의 `IAppDataPaths`(TSK-02-03 이 이름만 정의) 한 곳에서 한다.

### 1.2 파일 목록

| 경로 | 내용 | 형식 | C# 루트 형식 | 스키마 |
|---|---|---|---|---|
| `{data}/settings.json` | 앱 일반 설정(테마·이력 보존·자동 저장 등) | JSON | `AppSettings` | 2.1 |
| `{data}/connections.json` | 접속 프로필 목록 | JSON | `ConnectionsDocument` | 2.2 |
| `{data}/formatter/{id}.json` | 사용자 포매터 프로필(1 파일 = 1 프로필) | JSON | `FormatterProfile` | 2.3 |
| `{data}/keymaps/{id}.json` | 사용자 키맵(프리셋 위에 덮어쓰기) | JSON | `Keymap` | 2.4 |
| `{data}/layout.json` | 창 위치·도킹 배치 | JSON | `LayoutDocument` (App DTO) | 2.5 |
| `{data}/layouts/{id}.json` | 이름 붙인 레이아웃(창 → 레이아웃 이름 붙여 저장) | JSON | `LayoutDocument`(+`name`, 창 위치 없음) | 2.5 |
| `{data}/workspaces/{id}.json` | 작업 공간(탭 목록) | JSON | `Workspace` | 2.6 |
| `{data}/autosave/{workspaceId}.json` | 복구용 자동 저장(10 s 간격) | JSON | `Workspace` (같은 스키마) | 2.6 |
| `{data}/oranda.db` (+ `-wal`, `-shm`) | 실행 이력, 메타데이터 캐시, 변경 일지 | SQLite | — | 3 |
| `{data}/credentials/{hash}.bin` | **Windows 만**. DPAPI(현재 사용자)로 암호화한 비밀 | 바이너리 | — | 5 |
| `{data}/logs/` | Serilog 파일(7일 보관) | 텍스트 | — | 범위 밖 |
| `{data}/backup/` | 마이그레이션 전 백업 | 원본 그대로 | — | 4 |

- 기본 제공 포매터 프로필 3종·키맵 프리셋 4종은 **앱 안의 임베디드 리소스**다. `{data}` 에 복사하지 않는다. 사용자가 고치면 그때 `{data}/formatter/`·`{data}/keymaps/` 에 새 파일로 저장한다.
- `{id}` = 소문자 영숫자·`-` 만(`^[a-z0-9][a-z0-9-]{0,63}$`). 파일 이름과 파일 안 `id` 가 다르면 파일 안 `id` 를 따르고 경고 로그를 남긴다.
- 관리자 정책 `policy.json`(M3) 은 앱 설치 폴더에 있고 이 문서 범위 밖이다.
- 이 표에 없는 파일을 `{data}` 에 만들지 않는다. 새 파일은 이 문서를 고친 뒤에 만든다.

### 1.3 JSON 공통 규칙

| 항목 | 규칙 |
|---|---|
| 직렬화 | System.Text.Json, 소스 생성(`OrandaJsonContext`, Core) |
| 이름 | camelCase. C# 속성 이름(PascalCase)을 camelCase 로 바꾼 것과 같다 |
| 열거형 | 문자열(`JsonStringEnumConverter`), 멤버 이름 그대로(예: `"Production"`) |
| 시각 | ISO 8601 UTC 문자열(`2026-10-19T08:00:00Z`), C# `DateTimeOffset` |
| GUID | 소문자 하이픈 형식(`"b1f6c2d4-…"`), C# `Guid` |
| null | 쓰지 않는다(`WhenWritingNull`). 읽을 때 빠진 필드 = 기본값 |
| 모르는 필드 | 같은 버전이면 무시(경고 로그). 미래 버전 처리는 4장 |
| 인코딩 | UTF-8, BOM 없음, 들여쓰기 2칸 |
| 쓰기 | `{file}.tmp` 에 쓰고 flush → 원본 교체(원자적). 실패하면 원본 유지 |
| 버전 | 모든 파일 맨 앞 필드가 `schemaVersion`(정수, 1부터). 현재 버전은 전부 **1** |

---

## 2. JSON 스키마

표의 「필수」: ● = 반드시 있어야 함(없으면 그 항목 무효), ○ = 선택(없으면 기본값).

### 2.1 `settings.json` — `AppSettings`

| 필드 | 형식 | 필수 | 기본값 | 설명 |
|---|---|---|---|---|
| `schemaVersion` | int | ● | 1 | |
| `ui.theme` | `Theme` = `System`\|`Light`\|`Dark` | ○ | `System` | APP-02 |
| `ui.language` | string | ○ | `"system"` | `"system"`\|`"ko"`\|`"en"` (APP-03) |
| `editor.fontFamily` | string | ○ | `null`(플랫폼 기본 고정폭) | |
| `editor.fontSize` | double | ○ | 13 | pt |
| `editor.tabSize` | int | ○ | 4 | |
| `editor.lintEnabled` | bool | ○ | true | SQL 점검(lint) 켜기, TSK-13-04 |
| `keymapId` | string | ○ | `"default"` | 프리셋 id 또는 `{data}/keymaps/{id}` |
| `formatterProfileId` | string | ○ | `"oranda-default"` | 기본 포매터 프로필(FMT-08) |
| `execution.autoCommit` | bool | ○ | false | SAFE-04 |
| `execution.dbmsOutput` | bool | ○ | true | 세션 시작 시 `DBMS_OUTPUT.ENABLE` |
| `execution.scriptErrorMode` | `ScriptErrorMode` = `Ask`\|`Stop`\|`Continue` | ○ | `Ask` | EXEC-10 |
| `execution.splitOnBlankLine` | bool | ○ | false | 빈 줄을 문장 끝으로 볼지 |
| `grid.pageSize` | int | ○ | 500 | 행/페이지 |
| `grid.memoryLimitMb` | int | ○ | 200 | 페이지 캐시 상한 |
| `grid.initialLobFetchSize` | int | ○ | 4000 | 문자 |
| `grid.localSortMaxRows` | int | ○ | 100000 | 머리글 정렬을 이 PC 에서 바로 하는 행 수 문턱. 닿으면 「서버에서 ORDER BY 로 다시 조회」를 묻는다(TSK-13-17) |
| `history.enabled` | bool | ○ | true | 실행 이력 기록 |
| `history.saveBindValues` | bool | ○ | **false** | 켜야만 `binds_json` 기록 |
| `history.maxEntries` | int | ○ | 50000 | 3.4 보존 정책 |
| `history.maxAgeDays` | int | ○ | 180 | 3.4 보존 정책 |
| `metadataCache.retentionDays` | int | ○ | 30 | 쓰지 않은 접속의 캐시 삭제 |
| `monitor.refreshSeconds` | int | ○ | 5 | 2~60 |
| `workspace.autosaveSeconds` | int | ○ | 10 | 1~10 (PRD 복구 요구: 10 s 이하) |
| `workspace.restoreOnStartup` | bool | ○ | true | 마지막 작업 공간 다시 열기 |
| `workspace.lastWorkspaceId` | string | ○ | `"default"` | 종료 때 보고 있던 작업 공간(다음 시작 때 활성) |
| `workspace.openWorkspaceIds` | string[] | ○ | `["default"]` | 종료 때 열려 있던 작업 공간들(문서 탭 순서). 다음 시작 때 모두 다시 연다 |
| `tableLayout.hideAudit` · `tableLayout.auditColumns` | bool · string | ○ | false · `""` | 테이블 레이아웃 창 「감사 컬럼 빼기」(TSK-13-40). `auditColumns` = 설정 창 편집기 탭에서 정하는 컬럼 이름 목록(쉼표·줄로 나눔, `*`·`?` 와일드카드, 대소문자 무시). 선택 필드 추가라 스키마 버전은 그대로 |
| `schemaBrowser.singleClickPreview` | bool | ○ | true | 스키마 브라우저에서 객체를 한 번 누르면(방향키로 고르고 잠깐 멈추면) 미리보기 문서로 연다. false 면 두 번 누르기·Enter 로만 연다(SCH-01) |
| `schemaBrowser.filters` | object[] | ○ | `[]` | 저장 필터(모든 접속 공용, TSK-13-10). 항목: `id`(바뀌지 않는 식별자), `name`, `scope`(`All`·`LogonSchema`·`SchemasWithObjects`·`Selected`·`Pattern`), `schemas`(string[], `Selected` 일 때), `schemaPattern`(string?, `Pattern` 일 때 스키마 이름에 거는 .NET 정규식: 대소문자 무시·일부 일치·시간 제한 100 ms. 옛 파일에는 없음 = null), `hiddenTypes`(숨길 OBJECT_TYPE), `namePatterns`(OBJECT_TYPE → 이름 패턴, `*`·`%` 와일드카드), `underscoreIsWildcard`(기본 false), `wrapWithPercent`(기본 true: 와일드카드가 없으면 포함 검색) |
| `schemaBrowser.activeFilters` | object | ○ | `{}` | 접속 프로필 id → 그 접속에서 고른 필터 `id`. 없으면 필터 없음 |
| `update.checkOnStartup` | bool | ○ | true | 메인 창이 뜬 20초 뒤 백그라운드로 새 버전 확인(24시간에 한 번). 확인은 원격 전송 동의 대상이 아니다(PRD 7.4) |
| `update.autoDownload` | bool | ○ | true | 새 버전을 찾으면 백그라운드로 내려받는다. 끄면 「있음」만 알린다 |
| `update.feedUrl` | string | ○ | `null`(빌드 기본값 `OrandaUpdateFeed`) | http(s) 주소, 폴더·UNC 경로, `https://github.com/<owner>/<repo>`. http 는 설정 화면에 암호화 경고 |
| `update.skippedVersion` | string | ○ | `null` | 「이 버전 건너뛰기」 한 버전. 시작 확인에서 알리지 않는다 |
| `update.lastCheckedUtc` | datetime(UTC) | ○ | `null` | 마지막으로 확인을 끝낸 시각. 시작 확인의 24시간 규칙 |

### 2.2 `connections.json` — `ConnectionsDocument`

루트:

| 필드 | 형식 | 필수 | 기본값 | 설명 |
|---|---|---|---|---|
| `schemaVersion` | int | ● | 1 | |
| `profiles` | `ConnectionProfile[]` | ○ | `[]` | 표시 순서 = 배열 순서 |

`profiles[]` 항목 (`ConnectionProfile`):

| 필드 | 형식 | 필수 | 기본값 | 설명 |
|---|---|---|---|---|
| `id` | Guid | ● | 저장 시 새로 발급 | 프로필 식별자. 이력·작업 공간이 참조 |
| `name` | string | ● | | 표시 이름. 파일 안에서 중복 금지 |
| `user` | string | ● | | 접속 사용자. 프록시면 실제 대상 사용자(`scott`) |
| `dataSource` | string | ● | | EZConnect Plus 문자열 또는 TNS 별칭 (CONN-01) |
| `auth` | `AuthMode` = `Default`\|`SysDba`\|`SysOper`\|`SysBackup`\|`SysDg`\|`SysKm` | ○ | `Default` | CONN-02 |
| `proxyUser` | string | ○ | null | 프록시 접속 사용자(`app[scott]` 의 `app`). 있으면 저장 비밀은 이 사용자의 것 |
| `tnsAdmin` | string | ○ | null | TNS 디렉터리(접속별 `TNS_ADMIN`) |
| `walletLocation` | string | ○ | null | 지갑 폴더(접속별 `Wallet_Location`). 자동 로그인 지갑(`cwallet.sso`) 전제 |
| `connectTimeoutSeconds` | int | ○ | 15 | |
| `environment` | `ConnectionEnvironment` = `Development`\|`Test`\|`Production` | ○ | `Development` | CONN-06, SAFE-01 |
| `color` | string | ○ | null(환경 기본색) | `#RRGGBB`. 운영(`Production`)은 무시하고 항상 빨강 |
| `group` | string | ○ | null | 접속 목록 폴더 이름(한 단계) |
| `readOnly` | bool | ○ | false | SAFE-02 |
| `licensePack` | `LicensePack` = `None`\|`Diagnostics`\|`DiagnosticsAndTuning` | ○ | `None` | SAFE-07. Tuning 은 Diagnostics 를 전제하므로 3값 |
| `keepAliveSeconds` | int | ○ | 0 | 0 = 끔, 그 외 60 이상 (CONN-12) |
| `nls` | `NlsSettings` | ○ | null(서버 기본) | CONN-08 |
| `nls.dateFormat` | string | ○ | null | `NLS_DATE_FORMAT` |
| `nls.timestampFormat` | string | ○ | null | `NLS_TIMESTAMP_FORMAT` |
| `nls.language` | string | ○ | null | `NLS_LANGUAGE` |
| `nls.territory` | string | ○ | null | `NLS_TERRITORY` |
| `nls.sort` | string | ○ | null | `NLS_SORT` |
| `credentialRef` | string | ○ | null | `ICredentialStore` 키. **null = 「저장 안 함」**(접속 때마다 묻기, SAFE-06). 형식은 5.1 |
| `lastConnectedAt` | DateTimeOffset | ○ | null | 접속에 성공할 때 적는다. 시작 화면 「최근 접속」, 접속 관리자 목록의 마지막 접속 시각·「최근 접속순」 정렬(CONN-14). 접속 시험(CONN-15)과 복제는 적지 않는다 |
| `comment` | string | ○ | null(없음) | 사람이 적는 설명(여러 줄, 앞뒤 공백만 뺀다). 접속 관리자 목록 줄 도움말·보조 글·찾기에 쓰인다. 복제는 그대로 옮긴다 (TSK-13-12) |

검증(저장·읽기 때 둘 다):

| 규칙 | 실패 시 |
|---|---|
| `dataSource` 에 `password=`·`pwd=`(대소문자 무시)가 있거나 `user/xxx@` 형태 | 저장 거부, 읽을 때는 그 프로필만 무효 표시 |
| `name` 중복 | 저장 거부 |
| `keepAliveSeconds` 가 1~59 | 60 으로 올림 |

접속 내보내기 파일(`ConnectionExportDocument`, TSK-13-13 — 「파일 > 접속 내보내기」 가 쓰고 「접속 가져오기」 가 읽는다. 기본 이름 `oranda-connections.json`):

| 필드 | 형식 | 필수 | 기본값 | 설명 |
|---|---|---|---|---|
| `format` | string | ● | `"oranda.connections"` | 형식 표시. SQL Developer 의 `.json` 과 이 값으로 가른다 |
| `schemaVersion` | int | ● | 1 | 읽는 쪽보다 크면 가져오기를 거부한다 |
| `exportedAt` | DateTimeOffset | ○ | null | 참고용 |
| `profiles` | `ConnectionProfile[]` | ○ | `[]` | 위 `profiles[]` 와 같은 모양. **`credentialRef`·`lastConnectedAt` 은 쓰지 않고, 읽을 때도 버린다.** 비밀번호는 어디에도 없다 |

가져오기는 새 `id` 를 발급하고, 이름·사용자·접속 문자열이 같은 접속이 이미 있으면 「이미 있음」 으로 기본 제외한다(골라 넣으면 「이름 (2)」).

### 2.3 `formatter/{id}.json` — `FormatterProfile`

내보내기·가져오기(FMT-08 팀 공유)도 이 파일 그대로 쓴다.

| 필드 | 형식 | 필수 | 기본값 | 설명 |
|---|---|---|---|---|
| `schemaVersion` | int | ● | 1 | |
| `id` | string | ● | | 파일 이름과 같음. 기본 제공: `oranda-default`, `orange5`, `compact` |
| `name` | string | ● | | 표시 이름 |
| `options` | `FormatterOptions` | ○ | 아래 기본값 전부 | `ISqlFormatter.Format` 에 그대로 넘긴다 |

`options` (`FormatterOptions`) — 기본값 = 옛 「Oranda 기본」 출력(파일에 없는 필드도 이 값으로 읽는다). 기본 제공 「Oranda 기본」(`oranda-default`)은 사내 쿼리 서식 값(`FormatterOptions.OrandaDefault`, TSK-13-43: `case.identifier` `Upper`·`commaPosition` `Leading`·`clauseLayout` `Column`·`lineBreaks.groupOrderBy` `MultiLine`·`lineBreaks.orderBy` `SingleLine`·`lineBreaks.inListThreshold` 0·`align.alias` false·`align.comparison` true):

| 필드 | 형식 | 기본값 | PRD |
|---|---|---|---|
| `case.keyword` | `LetterCase` = `Upper`\|`Lower`\|`AsIs` | `Upper` | FMT-02 |
| `case.function` | `LetterCase` | `Upper` | FMT-02 |
| `case.identifier` | `LetterCase` | `AsIs` | FMT-02 |
| `indent.useTabs` | bool | false | FMT-03 |
| `indent.size` | int (1~16) | 4 | FMT-03 |
| `indent.subquery` | int (0~4, 단계 수) | 1 | FMT-03 |
| `indent.case` | int (0~4) | 1 | FMT-03 |
| `indent.plsqlBlock` | int (0~4) | 1 | FMT-03 |
| `commaPosition` | `CommaPosition` = `Leading`\|`Trailing` | `Trailing` | FMT-04 |
| `keywordAlign` | `KeywordAlign` = `Left`\|`Right` | `Left` | FMT-05 (Right = 절 키워드 끝을 SELECT 끝에 맞춤, TSK-13-38) |
| `clauseLayout` | `ClauseLayout` = `Indent`\|`Column` | `Indent` | FMT-05 (Column = 절 키워드 맨 앞 열·본문 7번째 열·여러 줄 괄호 묶음, SELECT·WITH·INSERT·UPDATE·DELETE·CREATE … AS 질의, TSK-13-43) |
| `operatorSpacing` | bool | true | FMT-04 |
| `parenInnerSpace` | bool | false | FMT-04 |
| `lineBreaks.selectList` | `SelectListLayout` = `OnePerLine`\|`FillToWidth` | `OnePerLine` | FMT-05 |
| `lineBreaks.groupOrderBy` | `ByListLayout` = `SingleLine`\|`MultiLine` | `SingleLine` | FMT-05 (GROUP BY·ORDER BY 목록, TSK-13-38) |
| `lineBreaks.orderBy` | `OrderByLayout` = `FollowGroupBy`\|`SingleLine`\|`MultiLine` | `FollowGroupBy` | FMT-05 (ORDER BY 만 따로, TSK-13-43) |
| `lineBreaks.joinClause` | bool | true | FMT-05 |
| `lineBreaks.andOr` | `AndOrBreak` = `Before`\|`After`\|`None` | `Before` | FMT-05 |
| `lineBreaks.inListThreshold` | int (0 = 줄바꿈 안 함) | 5 | FMT-05 |
| `lineBreaks.maxLineWidth` | int (40~400) | 120 | FMT-05 |
| `align.alias` | bool | true | FMT-06 |
| `align.assignment` | bool | true | FMT-06 |
| `align.columnDefinition` | bool | true | FMT-06 |
| `align.equals` | bool | false | FMT-06 |
| `align.comparison` | bool | false | FMT-06 (Column 에서 외부 조인 `(+)` 묶음·같은 칼럼 범위 조건의 비교 연산자·값 열 맞춤, TSK-13-43) |
| `comments.preserve` | bool | true | FMT-07 |
| `comments.maxBlankLines` | int (0~5) | 1 | FMT-07 |
| `statementTerminator` | `StatementTerminator` = `Keep`\|`Add`\|`Remove` | `Keep` | FMT-07 |
| `honorFormatterOff` | bool | true | FMT-10 (`-- @formatter:off`/`on` 존중) |

- 범위를 벗어난 정수는 가장 가까운 경계값으로 맞추고 경고 로그.
- FMT-11(토큰 동일성)은 옵션이 아니라 항상 켜진 보장이다. 끄는 필드를 두지 않는다.

### 2.4 `keymaps/{id}.json` — `Keymap` (형식만)

키 표 내용(명령 ID·프리셋 4종의 키)은 TSK-02-02 가 정한다. 이 절은 파일 형식만 정한다.

| 필드 | 형식 | 필수 | 기본값 | 설명 |
|---|---|---|---|---|
| `schemaVersion` | int | ● | 1 | |
| `id` | string | ● | | 프리셋 id: `default`, `orange5`, `plsqldev`, `sqldev` (TSK-02-02 확정) |
| `name` | string | ● | | 표시 이름 |
| `basedOn` | string | ○ | null | 바탕 프리셋 id. 사용자 키맵은 이 프리셋 위에 `bindings` 를 덮어쓴다. 프리셋 자신은 null |
| `bindings` | `KeyBinding[]` | ○ | `[]` | |
| `bindings[].command` | string | ● | | 명령 ID(`ICommandRegistry`, TSK-02-02 형식) |
| `bindings[].keys` | string[] | ● | | 키 조합 목록. **`[]` = 이 명령의 바탕 키를 지움** |
| `bindings[].when` | string | ○ | null | 적용 문맥(예: `editorFocus`, `gridFocus`). 값 목록 = commands.md 2.2 |
| `bindings[].mac` | string[] | ○ | null | macOS 에서 `keys` 대신 쓰는 목록. 이 안의 `Ctrl` 은 실제 Control 키(commands.md macOS 규칙) |

키 조합 문자열 규칙:

| 항목 | 규칙 |
|---|---|
| 수식키 | `Ctrl`, `Shift`, `Alt`, `Meta`, `Primary`(Windows=Ctrl, macOS=Cmd). 순서 `Primary+Ctrl+Alt+Shift+Meta+키` 로 정규화해 저장 |
| 키 이름 | Avalonia `Key` 열거형 이름(`F5`, `Enter`, `A`, `OemComma` …) |
| 연속 입력(chord) | 공백 하나로 구분: `"Primary+K Primary+C"` (최대 2단) |
| 충돌 | 같은 `when` 에서 같은 키가 둘이면 뒤 항목이 이기고 경고 |

### 2.5 `layout.json` — `LayoutDocument` (Oranda.App DTO)

TRD 4.11 대로 앱 DTO 다(Core 가 아니라 `Oranda.App`). Dock.Avalonia 의 형식을 그대로 저장하지 않고 이 DTO 로 바꿔 저장한다 — 라이브러리 교체·버전업에 파일이 깨지지 않게 하기 위해서다.

| 필드 | 형식 | 필수 | 기본값 | 설명 |
|---|---|---|---|---|
| `schemaVersion` | int | ● | 2 | 1 → 2: 예전 기본 모양을 왼쪽 묶음 전체 높이 모양으로 옮긴다(ui-shell 2.1) |
| `window` | `WindowPlacement` | ○ | null(화면 가운데 기본 크기) | 이름 붙인 레이아웃에는 싣지 않는다 |
| `name` | string | ○ | null | 이름 붙인 레이아웃(`layouts/{id}.json`)의 이름. 이름으로 찾고 같은 이름은 덮어쓴다. `layout.json` 에서는 쓰지 않는다 |
| `window.x`, `window.y` | int | ● | | 논리 좌표(DIP) |
| `window.width`, `window.height` | int | ● | | DIP. 최소 800×500 |
| `window.state` | `WindowStateKind` = `Normal`\|`Maximized`\|`FullScreen` | ○ | `Normal` | 최소화로는 저장하지 않음 |
| `window.screen` | string | ○ | null | 화면 이름. 없거나 연결이 끊긴 화면이면 주 화면 가운데로 |
| `root` | `DockNode` | ○ | null(기본 배치, TSK-02-02) | 도킹 트리 |
| `root.kind` | `DockNodeKind` = `Split`\|`ToolGroup`\|`Documents` | ● | | `Documents` 는 트리에 정확히 1개 |
| `root.orientation` | `Orientation` = `Horizontal`\|`Vertical` | ○ | `Horizontal` | `Split` 만 |
| `root.proportion` | double (0~1) | ○ | 균등 분배 | 부모 안에서 차지하는 비율 |
| `root.children` | `DockNode[]` | ○ | `[]` | `Split` 만 |
| `root.tools` | string[] | ○ | `[]` | `ToolGroup` 만. 도구 창 ID(TSK-02-02) |
| `root.activeTool` | string | ○ | 첫 항목 | `ToolGroup` 만 |
| `hiddenTools` | string[] | ○ | `[]` | 닫아 둔 도구 창 ID |
| `migrations` | string[] | ○ | null(표지 없음) | 이 배치에 이미 적용한 일회성 이주 이름. `rightToolsOpen` 이 없으면 읽을 때 한 번 바인드 변수·템플릿 창을 열고 표지를 남긴다(TSK-13-16). 기본 배치·되돌림에도 표지가 붙는다 |

- 모르는 도구 ID는 버린다. 필수 도구가 빠졌으면 기본 위치에 다시 넣는다.
- 편집기 탭(문서)은 이 파일이 아니라 작업 공간(2.6)에 있다.

### 2.6 `workspaces/{id}.json` — `Workspace`

자동 저장 `autosave/{workspaceId}.json` 도 같은 스키마다. 작업 공간은 여러 개를 동시에 열 수 있고(문서 탭 하나 = 작업 공간 하나), 열린 작업 공간마다 파일이 하나씩이다. 정상 종료하면 열린 작업 공간을 모두 저장하고 자동 저장 파일을 지운다. 작업 공간 문서를 닫아도 저장하고 그 자동 저장을 지운다(「작업 공간 열기」로 다시 연다). 시작할 때 자동 저장 파일이 하나라도 남아 있으면 비정상 종료로 보고 복구를 한 번 묻고, 예 하면 남은 작업 공간을 모두 연다.

새 작업 공간 ID 는 열려 있지도 저장돼 있지도 않으면 `default`, 아니면 `workspace-N` 이다(닫아 둔 작업 공간 파일을 덮어쓰지 않는다). Markdown 작업 공간 파일·MyBatis 매퍼에서 연 작업 공간은 `{파일 이름 slug}-{경로 해시 8자}` 라서 같은 파일은 늘 같은 ID 다.

| 필드 | 형식 | 필수 | 기본값 | 설명 |
|---|---|---|---|---|
| `schemaVersion` | int | ● | 1 | |
| `id` | string | ● | | 파일 이름과 같음. 기본 작업 공간 = `default` |
| `name` | string | ● | | |
| `savedAt` | DateTimeOffset | ● | | |
| `activeTabId` | Guid | ○ | 첫 탭 | |
| `tabs` | `WorkspaceTab[]` | ○ | `[]` | 열린 순서 |
| `tabs[].id` | Guid | ● | | |
| `tabs[].title` | string | ● | | 파일 없는 탭의 표시 이름 |
| `tabs[].filePath` | string | ○ | null | 파일과 연결된 탭이면 절대 경로 |
| `tabs[].content` | string | ○ | null | **저장 안 된 내용만**. 파일 탭이고 변경 없으면 null(파일에서 다시 읽음) |
| `tabs[].isDirty` | bool | ○ | false | |
| `tabs[].encoding` | string | ○ | `"utf-8"` | 파일 탭의 인코딩 이름 |
| `tabs[].caret` | `TextPosition` | ○ | `{line:1,column:1}` | 1부터 |
| `tabs[].selectionStart` | `TextPosition` | ○ | null | 선택이 있을 때만 |
| `tabs[].scrollLine` | int | ○ | 1 | 맨 위에 보이던 줄 |
| `tabs[].connectionProfileId` | Guid | ○ | null | 탭이 쓰던 접속. 열 때 자동 접속하지 않고 「다시 접속」 버튼만 둔다 |
| `tabs[].binds` | `WorkspaceBind[]` | ○ | null | 탭의 SQL 이 참조하는 바인드 변수의 값(EXEC-03). SQL 에 처음 나온 순서. 바인드 변수 창에 없는 이름, 기본값(빈 값·`Varchar2`·`In`)인 줄, 이름에 `pass`·`pwd`·`secret`·`token` 이 든 바인드는 넣지 않는다. 탭을 열면 이 값으로 창의 줄을 만들거나 덮어쓴다 |
| `tabs[].binds[].name` | string | ● | | 콜론 없는 이름 |
| `tabs[].binds[].type` | `BindDataType` | ○ | `"Varchar2"` | `Varchar2`·`NVarchar2`·`FixedChar`·`Number`·`BinaryDouble`·`Date`·`Timestamp`·`TimestampTz`·`Clob`·`Blob`·`Raw`·`Boolean`·`Json`·`RefCursor` |
| `tabs[].binds[].direction` | `BindDirection` | ○ | `"In"` | `In`·`Out`·`InOut` |
| `tabs[].binds[].value` | string | ○ | `""` | 값 칸의 글자 그대로. NULL 이면 빈 문자열 |
| `tabs[].binds[].isNull` | bool | ○ | false | 값이 NULL(값 칸이 빔) |
| `sourcePath` | string | ○ | null | 이 작업 공간을 연 Markdown 작업 공간 파일(.md)·MyBatis 매퍼(.xml)의 절대 경로. 같은 파일을 다시 열면 이 작업 공간으로 옮겨 간다 |
| `layout` | string | ○ | `"layout.json"` | 쓸 레이아웃 파일(`{data}` 상대). 1.0 은 항상 `layout.json` |

`tabs[].binds` 는 선택 필드 추가라 `schemaVersion` 은 1 그대로다(4.1). 이 필드가 없는 옛 파일은 바인드 값 없이 읽힌다. 자동 저장은 탭의 바인드 값(이름·형식·방향·값·NULL)이 바뀌어도 쓴다: 값만 바꾸고 비정상 종료해도 복구하면 그 값이 돌아온다.

**.sql 파일 끝 바인드 블록.** SQL 편집기가 `.sql` 파일을 저장할 때(저장·다른 이름으로 저장·모두 저장) 위와 같은 규칙으로 고른 값을 본문 끝에 `--` 줄 주석 블록으로 붙인다. MyBatis(.xml)·Markdown 작업 공간(.md)·PL/SQL 편집기 저장과 다른 확장자에는 붙이지 않는다.

```
select * from emp where empno = :empno;

-- @oranda-binds v1
-- {"name":"EMPNO","type":"Number","direction":"In","value":"1002","isNull":false}
```

- 첫 줄은 고유 표지 `-- @oranda-binds v1`, 다음 줄마다 `-- ` 뒤에 `WorkspaceBind` JSON 한 개(필드 이름·열거형 표기는 `tabs[].binds[]` 와 같다). JSON 은 기본 이스케이프라 줄바꿈·따옴표·`&`·비 ASCII 글자가 `\uXXXX` 꼴이 되어 블록은 ASCII 한 줄씩이다(cp949·UTF-16 파일에서도 왕복하고, 주석 안의 `&` 도 치환 변수로 묻는 SQL*Plus 에 걸리지 않는다).
- 본문과 표지 사이에는 본문 끝 줄바꿈 유무와 상관없이 줄바꿈 하나를 넣고(본문의 줄바꿈 종류를 따른다. 위 예는 본문이 줄바꿈으로 끝나서 빈 줄이 하나 생겼다), 뗄 때 그 하나만 뗀다. 그래서 블록 앞 본문은 인코딩·줄바꿈 종류·끝 줄바꿈까지 바이트 그대로 돌아온다.
- 파일을 열면(작업 공간 복원에서 파일을 다시 읽을 때도) 표지 줄부터 파일 끝까지 모든 줄이 `-- ` 로 시작하고 JSON 으로 읽히는 경우에만 블록을 떼어 편집기에는 본문만 보이고 값은 바인드 변수 창에 넣는다(작업 공간에 남은 값이 있으면 그것이 이긴다). 하나라도 어긋나면 떼지 않는다. 형식·방향 이름은 대소문자·공백·밑줄을 가리지 않고 읽는다(`NUMBER`, `IN OUT`).
- 편집기 글에는 블록이 없으므로 몇 번을 저장해도 블록은 하나다. 저장할 값이 없으면 블록을 쓰지 않아 예전과 같은 바이트가 된다. 바인드 값만 바꾸면 탭은 변경됨(`*`)이 되지 않는다(「모두 저장」은 변경된 탭만 쓴다).

---

## 3. `oranda.db` SQLite 스키마

### 3.1 연결 설정

| 항목 | 값 | 이유 |
|---|---|---|
| 드라이버 | Microsoft.Data.Sqlite (번들 e_sqlite3) | TRD 2.2 |
| `PRAGMA journal_mode` | `WAL` | 이력 쓰기 중에도 검색 가능 |
| `PRAGMA synchronous` | `NORMAL` | WAL 에서 충분, 쓰기 지연 감소 |
| `PRAGMA foreign_keys` | `ON` | |
| `PRAGMA busy_timeout` | 5000 | 앱 두 개가 동시에 열 때 |
| 시각 | TEXT, ISO 8601 UTC(`2026-10-19T08:00:00.123Z`) | 문자열 정렬 = 시간 정렬 |
| GUID | TEXT 소문자 하이픈 | JSON 과 같은 표기 |

### 3.2 DDL (v1)

전체 생성 스크립트: [`examples/oranda-db-v1.sql`](examples/oranda-db-v1.sql). 앱 안 마이그레이션 스크립트 `0001_init.sql` 은 이 파일과 같은 내용이다.

```sql
CREATE TABLE schema_version (
  version     INTEGER PRIMARY KEY,           -- 적용한 스크립트 번호
  applied_at  TEXT    NOT NULL,              -- UTC ISO 8601
  description TEXT    NOT NULL
);

CREATE TABLE history (
  id             INTEGER PRIMARY KEY,        -- rowid
  profile_id     TEXT    NOT NULL,           -- connections.json profiles[].id
  profile_name   TEXT    NOT NULL,           -- 기록 당시 이름(프로필을 지워도 이력 표시용)
  db_key         TEXT,                       -- 3.3. 접속 전 실패면 NULL
  executed_at    TEXT    NOT NULL,
  sql_text       TEXT    NOT NULL,           -- 비밀 가림(5.3) 후 본문
  sql_truncated  INTEGER NOT NULL DEFAULT 0, -- 1 = 256 KiB 에서 잘림
  statement_kind TEXT    NOT NULL,           -- StatementKind 이름
  source         TEXT    NOT NULL,           -- HistorySource 이름
  status         TEXT    NOT NULL,           -- HistoryStatus 이름
  elapsed_ms     INTEGER,
  rows_affected  INTEGER,
  error_code     TEXT,                       -- 'ORA-00942' 형식
  error_message  TEXT,                       -- 첫 줄만, 최대 1,000자
  binds_json     TEXT                        -- history.saveBindValues=true 일 때만. 기본 NULL
);
CREATE INDEX ix_history_profile_time ON history(profile_id, executed_at DESC);
CREATE INDEX ix_history_time         ON history(executed_at DESC);

-- 이력 검색(EXEC-07): 부분 문자열 검색용 trigram 전문 색인(외부 내용 테이블)
CREATE VIRTUAL TABLE history_fts USING fts5(
  sql_text, content='history', content_rowid='id', tokenize='trigram');
-- 동기화 트리거 3개(ai/ad/au)는 예제 파일 참조

CREATE TABLE meta_source (                   -- 캐시 단위(접속 대상)별 관리 정보
  db_key        TEXT PRIMARY KEY,
  server_version TEXT,                       -- '19.24.0.0.0'
  refreshed_at  TEXT,                        -- 객체 이름 목록을 마지막으로 다 받은 시각
  last_used_at  TEXT    NOT NULL             -- 보존 정책(3.4) 기준
);

CREATE TABLE meta_object (
  db_key        TEXT NOT NULL REFERENCES meta_source(db_key) ON DELETE CASCADE,
  owner         TEXT NOT NULL,
  name          TEXT NOT NULL,
  type          TEXT NOT NULL,               -- Oracle OBJECT_TYPE 그대로('TABLE', 'PACKAGE BODY')
  status        TEXT,                        -- 'VALID' | 'INVALID'
  last_ddl_time TEXT,                        -- 변경 감지(TRD 4.7)
  payload_json  TEXT,                        -- 열·인자 등 상세. NULL = 아직 안 받음
  fetched_at    TEXT NOT NULL,
  PRIMARY KEY (db_key, owner, name, type)
) WITHOUT ROWID;
CREATE INDEX ix_meta_object_name ON meta_object(db_key, name);         -- 자동완성 이름 앞부분 검색
CREATE INDEX ix_meta_object_type ON meta_object(db_key, owner, type);  -- 스키마 트리
```

열 값 목록:

| 열 | 값 |
|---|---|
| `statement_kind` | `Query`, `Dml`, `Ddl`, `PlSql`, `TransactionControl`, `SqlPlus`, `Other` |
| `source` | `Editor`, `Script`, `GridEdit`, `SchemaBrowser`, `Monitor`, `Extension` |
| `status` | `Succeeded`, `Failed`, `Cancelled`, `Denied`(정책 관문 거부) |

- `payload_json` 내용은 메타데이터 Task(SCH)가 정한다. 맨 위에 `"v"`(정수) 필드를 두고, 앱이 아는 `v` 와 다르면 캐시가 없는 것으로 본다(DB 마이그레이션 없이 형식 변경 가능).
- `meta_*` 는 언제든 다시 만들 수 있는 캐시다. `history`·`change_journal` 은 사용자 데이터다.

### 3.2.1 `change_journal` (v2, 마이그레이션 `0002_change_journal.sql`, TSK-13-07)

DDL 실행 직전·PL/SQL 컴파일 직전에 대상 객체의 **이전 DDL(서버에 있던 그대로)** 을 보관한다. 서버에는 아무것도 설치하지 않고, 앱이 같은 세션에서 `DBMS_METADATA.GET_DDL`(SELECT)로 읽어 로컬에만 쌓는다.

```sql
CREATE TABLE change_journal (
  id                INTEGER PRIMARY KEY,
  captured_at       TEXT    NOT NULL,           -- 보관 시각(UTC)
  profile_id        TEXT    NOT NULL,           -- 프로필을 지워도 남도록 이름을 같이 둔다
  profile_name      TEXT    NOT NULL,
  db_key            TEXT    NOT NULL,           -- 3.3. 같은 DB·같은 사용자면 프로필이 달라도 한 객체의 버전으로 본다
  server_name       TEXT,                       -- 접속 대상(DataSource) 표시용
  owner             TEXT    NOT NULL,
  name              TEXT    NOT NULL,
  object_type       TEXT    NOT NULL,           -- Oracle OBJECT_TYPE('TABLE', 'PACKAGE BODY')
  cause             TEXT    NOT NULL CHECK (cause IN ('Ddl','Compile')),
  statement_summary TEXT,                       -- 실행한 문장의 첫 줄(비밀 가림 후, 200자)
  previous_ddl      TEXT,                       -- 이전 DDL. 보관에 실패하면 NULL
  content_hash      TEXT,                       -- previous_ddl(가림 후) SHA-256 소문자 hex. 줄바꿈 CRLF→LF, 앞뒤 공백 무시
  failure_reason    TEXT                        -- 보관 실패 이유(권한·시간 초과·객체 없음). 성공이면 NULL
);
CREATE INDEX ix_change_journal_object ON change_journal(db_key, owner, name, object_type, id DESC);
CREATE INDEX ix_change_journal_time   ON change_journal(captured_at DESC);
```

| 항목 | 규칙 |
|---|---|
| 대상 | `ALTER`·`DROP`·`CREATE OR REPLACE`·`RENAME`. 새 객체를 만드는 `CREATE`(보관할 이전 DDL 없음)·`TRUNCATE`(데이터 작업)·`COMMENT`(`GET_DDL('TABLE')` 에 주석이 들어가지 않아 되돌리기에 쓸 수 없음)는 제외 |
| 읽기 | `DBMS_METADATA.OPEN` 핸들에 `DDL` 변환을 걸고 `SQLTERMINATOR` 만 켠다. 세션 변환 설정(DDL 보기에서 저장소·테이블스페이스를 껐을 때 등)을 물려받지 않아 보관본과 해시가 세션 설정에 흔들리지 않는다. 핸들은 항상 닫는다. `FETCH_CLOB` 이 NULL 이면 `ALL_OBJECTS` 로 「없음(ORA-31603)」과 「있으나 읽을 권한 없음(ORA-01031)」을 가른다 |
| 순서 | `Ddl` 원인: 정책 관문·SAFE-03 확인을 통과한 뒤, 사용자 문장을 보내기 직전에 같은 세션에서 읽는다. `Compile` 원인(PL/SQL 편집기)은 편집기가 실행 엔진 호출 직전에 읽으므로 정책이 거부하거나 사용자가 확인을 취소한 컴파일도 행이 남을 수 있다(같은 내용은 쌓이지 않아 피해가 작다). 조회는 SELECT 뿐이라 COMMIT·ROLLBACK·세션 변환 설정을 건드리지 않는다 |
| 실패 | 보관 실패는 실행을 막지 않는다. 권한(ORA-01031)·제한 시간(10초)·`ORA-31603`(객체 없음, ALTER·DROP·RENAME 한정)은 `failure_reason` 이 있는 행 1줄로 남긴다. `CREATE OR REPLACE` 로 만드는 새 객체의 `ORA-31603` 은 행을 남기지 않는다 |
| PACKAGE·TYPE | 명세와 본문은 별개 버전이다(`PACKAGE` / `PACKAGE BODY`). `DROP`·`ALTER` 는 둘 다, `CREATE OR REPLACE PACKAGE` 는 명세만 보관한다 |
| 중복 | 같은 `(db_key, owner, name, object_type)` 의 **마지막 성공 행**과 `content_hash` 가 같으면 쌓지 않는다. 실패 행이 사이에 끼어도 같다 |
| 원인 | `Ddl` = 실행 엔진이 읽은 `GET_DDL` 본문, `Compile` = PL/SQL 편집기가 컴파일 직전에 읽은 소스(`CREATE OR REPLACE` + 소유자를 붙인 편집기 글자). 형식이 달라 같은 객체라도 두 원인의 해시는 다를 수 있다 |
| 크기 | `previous_ddl` 2 MiB(UTF-8) 초과 시 본문 없이 실패 행으로 남긴다 |
| 보존 | 365일(`captured_at`) 지난 행, 객체(`db_key`·`owner`·`name`·`object_type`)당 성공 50버전 초과분(오래된 것부터)을 지운다. 실패 행은 기간으로만 지운다. 상수이며 설정 화면에 두지 않는다. 시작 후 1회 + 기록 100건마다 |
| 한계 | 문장 안 `EXECUTE IMMEDIATE '...'` 로 나가는 DDL(익명 블록)과 대상 이름을 치환 변수(`&t`)로 쓴 문장은 대상을 알 수 없어 보관하지 않는다. 호출하는 쪽이 `SqlStatement.ChangeTarget` 을 직접 채우면 보관된다 |
| 검색 | `owner`·`name`·`statement_summary`·`previous_ddl` 의 부분 문자열(LIKE) |

### 3.3 `db_key`

`{DB_UNIQUE_NAME}/{CON_NAME}/{SESSION_USER}` 를 대문자로. 비 CDB 는 `CON_NAME` 자리에 `-`.
예: `ORCLCDB/ORDERS_PDB/APP_RO`. 같은 DB·같은 사용자면 프로필이 달라도 캐시를 함께 쓴다. 값은 접속 직후 `ServerProbe` 결과에서 만든다.

### 3.4 보존 정책

| 대상 | 규칙 | 언제 |
|---|---|---|
| `history` 개수 | `history.maxEntries`(기본 50,000)를 넘으면 오래된 것부터 지움 | 앱 시작 후 백그라운드 1회 + 기록 1,000건마다 |
| `history` 기간 | `executed_at` 이 `history.maxAgeDays`(기본 180일)보다 오래되면 지움 | 같음 |
| `history.sql_text` | 256 KiB(UTF-8 바이트) 넘으면 앞부분만 저장, `sql_truncated=1` | 기록할 때 |
| `history.enabled=false` | 새 기록 안 함. 기존 이력은 유지(지우기는 이력 창 명령) | |
| `change_journal` | 3.2.1. 365일 지난 행, 객체당 성공 50버전 초과분을 지움(상수) | 앱 시작 후 백그라운드 1회 + 기록 100건마다 |
| `meta_source` | `last_used_at` 이 `metadataCache.retentionDays`(기본 30일) 지나면 행 삭제 → `meta_object` 연쇄 삭제 | 앱 시작 후 백그라운드 1회 |
| 파일 크기 | 자동 정리 뒤에는 `VACUUM` 하지 않는다(빈 페이지는 SQLite 가 재사용). 사용자가 이력 창에서 「이력 비우기」를 할 때만 백그라운드 `VACUUM` | UI 멈춤 방지 |

---

## 4. 상향 마이그레이션 규칙

내리는 방향(다운그레이드)은 지원하지 않는다. 대신 미래 버전 파일은 읽기 전용으로 연다.

### 4.1 JSON 파일

```
읽기 → JsonNode 로 파싱 → schemaVersion 확인
  ├─ 없음/정수 아님/파싱 실패 → 손상: {file} 을 backup/{name}.corrupt-{UTC시각} 로 옮기고 기본값으로 시작, 사용자에게 알림
  ├─ == 현재           → 그대로 역직렬화
  ├─ <  현재           → ① backup/ 에 원본 복사  ② v→v+1 변환기를 차례로 적용(JsonNode 단위)
  │                       ③ 역직렬화·검증  ④ 현재 버전으로 원자적 다시 쓰기
  │                       ③에서 실패하면 원본은 그대로 두고 해당 파일만 읽기 전용 + 알림
  └─ >  현재           → 읽기 전용: 아는 필드만 읽어 쓴다. 이 파일은 저장하지 않는다(설정 화면에 「새 버전 Oranda 가 만든 파일」 표시)
```

| 항목 | 규칙 |
|---|---|
| 변환기 | 파일 종류별·버전 단계별 1개: `ISettingsMigration { string FileKind; int FromVersion; JsonNode Apply(JsonNode) }` (`FileKind` = `settings`, `connections`, `formatter`, `keymap`, `layout`, `workspace`). 반드시 1 단계씩(1→2, 2→3). 건너뛰는 변환기 금지 |
| 변환기 성질 | 순수 함수(입력 노드만 보고 결과 노드 반환), 파일·네트워크·키체인 접근 금지. 비밀을 만들어 넣지 않는다 |
| 백업 이름 | `backup/{파일이름}.v{옛 버전}.{UTC yyyyMMddHHmmss}.bak`. 종류별 최근 3개만 남김 |
| 시험 | 변환기마다 `examples/` 의 옛 버전 예제 → 새 버전 기대값 비교 시험을 둔다. 새 버전을 만들 때 그때의 예제를 `examples/v{n}/` 에 얼려 둔다 |
| 버전 올리기 기준 | 필드 이름 변경·삭제·의미 변경·형식 변경 때만 올린다. **선택 필드 추가는 버전을 올리지 않는다**(옛 앱은 모르는 필드를 무시) |
| 현재 버전 상수 | Core `SchemaVersions` 정적 클래스: `Settings`, `Connections`, `Formatter`, `Keymap`, `Layout`, `Workspace` = 1 |

### 4.2 SQLite (`oranda.db`)

| 항목 | 규칙 |
|---|---|
| 스크립트 | `Oranda.Platform` 임베디드 리소스 `Migrations/OrandaDb/NNNN_설명.sql`(4자리 순번, 1부터 빈 번호 없음) |
| 현재 버전 | `SELECT MAX(version) FROM schema_version` (테이블 없으면 0). `PRAGMA user_version` 은 쓰지 않는다 |
| 적용 | 현재보다 큰 스크립트를 순번대로 하나씩: `BEGIN IMMEDIATE` → 스크립트 실행 → `INSERT INTO schema_version` → `COMMIT`. 실패하면 `ROLLBACK`, 이후 스크립트 중단 |
| 백업 | 적용할 스크립트가 하나라도 있으면 먼저 `VACUUM INTO 'backup/oranda.db.v{현재}.{시각}.bak'` (최근 2개 유지) |
| 실패 시 | 이력은 읽기 전용, 메타데이터 캐시는 메모리만 쓰고 알림. 앱은 계속 동작 |
| 미래 버전 | DB 버전 > 앱이 아는 최대 번호 → 같은 처리(이력 읽기 전용, 캐시 메모리만). 쓰지 않는다 |
| 손상 | 열기·`PRAGMA quick_check` 실패 → `backup/oranda.db.corrupt-{시각}` 로 옮기고 새로 만든다(이력 유실 알림) |
| 캐시 테이블 변경 | `meta_*` 형식이 바뀌면 스크립트에서 `DROP` 후 다시 만든다(데이터 옮기기 불필요) |
| `history` 변경 | 데이터 보존 필수. `ALTER TABLE ADD COLUMN` 또는 새 테이블 복사 후 교체 |

---

## 5. 비밀번호가 파일에 들어가지 않는 구조

### 5.1 흐름

```
접속 대화상자 ─비밀번호─▶ ICredentialStore.SaveAsync(credentialRef, bytes)   ← 「저장」 선택 시만
                         │
connections.json ◀────── credentialRef 문자열만 저장 ("oranda:conn:{profileId}")
                         │
접속 시 ─ credentialRef ─▶ ICredentialStore.LoadAsync → SecureCredential(메모리, 사용 후 지움) → ISessionManager.OpenAsync
```

| 항목 | 규칙 |
|---|---|
| `credentialRef` 형식 | `oranda:conn:{profileId 소문자 GUID}`. 1.0 은 프로필당 비밀 1개(프록시면 프록시 사용자 비밀) |
| Windows 저장 | `{data}/credentials/{SHA-256(credentialRef) 소문자 hex}.bin`, DPAPI `CurrentUser` 범위 + 엔트로피 상수. 파일 이름에 사용자·호스트가 드러나지 않음 |
| macOS 저장 | 키체인 일반 암호, service = `Oranda`, account = `credentialRef` |
| 「저장 안 함」 | `credentialRef = null`. 저장소에 아무것도 쓰지 않고 접속마다 묻는다 |
| 프로필 삭제 | `ICredentialStore.DeleteAsync(credentialRef)` 를 함께 호출 |
| 가져오기(CONN-09) | 다른 도구의 암호화 비밀번호는 풀 수 있으면 곧바로 저장소로 옮기고, 파일에는 쓰지 않는다 |
| 지갑 비밀번호 | 다루지 않는다. 자동 로그인 지갑(`cwallet.sso`)만 지원 |
| 비밀번호 만료(CONN-04) | 새 비밀번호는 접속 후 저장소에만 갱신 |

### 5.2 파일별 「비밀 없음」 확인 표

| 파일 | 비밀이 들어갈 수 있는 자리 | 막는 방법 | 자동 검사 |
|---|---|---|---|
| `connections.json` | `dataSource` 문자열 | 스키마에 비밀 필드 없음 + 2.2 검증(`password=`, `user/pw@` 거부) | 시험 ①② |
| `settings.json` | 없음 | 스키마에 문자열 자유 필드 거의 없음 | 시험 ① |
| `formatter/*.json` | 없음 | 서식 옵션만 | 시험 ① |
| `keymaps/*.json` | 없음 | 명령 ID·키만 | 시험 ① |
| `layout.json` | 없음 | 좌표·도구 ID만 | 시험 ① |
| `workspaces/*.json`, `autosave/*.json` | `tabs[].content`(사용자가 쓴 SQL 본문), `tabs[].binds[].value`(바인드 값) | 사용자 작성 내용이므로 가리지 않는다. 대신 접속 비밀번호를 앱이 넣는 경로는 없다. 이름에 `pass`·`pwd`·`secret`·`token` 이 든 바인드는 저장하지 않는다(이력 가림과 같은 규칙, Core `SensitiveBindNames`) | 시험 ② + 바인드 저장 시험 |
| SQL 편집기의 `.sql` 파일(사용자 폴더) | 끝 바인드 블록(2.6) | 위와 같은 규칙으로 비밀로 보이는 바인드는 넣지 않는다 | 바인드 저장 시험 |
| `oranda.db` `history.sql_text` | `IDENTIFIED BY …`, `CONNECT u/p@…` 같은 문장 | 기록 전 5.3 가림 규칙 적용 | 시험 ③ |
| `oranda.db` `change_journal.previous_ddl`·`statement_summary` | 서버에서 읽은 이전 DDL(USER·DB 링크 DDL 의 `IDENTIFIED BY VALUES`), 실행한 문장 | 기록 전 5.3 가림 규칙 적용. **저장 소스(PACKAGE·PROCEDURE·FUNCTION·TRIGGER·TYPE 와 각 BODY·LIBRARY·JAVA SOURCE)의 본문은 가리지 않는다**: 변수 이름 `password` 같은 코드를 `***` 로 바꾸면 되돌리기 본문이 깨진다. 요약은 항상 가림 | 변경 일지 저장소 시험 |
| `oranda.db` `history.binds_json` | 바인드 값 | 기본 끔(`history.saveBindValues=false`). 켜도 이름이 `pass`·`pwd`·`secret`·`token` 을 포함하는 바인드는 `***` | 시험 ③ |
| `oranda.db` `history.error_message` | 서버 오류문 | 서버가 비밀번호를 오류문에 넣지 않음. 첫 줄·1,000자 제한 | — |
| `oranda.db` `meta_*` | 서버 메타데이터(소스 코드 일부일 수 있음) | 비밀번호 아님. DB 링크 비밀번호 열(`*_LINKS.PASSWORD`)은 조회 대상에서 뺀다 | — |
| `credentials/*.bin` | **암호화된 비밀** | DPAPI 사용자 범위. 다른 사용자·다른 PC에서 못 풂 | — |
| `logs/` | 연결 문자열·바인드 | TRD 9 Serilog 마스킹 필터(범위 밖, 같은 가림 규칙 공유) | 별도 Task |
| 바인드 마지막 값(EXEC-03) | 바인드 값 | 탭의 SQL 이 참조하는 이름의 값만 작업 공간 `tabs[].binds` 와 `.sql` 끝 블록에 저장한다(2.6). 비밀로 보이는 이름은 빼고, 나머지 목록은 메모리에만 있다 | 바인드 저장 시험 |

자동 검사(PRD 9.2 「비밀번호가 설정 파일·로그 어디에도 평문으로 없다」):

| 번호 | 시험 |
|---|---|
| ① | 반사(reflection)로 6장 모든 영속 record 의 속성 이름에 `password`·`pwd`·`secret`·`token` 이 없음을 단정 |
| ② | 임시 `ORANDA_DATA_DIR` 에서 알려진 시험 비밀번호로 프로필 저장·접속 흐름(가짜 세션)을 돈 뒤, 폴더 전체 파일(`credentials/` 제외)의 바이트에 그 비밀번호(UTF-8·UTF-16)가 없음을 단정 |
| ③ | 5.3 가림 규칙 단위 시험 + `history` 기록 후 DB 파일에 시험 비밀번호가 없음을 단정 |

### 5.3 이력 SQL 가림 규칙

기록 직전 L1 렉서 토큰 기준(문자열·주석 안은 건드리지 않음)으로 아래 다음 토큰 하나를 `***` 로 바꾼다.

| 패턴 | 예 → 저장 |
|---|---|
| `IDENTIFIED BY <x>` (CREATE/ALTER USER, CREATE DATABASE LINK, ALTER ROLE 등) | `ALTER USER a IDENTIFIED BY "Pw1"` → `ALTER USER a IDENTIFIED BY ***` |
| `IDENTIFIED BY VALUES '<hash>'` | `… IDENTIFIED BY VALUES ***` |
| `REPLACE <x>` (ALTER USER … IDENTIFIED BY n REPLACE o) | `… REPLACE ***` |
| SQL*Plus `CONNECT u/p[@db]`, `CONN u/p` | `CONNECT u/***@db` |
| `PASSWORD` 명령 다음 인자 | `PASSWORD ***` |

가림 함수는 `Oranda.Language` 에 두고(`ISecretRedactor`, TSK-02-03 이 이름만 정의) 이력과 로그가 같이 쓴다.

---

## 6. C# 모델 매핑 (TSK-02-03 이 그대로 옮김)

공통:

| 항목 | 규칙 |
|---|---|
| 선언 | `public sealed record`, 속성은 `{ get; init; }` + 기본값. **예외: `ConnectionProfile` 은 D-004 의 위치 매개변수 record 를 유지**하고 새 매개변수를 끝에 기본값과 함께 덧붙인다 |
| 컬렉션 | `IReadOnlyList<T>`, 기본값 `[]` |
| 선택 필드 | 참조형은 `T?`, 값 형식 선택 필드는 `T?` |
| 직렬화 | `Oranda.Core.Serialization.OrandaJsonContext`(소스 생성) 에 모든 루트 형식 등록. 1.3 공통 규칙 옵션 |
| 위치 | Core 는 Avalonia·ODP.NET·SQLite 를 참조하지 않는다. 파일·DB 읽기·쓰기 구현은 `Oranda.Platform` |

### 6.1 형식 목록

| JSON / 테이블 | C# 형식 | 네임스페이스 |
|---|---|---|
| `settings.json` | `AppSettings`, `UiSettings`, `EditorSettings`, `ExecutionSettings`, `GridSettings`, `HistorySettings`, `MetadataCacheSettings`, `MonitorSettings`, `WorkspaceSettings`, `SchemaBrowserSettings`, `UpdateSettings`, enum `Theme` | `Oranda.Core.Settings` |
| (공통) | `SchemaVersions`(static), `ISettingsMigration` | `Oranda.Core.Settings` |
| `connections.json` | `ConnectionsDocument`, `ConnectionProfile`(기존), `NlsSettings`, enum `AuthMode`(기존+3값), `ConnectionEnvironment`, `LicensePack` | `Oranda.Core.Connection` |
| `formatter/*.json` | `FormatterProfile`, `FormatterOptions`, `CaseOptions`, `IndentOptions`, `LineBreakOptions`, `AlignOptions`, `CommentOptions`, enum `LetterCase`, `CommaPosition`, `SelectListLayout`, `AndOrBreak`, `StatementTerminator` | `Oranda.Core.Formatting` |
| `keymaps/*.json` | `Keymap`, `KeyBinding`(+`Mac`) | `Oranda.Core.Shell` (TSK-02-05) |
| `layout.json` | `LayoutDocument`, `WindowPlacement`, `DockNode`, enum `WindowStateKind`, `DockNodeKind`, `DockOrientation` | **`Oranda.App.Shell.Layout`** (App DTO) |
| `workspaces/*.json` | `Workspace`, `WorkspaceTab`, `TextPosition` | `Oranda.Core.Workspaces` |
| `history` | `HistoryEntry`, enum `HistorySource`, `HistoryStatus` | `Oranda.Core.History` |
| `history.statement_kind` | enum `StatementKind` | `Oranda.Core.Execution` (TSK-02-03 이 `SqlStatement` 와 함께 정의, 값 이름은 3.2 표) |
| `meta_source`, `meta_object` | `MetaSource`, `MetaObject` | `Oranda.Core.Metadata` |
| `schema_version` | (코드 형식 없음, Platform 내부) | — |

JSON 이름 `layout.root.orientation` 은 C# `DockOrientation`(Avalonia `Orientation` 과 이름 충돌 회피) 이며 값은 `Horizontal`\|`Vertical`.

### 6.2 `ConnectionProfile` (기존 record 확장)

```csharp
public enum AuthMode { Default, SysDba, SysOper, SysBackup, SysDg, SysKm }
public enum ConnectionEnvironment { Development, Test, Production }
public enum LicensePack { None, Diagnostics, DiagnosticsAndTuning }

public sealed record NlsSettings
{
    public string? DateFormat { get; init; }
    public string? TimestampFormat { get; init; }
    public string? Language { get; init; }
    public string? Territory { get; init; }
    public string? Sort { get; init; }
}

public sealed record ConnectionProfile(
    string Name,
    string User,
    string DataSource,
    AuthMode Auth = AuthMode.Default,
    string? TnsAdmin = null,
    string? WalletLocation = null,
    int ConnectTimeoutSeconds = 15,
    // ── 이하 TSK-02-01 추가 (이 순서 그대로) ──
    Guid Id = default,                                  // Guid.Empty = 아직 저장 안 됨, 저장소가 발급
    ConnectionEnvironment Environment = ConnectionEnvironment.Development,
    bool ReadOnly = false,
    LicensePack LicensePack = LicensePack.None,
    int KeepAliveSeconds = 0,
    NlsSettings? Nls = null,
    string? CredentialRef = null,
    string? ProxyUser = null,
    string? Color = null,
    string? Group = null,
    DateTimeOffset? LastConnectedAt = null);

public sealed record ConnectionsDocument
{
    public int SchemaVersion { get; init; } = SchemaVersions.Connections;
    public IReadOnlyList<ConnectionProfile> Profiles { get; init; } = [];
}
```

`SysBackup`·`SysDg`·`SysKm` 은 ODP.NET `DBA Privilege` 연결 속성 값이다. Managed 드라이버 지원 여부는 접속 Task(CONN-02)가 실측하고, 안 되면 그 값을 고를 때 오류로 알린다(열거형은 유지).

### 6.3 나머지 record 필드

필드 이름·형식·기본값은 2장 표 그대로다(JSON camelCase ↔ C# PascalCase). 아래는 표만으로 애매한 것만 적는다.

| C# 형식 | 속성 (형식 = 기본값) |
|---|---|
| `AppSettings` | `SchemaVersion`(int=1), `Ui`(UiSettings=new), `Editor`, `KeymapId`(string="default"), `FormatterProfileId`(string="oranda-default"), `Execution`, `Grid`, `History`, `MetadataCache`, `Monitor`, `Workspace` — 하위 record 는 모두 `= new()` |
| `UiSettings` | `Theme`(Theme=System), `Language`(string="system") |
| `EditorSettings` | `FontFamily`(string?=null), `FontSize`(double=13), `TabSize`(int=4) |
| `ExecutionSettings` | `AutoCommit`(false), `DbmsOutput`(true), `ScriptErrorMode`(ScriptErrorMode=Ask — TRD 6장 `ScriptErrorMode` 와 같은 형식), `SplitOnBlankLine`(false) |
| `GridSettings` | `PageSize`(500), `MemoryLimitMb`(200), `InitialLobFetchSize`(4000) |
| `HistorySettings` | `Enabled`(true), `SaveBindValues`(false), `MaxEntries`(50000), `MaxAgeDays`(180) |
| `MetadataCacheSettings` | `RetentionDays`(30) |
| `MonitorSettings` | `RefreshSeconds`(5) |
| `WorkspaceSettings` | `AutosaveSeconds`(10), `RestoreOnStartup`(true), `LastWorkspaceId`(string="default") |
| `FormatterProfile` | `SchemaVersion`(1), `Id`(string, required), `Name`(string, required), `Options`(FormatterOptions=new) |
| `FormatterOptions` | `Case`(CaseOptions), `Indent`(IndentOptions), `CommaPosition`(Trailing), `OperatorSpacing`(true), `ParenInnerSpace`(false), `LineBreaks`(LineBreakOptions), `Align`(AlignOptions), `Comments`(CommentOptions), `StatementTerminator`(Keep), `HonorFormatterOff`(true) |
| `CaseOptions` | `Keyword`(Upper), `Function`(Upper), `Identifier`(AsIs) |
| `IndentOptions` | `UseTabs`(false), `Size`(4), `Subquery`(1), `Case`(1), `PlsqlBlock`(1) |
| `LineBreakOptions` | `SelectList`(OnePerLine), `JoinClause`(true), `AndOr`(Before), `InListThreshold`(5), `MaxLineWidth`(120) |
| `AlignOptions` | `Alias`(true), `Assignment`(true), `ColumnDefinition`(true), `Equals`(false) — C# 에서 `Equals` 는 `object.Equals` 와 겹치므로 속성 이름 **`EqualsSign`** + `[JsonPropertyName("equals")]` |
| `CommentOptions` | `Preserve`(true), `MaxBlankLines`(1) |
| `Keymap` | `SchemaVersion`(1), `Id`(required), `Name`(required), `BasedOn`(string?), `Bindings`(IReadOnlyList<KeyBinding>=[]) |
| `KeyBinding` | `Command`(string, required), `Keys`(IReadOnlyList<string>=[]), `When`(string?), `Mac`(IReadOnlyList<string>?) |
| `LayoutDocument` | `SchemaVersion`(1), `Window`(WindowPlacement?), `Root`(DockNode?), `HiddenTools`(IReadOnlyList<string>=[]) |
| `WindowPlacement` | `X`, `Y`, `Width`, `Height`(int), `State`(WindowStateKind=Normal), `Screen`(string?) |
| `DockNode` | `Kind`(DockNodeKind), `Orientation`(DockOrientation=Horizontal), `Proportion`(double?), `Children`(IReadOnlyList<DockNode>=[]), `Tools`(IReadOnlyList<string>=[]), `ActiveTool`(string?) |
| `Workspace` | `SchemaVersion`(1), `Id`(required), `Name`(required), `SavedAt`(DateTimeOffset), `ActiveTabId`(Guid?), `Tabs`(IReadOnlyList<WorkspaceTab>=[]), `Layout`(string="layout.json") |
| `WorkspaceTab` | `Id`(Guid), `Title`(string), `FilePath`(string?), `Content`(string?), `IsDirty`(false), `Encoding`(string="utf-8"), `Caret`(TextPosition=new(1,1)), `SelectionStart`(TextPosition?), `ScrollLine`(int=1), `ConnectionProfileId`(Guid?) |
| `TextPosition` | `readonly record struct TextPosition(int Line, int Column)` — 1부터 |
| `HistoryEntry` | `Id`(long), `ProfileId`(Guid), `ProfileName`(string), `DbKey`(string?), `ExecutedAt`(DateTimeOffset), `SqlText`(string), `SqlTruncated`(bool), `StatementKind`(StatementKind), `Source`(HistorySource), `Status`(HistoryStatus), `ElapsedMs`(long?), `RowsAffected`(long?), `ErrorCode`(string?), `ErrorMessage`(string?), `BindsJson`(string?) |
| `MetaSource` | `DbKey`(string), `ServerVersion`(string?), `RefreshedAt`(DateTimeOffset?), `LastUsedAt`(DateTimeOffset) |
| `MetaObject` | `DbKey`, `Owner`, `Name`, `Type`(string), `Status`(string?), `LastDdlTime`(DateTimeOffset?), `PayloadJson`(string?), `FetchedAt`(DateTimeOffset) |

- 「required」 는 C# `required` 한정자로 둔다.
- SQLite 열 ↔ 속성은 snake_case ↔ PascalCase 1:1. 열거형 열은 멤버 이름 문자열로 저장.
- 저장소 인터페이스(`ISettingsStore`, `IHistoryStore`, `IMetadataCache`, `IAppDataPaths`)의 메서드는 TSK-02-03 이 정한다. 이 문서는 데이터 형식만 정한다.

---

## 7. 서버에 객체를 만들지 않음 (PRD C-05)

| 데이터 | 어디에 두나 | 서버에 하는 일 |
|---|---|---|
| 접속 프로필·설정·포매터·키맵·레이아웃·작업 공간 | `{data}` JSON | 없음 |
| 실행 이력 | `{data}/oranda.db` `history` | 없음(서버 감사는 SAFE-05 의 module/action 으로만) |
| 메타데이터 캐시 | `{data}/oranda.db` `meta_*` | 사전 뷰 `SELECT` 만 |
| 자격 증명 | DPAPI 파일 / 키체인 | 없음 |
| 이름 붙인 스크립트(EXEC-09) | 사용자가 고른 로컬 폴더(이 문서의 파일 아님) | 없음 |

이 문서의 어떤 스키마도 서버 테이블·패키지·시퀀스·디렉터리 객체를 요구하지 않는다. 서버 쪽 저장(Orange 의 ORANGE 스키마 같은 것)은 두지 않는다.

---

## 8. 예제 파일

| 파일 | 스키마 |
|---|---|
| [`examples/settings.json`](examples/settings.json) | 2.1 |
| [`examples/connections.json`](examples/connections.json) | 2.2 |
| [`examples/formatter/team-default.json`](examples/formatter/team-default.json) | 2.3 |
| [`examples/keymaps/my-keys.json`](examples/keymaps/my-keys.json) | 2.4 (명령 ID는 자리표시, TSK-02-02 가 확정) |
| [`examples/layout.json`](examples/layout.json) | 2.5 (도구 ID는 자리표시, TSK-02-02 가 확정) |
| [`examples/workspaces/default.json`](examples/workspaces/default.json) | 2.6 |
| [`examples/oranda-db-v1.sql`](examples/oranda-db-v1.sql) | 3.2 |

모든 예제에 비밀번호·토큰이 없다. 계약 Task 는 이 예제들을 역직렬화 → 다시 직렬화하는 왕복 시험에 쓴다.

---

## 9. TRD 5장과 다른 점 (정본 반영 요청)

| TRD | 이 문서 | 이유 |
|---|---|---|
| 5.1 `"licensePacks": []\|["Diagnostics"]\|["Diagnostics","Tuning"]` | `"licensePack": "None"\|"Diagnostics"\|"DiagnosticsAndTuning"` | Tuning 은 Diagnostics 전제라 조합이 3개뿐. 서버 `control_management_pack_access` 값(NONE/DIAGNOSTIC/DIAGNOSTIC+TUNING)과 1:1 |
| 5.1 `"environment"`, 필드 몇 개 | `connectTimeoutSeconds`, `proxyUser`, `color`, `group`, `lastConnectedAt`, `nls.timestampFormat/territory/sort` 추가 | CONN-02 프록시, CONN-06 색, CONN-08 정렬, 시작 화면 최근 접속 |
| 5.1 `credentialRef: "oranda:b1f6…"` | `oranda:conn:{guid}` | 나중에 다른 종류 비밀(토큰) 키와 구분 |
| 5.2 옵션이 루트에 펼쳐짐 | `id`·`name` + `options` 객체 | `ISqlFormatter.Format(text, FormatterOptions)` 에 그대로 넘기고, 프로필 메타와 옵션을 분리 |
| 5.3 `history` 9열 | `profile_name`, `db_key`, `sql_truncated`, `source`, `status`, `error_message` 추가, FTS5 trigram 색인 | EXEC-07 「결과」·「검색」, 프로필 삭제 후 표시 |
| 5.3 `schema_version(version)` | `version PK, applied_at, description` | 적용 기록 남김 |
| 5.3 (없음) | `meta_source` 테이블, `meta_object.status/fetched_at` | 캐시 보존 정책·갱신 시각 |
| 4.12 「설정」 | `settings.json` 스키마 명시 | 이력 보존·바인드 저장 여부 등 설정 자리 |
