# TRD - Oranda MyBatis 작업 공간

> version: 1.0
> date: 2026-10-23
> 상위 TRD = `docs/TRD.md`(고치지 않음). 이 문서는 MyBatis 작업 공간(`docs/oranda1/PRD.md`)의 기술 결정만 다룬다.

## 1. 기술 스택

- 상위와 같음: .NET 10 SDK 10.0.402, Avalonia 12.1.4, CommunityToolkit.Mvvm, AvaloniaEdit(편집기), ODP.NET Core 23.26.301, 로컬 데이터 SQLite(Microsoft.Data.Sqlite)
- xml 읽기: System.Xml.XmlReader(`DtdProcessing.Ignore`, `XmlResolver = null`) + IXmlLineInfo 로 요소 시작 위치를 얻고, 원문 문자열을 직접 훑어 범위를 잡는다
- 새 외부 패키지는 쓰지 않는다

## 2. 계층과 위치

| 계층 | 위치 | 내용 |
|------|------|------|
| Language | `src/Oranda.Language/MyBatis/` | 매퍼 원문 모델(MyBatisMapperDocument), 동적 SQL 계산기(MyBatisEvaluator), test 식 해석기(OgnlLite), 파라미터 뽑기 |
| Core | `src/Oranda.Core/MyBatis/` | 시나리오 저장소 인터페이스(IMyBatisScenarioStore)와 모델 |
| Platform/App 데이터 | 로컬 SQLite | 시나리오 저장 구현 |
| App | `src/Oranda.App/Features/MyBatisWorkspace/` | 작업 공간 ViewModel, 「문장」·「파라미터 · 시나리오」 도구 창, 펼친 SQL·조합 검사 패널, 저장 확인·바꾸기 대화 상자, 명령 |

규칙: Language·Core 는 Avalonia·ODP.NET 을 참조하지 않는다. 실행은 IExecutionService(정책 관문)만 쓴다. UI 스레드에서 DB 를 부르지 않는다.

## 3. 데이터 모델

### 3.1 매퍼 원문 모델 (메모리)

- MyBatisMapperDocument: 원문 문자열, 인코딩·BOM·줄 끝·끝 줄바꿈 정보, namespace, 요소 목록, 원문 해시(SHA-256)
- MyBatisElement: 종류(select·insert·update·delete·sql·resultMap·기타), id, 속성 목록, 요소 범위(시작·끝 오프셋), 안쪽 본문 범위
- 편집: 바꾸기 목록(범위 → 새 글)을 뒤에서부터 적용해 새 원문을 만든다. 새 문장은 `</mapper>` 바로 앞, 앞 문장 들여쓰기를 따른다

### 3.2 계산 결과 (메모리)

- EvalResult: 펼친 SQL, 바인드 목록(이름·타입 힌트·값), 치환 목록(이름·값), 블록 목록(원문 범위·켬/끔·근거 식), 계산 못 한 식 목록(원문 범위·이유)
- 파라미터 정보: 이름(점 경로), 쓰인 곳(test·#{}·${}·foreach·bind), 타입 힌트(jdbcType·javaType)

### 3.3 시나리오 (SQLite 로컬 데이터)

- 표 `mybatis_scenario`: id, mapper_path(정규화한 절대 경로), namespace, statement_id, name, values_json, sort_order, created_at, updated_at
- 문장 id 를 바꾸면 statement_id 를 같이 바꾼다. 매퍼 파일을 옮기면 namespace + statement_id 로 다시 찾는다
- values_json 에 비밀 이름(pass·pwd·secret·token 이 든 이름) 값은 넣지 않는다

## 4. 인터페이스(계약)

- `IMyBatisMapperDocument Parse(string text)` / `string Apply(IReadOnlyList<MapperEdit> edits)` / `MapperWriteCheck Check(string newText, string diskHash)`
- `EvalResult Evaluate(MyBatisElement statement, IMyBatisFragmentResolver fragments, IReadOnlyDictionary<string, object?> values, IReadOnlyDictionary<TextRange, bool>? overrides)`
- `IReadOnlyList<MyBatisParameter> ExtractParameters(MyBatisElement statement, IMyBatisFragmentResolver fragments)`
- `IMyBatisScenarioStore`: List·Save·Rename·Delete·MoveStatement(mapperPath, oldId, newId)
- 조합 검사: `IEnumerable<ConditionCombination> Combinations(EvalResult allOn, int fullLimit = 8)`

## 5. 저장·파일 감시

- 원자적 쓰기: 같은 폴더 임시 파일에 쓰고 바꾸기(File.Replace / rename). 원래 인코딩·BOM·줄 끝 유지
- 쓰기 전 디스크 해시를 다시 읽어 연 때의 해시와 비교한다. 다르면 3방향 문장 단위 병합(연 때 원문 · 디스크 · 내 것)으로 안 고친 문장은 디스크 것, 양쪽 고친 문장만 묻는다
- FileSystemWatcher(디바운스 300 ms)로 밖의 변경을 알린다

## 6. 화면 배선

- 문서 종류 `MyBatis` 를 작업 공간 문서 종류에 더한다. 도구 창 보임 규칙은 TSK-13-41(문서별 도구 창) 규칙에 「MyBatis 문서」 조건을 더한다
- 명령은 `[OrandaCommand]` 특성(자동 수집)으로 등록한다: `oranda.mybatis.runScenario`(F9), `oranda.mybatis.save`(Ctrl+S, MyBatis 문서일 때), `oranda.mybatis.checkCombinations`, `oranda.mybatis.copyExpanded`, `oranda.mybatis.convertWorkspace`, `oranda.mybatis.openFolder`, `oranda.mybatis.findStatement`
- 편집기 강조: AvaloniaEdit 에 MyBatis 모드(XML 태그·속성, `#{}`·`${}`, SQL 구간은 기존 SQL 강조 위임)

## 7. 비기능 요구사항 (NFR)

- 매퍼 1MB 파싱 200 ms 이하, 계산 50 ms 이하(문장 500줄), 조합 256개 구문 분석 3 s 이하
- 계산기와 식 해석기는 반사(reflection)·동적 코드 생성을 쓰지 않는다
- 시험: Language 단위 시험(왕복·태그·식 골든), App Headless 시험(화면·명령), Oracle 통합 시험(실행·실행 계획) 1건 이상

## 8. 제약 사항

- 상위 문서를 고치지 않는다. 결정은 `docs/oranda1/decisions.md` 에 남긴다
- 공유 파일(SqlEditorHost·WorkspaceFiles·도킹 규칙)은 계약 Task 가 정한 편집 지점에서만 고친다

## 9. 수락 기준 (기술)

- MB-01 왕복 시험: BOM·CRLF·CDATA·주석·엔티티·한글·DOCTYPE 표본에서 무변경 저장 결과가 원본과 바이트까지 같다
- MB-02 골든 시험: 표본 문장 × 값 묶음의 펼친 SQL 이 MyBatis 3.5 실제 출력(공백 정규화)과 같다
- 7절 성능 수치를 시험 PC 에서 잰 값이 기준 이하다
- `dotnet build Oranda.sln -warnaserror` 와 `dotnet test` 가 통과한다
