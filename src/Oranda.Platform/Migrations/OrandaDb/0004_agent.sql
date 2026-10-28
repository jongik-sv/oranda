-- oranda.db schema v4 (ai-tuning, 연구서 §6·§7)
-- 1) history.source CHECK 에 'Agent' 추가 — SQLite 는 CHECK 를 바꿀 수 없어 표를 다시 만든다.
-- 2) agent_audit: AI 에이전트 도구 호출의 감사 기록(IAgentAuditStore, 비밀·행 값 없음).
-- 마이그레이션 실행기는 버전을 읽은 뒤 BEGIN IMMEDIATE 로 들어가며 그 사이 다른 연결이 먼저 적용했는지 다시 보지 않는다.
-- 그래서 이 스크립트는 이미 적용된 DB 에서 다시 실행돼도 안전하게(멱등하게) 쓴다.

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
                   ('Editor','Script','GridEdit','SchemaBrowser','Monitor','Extension','Agent')),
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

CREATE TABLE IF NOT EXISTS agent_audit (
  id           INTEGER PRIMARY KEY,
  occurred_at  TEXT    NOT NULL,
  profile_id   TEXT,
  profile_name TEXT,
  client       TEXT,
  run_id       TEXT,
  tool         TEXT    NOT NULL,
  arguments    TEXT,
  access_level TEXT    NOT NULL,
  verdict      TEXT    NOT NULL,
  reason       TEXT,
  result_chars INTEGER,
  elapsed_ms   INTEGER
);
CREATE INDEX IF NOT EXISTS ix_agent_audit_time    ON agent_audit(occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_agent_audit_profile ON agent_audit(profile_id, occurred_at DESC);
