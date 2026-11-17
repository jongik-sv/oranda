-- oranda.db schema v6 (테이블 편집기·반영 묶음, 설계 docs/superpowers/specs/2026-11-17-table-editor-design.md 9.1, TE-13·TE-35)
-- 1) history.source CHECK 에 'DataCopy'(빠져 있어 데이터 복사 실행 이력이 남지 않았다)·'TableEditor'·'ChangeBundle' 추가 — 0004 와 같은 방식으로 표를 다시 만든다.
-- 2) 반영 묶음: change_bundle(묶음)·change_bundle_item(문장 전문, 비밀은 가린 뒤)·change_bundle_run(환경별 실행 기록).
-- 이미 적용된 DB 에서 다시 실행돼도 안전하게(멱등하게) 쓴다(0004 와 같은 방식).

-- 0001 을 거치지 않은 DB(시험용으로 schema_version 만 만든 파일 등)에서도 아래 복사가 실패하지 않게 표가 없으면 먼저 만든다.
CREATE TABLE IF NOT EXISTS history (
  id             INTEGER PRIMARY KEY,
  profile_id     TEXT    NOT NULL,
  profile_name   TEXT    NOT NULL,
  db_key         TEXT,
  executed_at    TEXT    NOT NULL,
  sql_text       TEXT    NOT NULL,
  sql_truncated  INTEGER NOT NULL DEFAULT 0,
  statement_kind TEXT    NOT NULL,
  source         TEXT    NOT NULL,
  status         TEXT    NOT NULL,
  elapsed_ms     INTEGER,
  rows_affected  INTEGER,
  error_code     TEXT,
  error_message  TEXT,
  binds_json     TEXT
);
CREATE VIRTUAL TABLE IF NOT EXISTS history_fts USING fts5(
  sql_text,
  content = 'history',
  content_rowid = 'id',
  tokenize = 'trigram'
);

DROP TABLE IF EXISTS history_new;

CREATE TABLE history_new (
  id             INTEGER PRIMARY KEY,
  profile_id     TEXT    NOT NULL,
  profile_name   TEXT    NOT NULL,
  db_key         TEXT,
  executed_at    TEXT    NOT NULL,
  sql_text       TEXT    NOT NULL,
  sql_truncated  INTEGER NOT NULL DEFAULT 0 CHECK (sql_truncated IN (0, 1)),
  statement_kind TEXT    NOT NULL CHECK (statement_kind IN
                   ('Query','Dml','Ddl','PlSql','TransactionControl','SqlPlus','Other')),
  source         TEXT    NOT NULL CHECK (source IN
                   ('Editor','Script','GridEdit','SchemaBrowser','Monitor','Extension','Agent','DataCopy','TableEditor','ChangeBundle')),
  status         TEXT    NOT NULL CHECK (status IN
                   ('Succeeded','Failed','Cancelled','Denied')),
  elapsed_ms     INTEGER,
  rows_affected  INTEGER,
  error_code     TEXT,
  error_message  TEXT,
  binds_json     TEXT
);

INSERT INTO history_new (id, profile_id, profile_name, db_key, executed_at, sql_text, sql_truncated,
                         statement_kind, source, status, elapsed_ms, rows_affected, error_code, error_message, binds_json)
SELECT id, profile_id, profile_name, db_key, executed_at, sql_text, sql_truncated,
       statement_kind, source, status, elapsed_ms, rows_affected, error_code, error_message, binds_json
FROM history;

-- 표를 지우면 그 표의 인덱스와 history_fts 를 갱신하는 트리거(history_ai·ad·au)도 함께 사라진다.
DROP TABLE history;
ALTER TABLE history_new RENAME TO history;

CREATE INDEX IF NOT EXISTS ix_history_profile_time ON history(profile_id, executed_at DESC);
CREATE INDEX IF NOT EXISTS ix_history_time         ON history(executed_at DESC);

CREATE TRIGGER IF NOT EXISTS history_ai AFTER INSERT ON history BEGIN
  INSERT INTO history_fts(rowid, sql_text) VALUES (new.id, new.sql_text);
END;
CREATE TRIGGER IF NOT EXISTS history_ad AFTER DELETE ON history BEGIN
  INSERT INTO history_fts(history_fts, rowid, sql_text) VALUES ('delete', old.id, old.sql_text);
END;
CREATE TRIGGER IF NOT EXISTS history_au AFTER UPDATE OF sql_text ON history BEGIN
  INSERT INTO history_fts(history_fts, rowid, sql_text) VALUES ('delete', old.id, old.sql_text);
  INSERT INTO history_fts(rowid, sql_text) VALUES (new.id, new.sql_text);
END;

-- 같은 rowid 로 옮겼으므로 색인은 그대로 맞지만, 다시 실행되는 경우까지 포함해 한 번 다시 만든다.
INSERT INTO history_fts(history_fts) VALUES ('rebuild');

CREATE TABLE IF NOT EXISTS change_bundle (
  id             INTEGER PRIMARY KEY,
  name           TEXT    NOT NULL,
  created_at     TEXT    NOT NULL,
  updated_at     TEXT    NOT NULL,
  dbms           TEXT    CHECK (dbms IS NULL OR dbms IN ('Oracle','Tibero')),
  target_owners  TEXT    NOT NULL DEFAULT '[]',
  is_current     INTEGER NOT NULL DEFAULT 0 CHECK (is_current IN (0, 1))
);
-- 「지금 묶음」은 많아야 하나.
CREATE UNIQUE INDEX IF NOT EXISTS ux_change_bundle_current ON change_bundle(is_current) WHERE is_current = 1;

CREATE TABLE IF NOT EXISTS change_bundle_item (
  id             INTEGER PRIMARY KEY,
  bundle_id      INTEGER NOT NULL REFERENCES change_bundle(id) ON DELETE CASCADE,
  position       INTEGER NOT NULL,
  statement_text TEXT    NOT NULL,
  source         TEXT    NOT NULL CHECK (source IN ('TableEditor','SqlEditor','Manual')),
  target_owner   TEXT,
  target_name    TEXT,
  checked        INTEGER NOT NULL DEFAULT 1 CHECK (checked IN (0, 1)),
  added_at       TEXT    NOT NULL,
  statement_hash TEXT    NOT NULL,
  cautions       INTEGER NOT NULL DEFAULT 0,
  warning        TEXT
);
CREATE INDEX IF NOT EXISTS ix_change_bundle_item_order ON change_bundle_item(bundle_id, position);
CREATE INDEX IF NOT EXISTS ix_change_bundle_item_hash  ON change_bundle_item(bundle_id, statement_hash);

CREATE TABLE IF NOT EXISTS change_bundle_run (
  id              INTEGER PRIMARY KEY,
  item_id         INTEGER NOT NULL REFERENCES change_bundle_item(id) ON DELETE CASCADE,
  environment     TEXT    NOT NULL CHECK (environment IN ('Development','Test','Production')),
  connection_name TEXT    NOT NULL,
  ran_at          TEXT    NOT NULL,
  succeeded       INTEGER NOT NULL CHECK (succeeded IN (0, 1))
);
CREATE INDEX IF NOT EXISTS ix_change_bundle_run_item ON change_bundle_run(item_id, environment, ran_at DESC);
