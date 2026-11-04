-- Oranda 사용 설명서 샘플 DB (4/4): PL/SQL 패키지
-- 패키지 하나(sample_pkg)에 함수 하나와 프로시저 둘이 있다. PL/SQL 장의 컴파일, 실행, 디버그 예시에 쓴다.

CREATE OR REPLACE PACKAGE sample_pkg AS
  -- 고객 한 명의 취소되지 않은 주문 합계.
  FUNCTION customer_total (p_customer_id IN NUMBER) RETURN NUMBER;

  -- 부서 사원의 급여를 비율만큼 올리고 바뀐 사원 수를 돌려준다.
  PROCEDURE raise_salary (p_dept_id IN NUMBER, p_percent IN NUMBER, p_changed OUT NUMBER);

  -- 고객의 최근 주문을 커서로 돌려준다.
  PROCEDURE recent_orders (p_customer_id IN NUMBER, p_orders OUT SYS_REFCURSOR);
END sample_pkg;
/

CREATE OR REPLACE PACKAGE BODY sample_pkg AS
  FUNCTION customer_total (p_customer_id IN NUMBER) RETURN NUMBER IS
    v_total NUMBER;
  BEGIN
    SELECT NVL(SUM(total_amount), 0)
    INTO   v_total
    FROM   orders
    WHERE  customer_id = p_customer_id
    AND    status <> 'CANCELLED';
    RETURN v_total;
  END customer_total;

  PROCEDURE raise_salary (p_dept_id IN NUMBER, p_percent IN NUMBER, p_changed OUT NUMBER) IS
  BEGIN
    IF p_percent <= 0 OR p_percent > 20 THEN
      RAISE_APPLICATION_ERROR(-20001, '인상률은 0 초과 20 이하여야 한다.');
    END IF;
    UPDATE employees
    SET    salary = ROUND(salary * (1 + p_percent / 100), -3)
    WHERE  dept_id = p_dept_id;
    p_changed := SQL%ROWCOUNT;
    DBMS_OUTPUT.PUT_LINE('급여를 올린 사원 수: ' || p_changed);
  END raise_salary;

  PROCEDURE recent_orders (p_customer_id IN NUMBER, p_orders OUT SYS_REFCURSOR) IS
  BEGIN
    OPEN p_orders FOR
      SELECT order_id, order_date, status, total_amount
      FROM   orders
      WHERE  customer_id = p_customer_id
      ORDER  BY order_date DESC
      FETCH FIRST 10 ROWS ONLY;
  END recent_orders;
END sample_pkg;
/
