-- oranda.db schema v9 (스키마 브라우저 세부 종류·상태 표식)
-- meta_object 한 줄의 표식(MetaObject.SubKind·StateMark)을 따로 둔다: sub_kind(세부 종류 낱말, 예 'GTT_SESSION PART:RANGE'), state_mark(상태, 예 'DISABLED').
-- 목록 질의(meta.objects)·목록 갱신(meta_object UPSERT)은 이 표를 건드리지 않고, 목록을 받은 뒤 보조 질의(meta.marks_*)가 소유자마다 통째로 바꾼다.
-- 따로 두는 까닭: 목록 갱신이 표식을 지우지 않고, 자동 완성 등 meta_object 읽기는 그대로다. 예전 캐시는 행이 없어 표식 없이 보이다가 다음 표식 읽기 때 채워진다.
-- 없앨 때는 접속 단위(meta_source)를 따라 지운다(ON DELETE CASCADE). 객체 무효화는 저장소가 같이 지운다.

CREATE TABLE IF NOT EXISTS meta_mark (
  db_key     TEXT NOT NULL REFERENCES meta_source(db_key) ON DELETE CASCADE,
  owner      TEXT NOT NULL,
  name       TEXT NOT NULL,
  type       TEXT NOT NULL,
  sub_kind   TEXT,
  state_mark TEXT,
  PRIMARY KEY (db_key, owner, name, type)
) WITHOUT ROWID;
