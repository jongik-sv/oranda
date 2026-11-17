-- 테이블의 LOB 열 저장 절(테이블 편집기 「저장」 페이지 LOB 표 18.2, T13c). 바인드: owner, name. LOB 열이 없으면 0행.
-- 열: column_name, securefile(YES/NO), tablespace_name, compression(NO·LOW·MEDIUM·HIGH, BASICFILE 은 NONE), deduplication(NO·LOB, BASICFILE 은 NONE), in_row(YES/NO).
-- 11g 에도 이 열이 모두 있다. 파티션 테이블의 tablespace_name 은 NULL 일 수 있다(파티션마다 따로).
-- id: meta.table_lobs
-- min-version: 11
-- requires: dba_lobs, all_lobs
-- license: none
SELECT l.column_name, l.securefile, l.tablespace_name, l.compression, l.deduplication, l.in_row
  FROM @PFX@_lobs l
 WHERE l.owner = :owner
   AND l.table_name = :name
 ORDER BY l.column_name
