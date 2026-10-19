-- 세그먼트 크기(테이블·인덱스·LOB). 바인드: owner, name. dba 권한이 있을 때만 쓴다.
-- id: meta.detail_size
-- min-version: 19
-- requires: dba_segments, dba_indexes, dba_lobs
-- license: none
SELECT s.segment_name, s.segment_type, SUM(s.bytes) AS bytes, SUM(s.extents) AS extents
  FROM dba_segments s
 WHERE s.owner = :owner
   AND (s.segment_name = :name
        OR s.segment_name IN (SELECT i.index_name FROM dba_indexes i WHERE i.table_owner = :owner AND i.table_name = :name)
        OR s.segment_name IN (SELECT l.segment_name FROM dba_lobs l WHERE l.owner = :owner AND l.table_name = :name)
        OR s.segment_name IN (SELECT l.index_name FROM dba_lobs l WHERE l.owner = :owner AND l.table_name = :name))
 GROUP BY s.segment_name, s.segment_type
 ORDER BY SUM(s.bytes) DESC, s.segment_name
