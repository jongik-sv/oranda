-- 테이블의 LOB 열 저장 절(테이블 편집기 「저장」 페이지 LOB 표 18.2, T13c). 바인드: owner, name. LOB 열이 없으면 0행.
-- 열: column_name, tablespace_name, compression(NOCOMPRESS·LOW·MEDIUM·HIGH), deduplication(NO·LOB), in_row(YES/NO). Tibero *_lobs 에는 SECUREFILE 열이 없어(저장 방식 모름) 읽지 않는다.
-- 실측 tibero-table-edit-probe.md 13장.
-- id: meta.table_lobs
-- dbms: tibero
-- min-version: 6
-- requires: dba_lobs, all_lobs
-- license: none
SELECT l.column_name, l.tablespace_name, l.compression, l.deduplication, l.in_row
  FROM @PFX@_lobs l
 WHERE l.owner = :owner
   AND l.table_name = :name
 ORDER BY l.column_name
