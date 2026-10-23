-- oranda.db schema v3 (TSK-01-02, docs/oranda1/design.md 6장)
-- 저장소 셋(이력·캐시·일지)이 처음 열 때 겹쳐도 안전하도록 IF NOT EXISTS 로 멱등하게 쓴다.
-- MyBatis 시나리오: 문장 하나의 파라미터 값 묶음을 앱 로컬에 둔다. 매퍼 폴더에는 아무것도 만들지 않는다.

CREATE TABLE IF NOT EXISTS mybatis_scenario (
  id           INTEGER PRIMARY KEY,
  mapper_path  TEXT    NOT NULL,
  namespace    TEXT,
  statement_id TEXT    NOT NULL,
  name         TEXT    NOT NULL,
  values_json  TEXT    NOT NULL,
  sort_order   INTEGER NOT NULL,
  created_at   TEXT    NOT NULL,
  updated_at   TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_mybatis_scenario_statement ON mybatis_scenario(mapper_path, statement_id, sort_order);
CREATE INDEX IF NOT EXISTS ix_mybatis_scenario_namespace ON mybatis_scenario(namespace, statement_id);
