# Catalog/app/

App 이 직접 보내던 문장(세션 종료·연결 끊기, 10053 트레이스, STATISTICS_LEVEL, 잠긴 행 읽기 등)을 모아 둔다. id 는 `app.` 로 시작한다.
`@KEY@` 자리(대문자, `@PFX@` 와 같은 모양)는 `EmbeddedCatalog.Text(id, (키, 값)…)` 가 한 번에 바꾼다(키는 소문자로 넘겨도 됨)(바인드로 보낼 수 없는 식별자·리터럴 자리, 값은 호출자가 이스케이프).
DBMS 별로 달라지는 문장이 여기에 모인다(Tibero 용은 `src/Oranda.Tibero/Catalog/` 에 같은 id 로 둔다, docs/design/tibero.md §2).
SELECT 가 아닌 문장이 들어 있으므로 `lock.`·`tuning.` 같은 「조회 전용」 검사 대상이 아니다.
