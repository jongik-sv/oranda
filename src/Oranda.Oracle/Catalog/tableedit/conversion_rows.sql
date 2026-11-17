-- 열 형식 변환 검사(설계 18.9) 2차: 1차가 실패했을 때 행마다 변환식 값을 새 형식 변수(@TYPE@)에 담아 실패한 행을 센다(Oracle 11g·12.2 미만, 사용자 변환식).
-- 바인드: query(앱이 만든 SELECT 한 문장, 끝에 ROWID = :rid), not_null(1 = NULL 값도 실패), max_ids(ROWID 를 모을 행 수) /
-- 출력 failed(실패 행 수), row_ids(쉼표로 이은 ROWID, 앞 max_ids 개·4000바이트 안). 읽기만 한다. 취소(ORA-01013)는 다시 던진다.
-- id: tableedit.conversion_rows
-- min-version: 11
-- license: none
DECLARE
  v @TYPE@;
  n PLS_INTEGER := 0;
  ids VARCHAR2(4000);
  q VARCHAR2(32767) := :query;
BEGIN
  FOR r IN (SELECT ROWID AS rid FROM @TABLE@) LOOP
    BEGIN
      EXECUTE IMMEDIATE q INTO v USING r.rid;
      IF :not_null = 1 AND v IS NULL THEN
        RAISE VALUE_ERROR;
      END IF;
    EXCEPTION
      WHEN OTHERS THEN
        IF SQLCODE = -1013 THEN
          RAISE;
        END IF;
        n := n + 1;
        IF n <= :max_ids AND NVL(LENGTHB(ids), 0) + 40 < 4000 THEN
          ids := ids || ROWIDTOCHAR(r.rid) || ',';
        END IF;
    END;
  END LOOP;
  :failed := n;
  :row_ids := RTRIM(ids, ',');
END;
