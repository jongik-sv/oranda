-- oranda.db schema v1 (TSK-02-01, docs/design/data-model.md 3장)
-- 앱 마이그레이션 0001_init.sql 과 같은 내용. 연결마다 아래 PRAGMA 를 먼저 건다:
--   PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;
--   PRAGMA foreign_keys = ON;  PRAGMA busy_timeout = 5000;
-- 마이그레이션 실행기가 BEGIN IMMEDIATE ... COMMIT 으로 감싼다.

CREATE TABLE schema_version (
  version     INTEGER PRIMARY KEY,
  applied_at  TEXT    NOT NULL,
  description TEXT    NOT NULL
);

CREATE TABLE history (
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
                   ('Editor','Script','GridEdit','SchemaBrowser','Monitor','Extension')),
  status         TEXT    NOT NULL CHECK (status IN
                   ('Succeeded','Failed','Cancelled','Denied')),
  elapsed_ms     INTEGER,
  rows_affected  INTEGER,
  error_code     TEXT,
  error_message  TEXT,
  binds_json     TEXT
);
CREATE INDEX ix_history_profile_time ON history(profile_id, executed_at DESC);
CREATE INDEX ix_history_time         ON history(executed_at DESC);

CREATE VIRTUAL TABLE history_fts USING fts5(
  sql_text,
  content = 'history',
  content_rowid = 'id',
  tokenize = 'trigram'
);

CREATE TRIGGER history_ai AFTER INSERT ON history BEGIN
  INSERT INTO history_fts(rowid, sql_text) VALUES (new.id, new.sql_text);
END;
CREATE TRIGGER history_ad AFTER DELETE ON history BEGIN
  INSERT INTO history_fts(history_fts, rowid, sql_text) VALUES ('delete', old.id, old.sql_text);
END;
CREATE TRIGGER history_au AFTER UPDATE OF sql_text ON history BEGIN
  INSERT INTO history_fts(history_fts, rowid, sql_text) VALUES ('delete', old.id, old.sql_text);
  INSERT INTO history_fts(rowid, sql_text) VALUES (new.id, new.sql_text);
END;

CREATE TABLE meta_source (
  db_key         TEXT PRIMARY KEY,
  server_version TEXT,
  refreshed_at   TEXT,
  last_used_at   TEXT NOT NULL
);

CREATE TABLE meta_object (
  db_key        TEXT NOT NULL REFERENCES meta_source(db_key) ON DELETE CASCADE,
  owner         TEXT NOT NULL,
  name          TEXT NOT NULL,
  type          TEXT NOT NULL,
  status        TEXT,
  last_ddl_time TEXT,
  payload_json  TEXT,
  fetched_at    TEXT NOT NULL,
  PRIMARY KEY (db_key, owner, name, type)
) WITHOUT ROWID;
CREATE INDEX ix_meta_object_name ON meta_object(db_key, name);
CREATE INDEX ix_meta_object_type ON meta_object(db_key, owner, type);

INSERT INTO schema_version(version, applied_at, description)
VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'init: history, history_fts, meta_source, meta_object');

-- 이력 검색 예(3자 이상은 trigram, 그보다 짧으면 LIKE 로 대체):
--   SELECT h.* FROM history h JOIN history_fts f ON f.rowid = h.id
--    WHERE history_fts MATCH '"all_objects"' AND h.profile_id = :pid
--    ORDER BY h.executed_at DESC LIMIT 200;
-- 보존 정리 예:
--   DELETE FROM history WHERE executed_at < :cutoff;
--   DELETE FROM history WHERE id IN (SELECT id FROM history ORDER BY executed_at DESC LIMIT -1 OFFSET :maxEntries);
