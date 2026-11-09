-- 시퀀스 DDL 의 속성. 바인드: owner, name. 숫자는 TO_CHAR 로 낸다(드라이버는 NUMBER 를 decimal 로 읽어 28자리를 넘으면 넘침 오류가 난다, tibero-driver-values.md).
-- 실측표(docs/design/tibero-catalog-map.md)에 *_SEQUENCES 의 열 목록이 없다. Oracle 과 같은 SEQUENCE_OWNER·SEQUENCE_NAME·MIN_VALUE·MAX_VALUE·INCREMENT_BY·
-- CYCLE_FLAG·ORDER_FLAG·CACHE_SIZE·LAST_NUMBER 로 보고 쓰며, 쓰기 IT(TiberoDdlObjectsIt)와 읽기 IT 의 파싱 확인이 이 열들을 드러낸다.
-- id: meta.ddl_sequence
-- dbms: tibero
-- min-version: 6
-- requires: dba_sequences, all_sequences
-- license: none
SELECT TO_CHAR(s.min_value) AS min_value, TO_CHAR(s.max_value) AS max_value, TO_CHAR(s.increment_by) AS increment_by,
       s.cycle_flag, s.order_flag, TO_CHAR(s.cache_size) AS cache_size, TO_CHAR(s.last_number) AS last_number
  FROM @PFX@_sequences s
 WHERE s.sequence_owner = :owner
   AND s.sequence_name = :name
