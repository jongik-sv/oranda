-- 복사(없으면 만들기): 원본 테이블에 걸린 트리거가 참조하는 시퀀스(ALL_DEPENDENCIES). 열 = REFERENCED_OWNER, REFERENCED_NAME. 바인드: OWN, TBL.
-- Oracle 판과 달라진 점: Tibero 의 *_dependencies 참조 대상 열은 parent_obj_owner / parent_obj_name / parent_obj_type 이다(REFERENCED_* 는 -8026, #166). 열 이름은 별칭으로 Oracle 과 맞춘다
-- id: copy.trigger_sequences
-- dbms: tibero
-- min-version: 6
-- requires: all_dependencies, all_triggers
-- license: none
SELECT DISTINCT d.PARENT_OBJ_OWNER AS REFERENCED_OWNER, d.PARENT_OBJ_NAME AS REFERENCED_NAME FROM ALL_DEPENDENCIES d JOIN ALL_TRIGGERS t ON t.OWNER = d.OWNER AND t.TRIGGER_NAME = d.NAME WHERE d.TYPE = 'TRIGGER' AND d.PARENT_OBJ_TYPE = 'SEQUENCE' AND t.TABLE_OWNER = :OWN AND t.TABLE_NAME = :TBL ORDER BY 1, 2
