-- 복사 사전 점검: 초기화 파라미터 값(Tibero 방언은 undo_retention 만 묻는다). 열 = VALUE. 바인드: PNAME(파라미터 이름, 대소문자 무관).
-- Oracle 판과 달라진 점: v$parameter 가 없어(-8033) v$parameters(NAME, VALUE, DFLT_VALUE)를 쓰고, Tibero 의 파라미터 이름은 대문자라 양쪽을 UPPER 로 비교한다.
-- UNDO_RETENTION 이름과 값 형식은 IT 로 확인한다. 읽을 수 없거나 행이 없으면 호출한 쪽이 「알 수 없음」으로 본다.
-- id: copy.parameter_value
-- dbms: tibero
-- min-version: 6
-- requires: v$parameters
-- license: none
SELECT VALUE FROM V$PARAMETERS WHERE UPPER(NAME) = UPPER(:PNAME)
