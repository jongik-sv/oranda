-- 세그먼트 크기(자기 스키마 객체). 바인드: owner, name. dba 권한이 없고 owner 가 접속 사용자일 때 쓴다.
-- id: meta.detail_size_user
-- min-version: 19
-- requires: user_segments, user_indexes, user_lobs
-- license: none
SELECT s.segment_name, s.segment_type, SUM(s.bytes) AS bytes, SUM(s.extents) AS extents
  FROM user_segments s
 WHERE :owner = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')
   AND (s.segment_name = :name
        OR s.segment_name IN (SELECT i.index_name FROM user_indexes i WHERE i.table_name = :name)
        OR s.segment_name IN (SELECT l.segment_name FROM user_lobs l WHERE l.table_name = :name)
        OR s.segment_name IN (SELECT l.index_name FROM user_lobs l WHERE l.table_name = :name))
 GROUP BY s.segment_name, s.segment_type
 ORDER BY SUM(s.bytes) DESC, s.segment_name
