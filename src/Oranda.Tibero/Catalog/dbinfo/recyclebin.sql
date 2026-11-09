-- 현재 스키마의 휴지통(user_recyclebin).
-- Oracle 과 달라진 점: can_undrop·can_purge 열이 Tibero 뷰에 없고 이 서버는 USE_RECYCLEBIN=NO 라 값을 확인하지 못해 NULL(호출 쪽은 NULL 을 「아니오」로 읽는다).
-- droptime 은 Tibero 에서 문자열이며 행이 없어 형식을 확인하지 못했다(Oracle 은 yyyy-MM-dd:HH:mm:ss).
-- id: dbinfo.recyclebin
-- dbms: tibero
-- min-version: 6
-- requires: user_recyclebin
-- license: none
SELECT r.object_name, r.original_name, r.type, r.ts_name, r.droptime,
       CAST(NULL AS VARCHAR2(3)) AS can_undrop,
       CAST(NULL AS VARCHAR2(3)) AS can_purge,
       r.space
  FROM user_recyclebin r
 ORDER BY r.droptime DESC, r.object_name
