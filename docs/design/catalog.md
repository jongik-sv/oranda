# SQL 카탈로그 설계 (TSK-02-04)

딕셔너리 SQL 은 코드에 흩어 두지 않고 `src/Oranda.Oracle/Catalog/**/*.sql` 파일로만 둔다(TRD 4.2, TC-03, TC-06).
파일 하나 = 쿼리 하나. 맨 앞 머리 주석에 메타데이터를 단다.

## 1. 구성

| 위치 | 내용 |
|------|------|
| `src/Oranda.Core/Catalog/ISqlCatalog.cs` | 계약 `CatalogQuery Get(string id, ServerInfo server)` (TRD 6장) |
| `src/Oranda.Core/Catalog/CatalogQuery.cs` | `CatalogQuery(Header, Sql)`, 원문 `CatalogSource(Path, Text)` |
| `src/Oranda.Core/Catalog/CatalogHeader.cs` | 머리 주석 record, `CatalogLicense`, `CatalogRac`, 하한 `LowestMinVersion = 19` |
| `src/Oranda.Core/Catalog/CatalogParser.cs` | 머리 주석 파서, 검사 규칙 이름 `CatalogRules`, 문제 `CatalogProblem` |
| `src/Oranda.Core/Catalog/SqlCatalog.cs` | `ISqlCatalog` 구현(검사 + 선택), `CatalogFormatException`, `CatalogQueryNotFoundException` |
| `src/Oranda.Oracle/Catalog/EmbeddedCatalog.cs` | 임베디드 리소스 자동 수집 `ReadSources(assembly)`, `Load()` |
| `src/Oranda.Oracle/Oranda.Oracle.csproj` | `<EmbeddedResource Include="Catalog\**\*.sql" LogicalName="Catalog/%(RecursiveDir)%(Filename)%(Extension)" />` 한 줄 |
| `tests/Oranda.Oracle.Tests/Catalog/` | 카탈로그 검사 시험(`Category=Catalog`) |

Core 는 원문 목록만 받는다(ODP.NET·어셈블리 리소스 모름). 리소스를 읽는 일은 Oracle 쪽 `EmbeddedCatalog` 가 한다.

## 2. 머리 주석 형식

파일 맨 앞의 **연속된 `--` 줄**이 머리 블록이다. 그 안의 `-- key: value` 줄이 필드이고, 키 형식이 아닌 `--` 줄은 자유 주석이다.
`--` 로 시작하지 않는 첫 줄(빈 줄 포함)에서 머리 블록이 끝나고 나머지가 SQL 본문이다. 값 뒤의 `-- 설명` 은 버린다.

| 키 | 필수 | 값 | 기본 | 뜻 |
|----|:---:|----|------|----|
| `id` | ○ | `폴더.이름` 소문자·숫자·`_`, 점으로 2마디 이상 (예: `session.blocking_tree`) | - | 기능 코드가 부르는 이름. **첫 마디 = 소유 폴더 이름** |
| `min-version` | ○ | 서버 주 버전 정수. 19 이상 | - | 이 파일을 쓸 수 있는 가장 낮은 서버 버전. 23ai/26ai 전용은 `23` |
| `license` | ○ | `none` \| `diagnostics` \| `tuning` | - | 필요한 관리 팩(TC-06) |
| `requires` | | 권한·뷰 이름, 쉼표 구분 | 빈 목록 | 접속 점검 결과와 대조할 대상(예: `v$session, v$sql`) |
| `rac` | | `v` \| `gv` | `v` | `gv` = `gv$` 뷰로 모든 인스턴스를 본다(`inst_id` 포함) |

그 밖의 키, 같은 키 두 번은 오류다(오타 방지).

### license 와 접속 프로필 `LicensePack` 대조

카탈로그는 "필요한 팩", 접속 프로필(`Oranda.Core.Connection.LicensePack`, data-model.md)은 "보유 선언"이다. 대조는 정책 관문(TSK-06-01)이 한다.

| `license:` | `CatalogLicense` | 실행 가능한 `LicensePack` |
|------------|------------------|---------------------------|
| `none` | `None` | 모두 |
| `diagnostics` | `Diagnostics` | `Diagnostics`, `DiagnosticsAndTuning` |
| `tuning` | `Tuning` | `DiagnosticsAndTuning` |

선언이 모자라면 쿼리 문자열을 드라이버로 보내지 않는다(TRD 4.2).

### 예시

`src/Oranda.Oracle/Catalog/session/list.sql`

```sql
-- 세션 목록(세션 모니터 기본 격자)
-- id: session.list
-- min-version: 19
-- requires: v$session          -- 권한 판단용
-- license: none
-- rac: gv
SELECT s.inst_id, s.sid, s.serial#, s.username
  FROM gv$session s
```

23 이상에서 다른 SQL 을 쓰려면 같은 id 로 파일을 하나 더 둔다: `session/list.23.sql` 에 `-- min-version: 23`.

## 3. 파일 이름·폴더 관례

| 항목 | 규칙 |
|------|------|
| 위치 | `src/Oranda.Oracle/Catalog/<폴더>/<이름>.sql`. Catalog/ 바로 아래 파일은 id 첫 마디와 맞지 않아 검사에 걸린다 |
| 이름 | id 의 나머지 마디를 그대로(권장). 버전 변형은 `<이름>.<min-version>.sql` (권장) |
| 수집 | csproj 한 줄이 `Catalog/**/*.sql` 을 리소스 `Catalog/<폴더>/<파일>` 로 싣는다. 기능 Task 는 **파일만 더한다**(공유 진입점·목록 수정 없음) |
| 인코딩 | UTF-8 |

## 4. 선택 규칙

`ISqlCatalog.Get(id, server)` 는 `server.MajorVersion` 으로 고른다.

1. 같은 `id` 후보 가운데 `min-version ≤ 서버 주 버전` 인 것만 남긴다.
2. 그중 `min-version` 이 가장 높은 것을 돌려준다.
3. 하나도 없으면 `CatalogQueryNotFoundException(id, 버전)`.

| 후보 | 서버 19 | 서버 21 | 서버 23 | 서버 26(23.26) | 서버 18 |
|------|---------|---------|---------|----------------|---------|
| `list.sql`(19), `list.23.sql`(23) | 19 | 19 | 23 | 23 | 없음 |

나중에 11.2·12c·18c 를 지원하면 `CatalogHeader.LowestMinVersion` 을 내리고 같은 id 로 `min-version: 11` 파일만 더한다(TC-03). 선택 규칙은 그대로다.
지원 버전 관문(19.x·23.x 만 접속 허용)은 TSK-03-03 의 설정 한 곳이 맡고, 카탈로그는 버전을 거르지 않는다.

## 5. 검사 규칙 (빌드 검사)

`SqlCatalog` 는 만들 때 전체를 검사하고 문제가 있으면 `CatalogFormatException`(문제 전체 목록)을 낸다.
같은 검사를 시험 `Category=Catalog` 가 매 빌드 돌린다(기본 `dotnet test` 에 포함, Integration 처럼 빼지 않음).

| 규칙(`CatalogRules`) | 잡는 것 |
|----------------------|---------|
| `header-missing` | 머리 주석 필드가 하나도 없다 |
| `header-invalid` | 필수 키(id·min-version·license) 없음, 값 형식 오류, 모르는 키, 같은 키 두 번, id 형식 오류 |
| `min-version-too-low` | `min-version` 이 19(지원 하한) 미만 |
| `id-folder-mismatch` | id 첫 마디가 소유 폴더 이름과 다르다 |
| `duplicate-id` | 같은 `id` + 같은 `min-version` 이 두 파일 이상 |
| `body-empty` | 머리 주석 뒤 SQL 본문이 없다 |

TRD 4.2 의 "19 미만 대상에 `FETCH FIRST` 금지" 는 지금 19 미만 파일을 둘 수 없으므로 `min-version-too-low` 로 덮인다. 구버전을 추가할 때 규칙을 더한다.

| 시험 | 단정 |
|------|------|
| `EmbeddedCatalogTests.Every_sql_file_under_oracle_catalog_is_embedded` | 디스크의 `Catalog/**/*.sql` 목록 = 수집된 리소스 목록(수집 누락 없음) |
| `EmbeddedCatalogTests.Product_catalog_passes_build_checks` | 제품 카탈로그 전체가 위 규칙을 통과 |
| `EmbeddedCatalogTests.Collects_*`, `Collected_fixtures_*` | 시험 고정 파일(`tests/Oranda.Oracle.Tests/Catalog/Fixtures`)로 수집·선택 동작 |
| `CatalogParserTests`, `SqlCatalogTests` | 규칙별 잡힘, 선택 규칙 |

공유 시험은 틀 존재·수집 동작만 단정한다. 제품 카탈로그의 파일 개수·id 목록은 단정하지 않는다.

실행: `dotnet test tests/Oranda.Oracle.Tests --filter Category=Catalog`

## 6. 폴더 소유 단위

| 폴더 | id 접두어 | 소유 Task | 쓰는 곳 |
|------|-----------|-----------|---------|
| `session/` | `session.` | TSK-09-01 세션 모니터 | 목록·블로킹 트리·상세·Kill·Trace |
| `sql/` | `sql.` | TSK-09-02 SQL 모니터(Top SQL) | `gv$sql`, `v$sql_bind_capture` |
| `meta/` | `meta.` | TSK-04-04 메타데이터 제공자·캐시 | 딕셔너리 조회, 23 전용 객체는 `min-version: 23` |
| `plan/` | `plan.` | TSK-07-01 실행 계획 | `DISPLAY_CURSOR` 등(라이선스 대상은 쓰지 않음) |

새 기능이 카탈로그가 필요하면 새 폴더를 만들고(id 접두어 = 폴더 이름) 아래 표에 한 줄 더한다. 한 폴더는 한 Task 만 소유한다.

## 기능 Task 편집 지점

| Task | 소유 폴더 | 시험 파일 |
|------|-----------|-----------|
| TSK-09-01 | `src/Oranda.Oracle/Catalog/session/` | `tests/Oranda.Oracle.Tests/Catalog/SessionCatalogTests.cs` |
| TSK-09-02 | `src/Oranda.Oracle/Catalog/sql/` | `tests/Oranda.Oracle.Tests/Catalog/SqlMonitorCatalogTests.cs` |
| TSK-04-04 | `src/Oranda.Oracle/Catalog/meta/` | `tests/Oranda.Oracle.Tests/Catalog/MetaCatalogTests.cs` |
| TSK-07-01 | `src/Oranda.Oracle/Catalog/plan/` | `tests/Oranda.Oracle.Tests/Catalog/PlanCatalogTests.cs` |

- 기능 Task 는 자기 폴더의 `.sql` 과 자기 시험 파일만 고친다. 로더(`EmbeddedCatalog`, `SqlCatalog`, `CatalogParser`), csproj 의 수집 줄, 공유 시험(`EmbeddedCatalogTests`·`CatalogParserTests`·`SqlCatalogTests`)은 고치지 않는다.
- 기능 Task 시험은 `[Trait("Category", "Catalog")]` 를 붙이고 자기 id 가 있는지(`EmbeddedCatalog.Load().Get("session.list", 19)`)·라이선스 태그가 맞는지를 단정한다. 실제 DB 실행 시험은 `Oranda.Oracle.IntegrationTests` 에 `Category=Integration` 으로 둔다.
- 폴더의 `README.md` 는 폴더 유지용이며 수집 대상이 아니다. 첫 `.sql` 을 넣을 때 지워도 된다.

## 결정

| 결정 | 이유 |
|------|------|
| 카탈로그 시험은 새 프로젝트 `tests/Oranda.Oracle.Tests` 에 둔다 | 수집 검사는 Oranda.Oracle 어셈블리 리소스를 읽어야 하는데, `Oranda.Core.Tests` 가 Oracle 을 참조하면 Core 시험이 드라이버를 끌고 온다. `Oranda.Oracle.IntegrationTests` 는 컨테이너 시험용이라 성격이 다르다. DB 없는 Oracle 단위 시험의 자리로 쓴다 |
| 파서·선택·검사는 Core, 리소스 수집만 Oracle | TRD 3: "SQL 카탈로그 로더" 는 Core. Core 는 원문 목록만 받아 ODP.NET·어셈블리 의존이 없다 |
| `license` 는 카탈로그 전용 `CatalogLicense(None/Diagnostics/Tuning)` | 쿼리 쪽은 "필요한 팩", 프로필 쪽 `LicensePack` 은 "보유 조합". 뜻이 달라 별도 형식으로 두고 대조표(2절)로 잇는다. `LicensePack` 은 중복 정의하지 않는다 |
| `min-version` 은 주 버전 정수 | 지원 단위가 19·23 이고 구버전 추가도 `11` 같은 주 버전으로 충분하다 |
| id 첫 마디 = 폴더 이름을 검사한다 | 폴더 소유 단위가 id 공간까지 나누므로 두 Task 가 같은 id 를 만들 수 없다 |
| 리소스 이름은 `LogicalName="Catalog/…"` 로 폴더 경로를 보존 | 기본 리소스 이름은 폴더 구분이 점으로 바뀌어 `list.23.sql` 같은 이름과 섞인다 |
