-- oranda.db schema v2 (TSK-13-07, docs/design/data-model.md 3.2.1)
-- 저장소 셋(이력·캐시·일지)이 처음 열 때 겹쳐도 안전하도록 IF NOT EXISTS 로 멱등하게 쓴다.
-- 변경 일지: DDL 실행 직전·PL/SQL 컴파일 직전의 서버 DDL 을 로컬에 보관한다. 서버에는 아무것도 설치하지 않는다.

CREATE TABLE IF NOT EXISTS change_journal (
  id                INTEGER PRIMARY KEY,
  captured_at       TEXT    NOT NULL,
  profile_id        TEXT    NOT NULL,
  profile_name      TEXT    NOT NULL,
  db_key            TEXT    NOT NULL,
  server_name       TEXT,
  owner             TEXT    NOT NULL,
  name              TEXT    NOT NULL,
  object_type       TEXT    NOT NULL,
  cause             TEXT    NOT NULL CHECK (cause IN ('Ddl','Compile')),
  statement_summary TEXT,
  previous_ddl      TEXT,
  content_hash      TEXT,
  failure_reason    TEXT
);
CREATE INDEX IF NOT EXISTS ix_change_journal_object ON change_journal(db_key, owner, name, object_type, id DESC);
CREATE INDEX IF NOT EXISTS ix_change_journal_time   ON change_journal(captured_at DESC);
