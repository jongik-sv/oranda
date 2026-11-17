-- 열 형식 변환 검사(설계 18.9) 1차: 모든 행의 변환식 값을 새 형식(@TYPE@) 모음에 담아 본다(2단계 UPDATE 와 같은 행·같은 변환, 500행씩).
-- 한 행이라도 실패하면 :ok = 0, :first_error = 오류(행 단위로 세는 것은 tableedit.conversion_rows). 읽기만 한다.
-- 바인드: query(앱이 만든 SELECT 한 문장 — 사용자 변환식은 이 안에만 들어가 PL/SQL 로 새지 않는다), not_null(1 = NULL 값도 실패) / 출력 ok, first_error.
-- 취소(ORA-01013)는 삼키지 않고 다시 던진다(시간 제한).
-- id: tableedit.conversion_scan
-- min-version: 11
-- license: none
DECLARE
  TYPE value_list IS TABLE OF @TYPE@;
  vs value_list;
  c SYS_REFCURSOR;
BEGIN
  :ok := 1;
  OPEN c FOR :query;
  LOOP
    FETCH c BULK COLLECT INTO vs LIMIT 500;
    IF :not_null = 1 THEN
      FOR i IN 1 .. vs.COUNT LOOP
        IF vs(i) IS NULL THEN
          RAISE VALUE_ERROR;
        END IF;
      END LOOP;
    END IF;
    EXIT WHEN vs.COUNT < 500;
  END LOOP;
  CLOSE c;
EXCEPTION
  WHEN OTHERS THEN
    IF SQLCODE = -1013 THEN
      RAISE;
    END IF;
    IF c%ISOPEN THEN
      CLOSE c;
    END IF;
    :ok := 0;
    :first_error := SUBSTR(SQLERRM, 1, 2000);
END;
