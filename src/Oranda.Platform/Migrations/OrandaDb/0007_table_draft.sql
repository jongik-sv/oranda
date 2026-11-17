-- oranda.db schema v7 (새 테이블 작업 저장, 설계 docs/superpowers/specs/2026-11-17-table-editor-design.md 18.7, TE-43)
-- 「✎ 새 테이블」 탭의 편집 모델(TableDraft JSON)을 30초마다·탭을 닫을 때 덮어쓴다. 실행 성공·「버리기」 때 지운다.
-- open_by = 이 저장분을 탭에 연 앱 인스턴스(프로세스 id@시작 시각). NULL = 닫기에서 「남겨 두기」한 것(소유자 노드 「만들던 새 테이블…」로만 다시 엶).
--           값이 있는데 그 인스턴스가 이미 끝났으면 앱을 다시 켤 때 작업 공간 복원이 탭으로 되살린다. 앱 두 개가 같은 파일을 써도
--           한 저장분은 한 인스턴스만 쥔다(쓰기·지우기는 open_by 를 조건으로 한다).
-- id 는 AUTOINCREMENT — 지운 id 를 새 저장분이 다시 받지 않게(아직 그 id 를 쥔 탭과 겹치지 않게).
-- 이미 적용된 DB 에서 다시 실행돼도 안전하게(멱등하게) 쓴다.

CREATE TABLE IF NOT EXISTS table_draft (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  connection_name TEXT    NOT NULL,
  dbms            TEXT    NOT NULL CHECK (dbms IN ('Oracle','Tibero')),
  owner           TEXT    NOT NULL,
  name            TEXT    NOT NULL DEFAULT '',
  model_json      TEXT    NOT NULL,
  updated_at      TEXT    NOT NULL,
  open_by         TEXT
);
CREATE INDEX IF NOT EXISTS ix_table_draft_owner ON table_draft(connection_name, owner, updated_at DESC);
