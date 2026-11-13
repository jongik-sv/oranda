-- oranda.db schema v5 (RB-21, 설계 docs/superpowers/specs/2026-11-13-rollback-point-design.md 12.1, data-model 3.2.2)
-- 되돌리기 지점: 커밋 묶음(rollback_group)과 묶음 안 문장(rollback_statement)의 메타데이터만 둔다.
-- 행 값·문장 글·바인드는 {data}/rollback/{yyyy-MM}/{id:D8}.orb 암호화 파일에만 있다(RD-10). 요약·이유는 가린 글.

CREATE TABLE IF NOT EXISTS rollback_group (
  id                 INTEGER PRIMARY KEY,
  started_at         TEXT NOT NULL,
  ended_at           TEXT,
  profile_id         TEXT NOT NULL,
  profile_name       TEXT NOT NULL,
  db_key             TEXT NOT NULL,
  server_name        TEXT,
  environment        TEXT NOT NULL CHECK (environment IN ('Development','Test','Production')),
  dbms               TEXT NOT NULL,
  point_name         TEXT,
  base_point         INTEGER,
  commit_point       INTEGER,
  transaction_id     TEXT,
  end_kind           TEXT,
  end_source         TEXT,
  state              TEXT NOT NULL,
  reason             TEXT,
  statement_count    INTEGER NOT NULL,
  step_count         INTEGER NOT NULL,
  rows_total         INTEGER NOT NULL,
  tables             TEXT NOT NULL,
  origin             TEXT NOT NULL DEFAULT 'Execution',
  origin_group_id    INTEGER,
  script_file        TEXT,
  expires_at         TEXT
);
CREATE TABLE IF NOT EXISTS rollback_statement (
  group_id           INTEGER NOT NULL REFERENCES rollback_group(id) ON DELETE CASCADE,
  seq                INTEGER NOT NULL,
  executed_at        TEXT NOT NULL,
  source             TEXT NOT NULL,
  verb               TEXT NOT NULL,
  owner              TEXT,
  name               TEXT,
  summary            TEXT,
  rows_affected      INTEGER,
  locked_rows        INTEGER,
  before_point       INTEGER,
  state              TEXT NOT NULL,
  reason             TEXT,
  step_count         INTEGER NOT NULL,
  invalidated        INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (group_id, seq)
);
CREATE INDEX IF NOT EXISTS ix_rollback_group_time  ON rollback_group(started_at DESC);
CREATE INDEX IF NOT EXISTS ix_rollback_group_db    ON rollback_group(db_key, started_at DESC);
CREATE INDEX IF NOT EXISTS ix_rollback_stmt_object ON rollback_statement(owner, name);
