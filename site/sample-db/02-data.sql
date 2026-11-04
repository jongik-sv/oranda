-- Oranda 사용 설명서 샘플 DB (2/4): 데이터
-- 난수 시드를 고정해서 누가 실행해도 같은 데이터가 만들어진다. 행 수: 부서 8, 사원 120, 고객 2000, 상품 80, 주문 20000, 주문 상세 약 6만.

INSERT INTO departments (dept_id, dept_name, location) VALUES (10, '경영지원', '서울');
INSERT INTO departments (dept_id, dept_name, location) VALUES (20, '영업', '서울');
INSERT INTO departments (dept_id, dept_name, location) VALUES (30, '마케팅', '서울');
INSERT INTO departments (dept_id, dept_name, location) VALUES (40, '개발', '대전');
INSERT INTO departments (dept_id, dept_name, location) VALUES (50, '품질', '대전');
INSERT INTO departments (dept_id, dept_name, location) VALUES (60, '물류', '부산');
INSERT INTO departments (dept_id, dept_name, location) VALUES (70, '고객지원', '부산');
INSERT INTO departments (dept_id, dept_name, location) VALUES (80, '재무', '서울');

DECLARE
  TYPE t_names IS VARRAY(10) OF VARCHAR2(10);
  v_family t_names := t_names('김', '이', '박', '최', '정', '강', '조', '윤', '장', '임');
  v_given  t_names := t_names('민준', '서연', '도윤', '지우', '하준', '수아', '예준', '지민', '주원', '서윤');
  v_jobs   t_names := t_names('사원', '대리', '과장', '차장', '부장');
  v_dept   NUMBER;
BEGIN
  DBMS_RANDOM.SEED(42);
  FOR i IN 1 .. 120 LOOP
    v_dept := 10 * (1 + MOD(i, 8));
    INSERT INTO employees (emp_id, emp_name, dept_id, manager_id, job_title, salary, hire_date, email)
    VALUES (
      1000 + i,
      v_family(1 + MOD(i, 10)) || v_given(1 + MOD(TRUNC(i / 10) + i, 10)),
      v_dept,
      CASE WHEN i <= 8 THEN NULL ELSE 1000 + 1 + MOD(i, 8) END,
      CASE WHEN i <= 8 THEN '부장' ELSE v_jobs(1 + MOD(i, 4)) END,
      3000000 + 100000 * TRUNC(DBMS_RANDOM.VALUE(0, 40)),
      DATE '2012-03-01' + TRUNC(DBMS_RANDOM.VALUE(0, 4000)),
      'emp' || (1000 + i) || '@example.com'
    );
  END LOOP;
END;
/

DECLARE
  TYPE t_names IS VARRAY(10) OF VARCHAR2(20);
  v_family t_names := t_names('김', '이', '박', '최', '정', '강', '조', '윤', '장', '임');
  v_given  t_names := t_names('민준', '서연', '도윤', '지우', '하준', '수아', '예준', '지민', '주원', '서윤');
  v_region t_names := t_names('서울', '경기', '부산', '대구', '광주', '대전', '인천', '강원', '충북', '제주');
  v_grade  t_names := t_names('BRONZE', 'SILVER', 'GOLD', 'VIP');
BEGIN
  DBMS_RANDOM.SEED(43);
  FOR i IN 1 .. 2000 LOOP
    INSERT INTO customers (customer_name, region, grade, joined_on)
    VALUES (
      v_family(1 + MOD(i, 10)) || v_given(1 + MOD(TRUNC(i / 10), 10)) || LPAD(TRUNC(i / 100), 2, '0'),
      v_region(1 + MOD(TRUNC(DBMS_RANDOM.VALUE(0, 1000)), 10)),
      v_grade(1 + LEAST(3, TRUNC(DBMS_RANDOM.VALUE(0, 1) * DBMS_RANDOM.VALUE(0, 1) * 4))),
      DATE '2018-01-01' + TRUNC(DBMS_RANDOM.VALUE(0, 2500))
    );
  END LOOP;
END;
/

DECLARE
  TYPE t_names IS VARRAY(8) OF VARCHAR2(20);
  v_cat  t_names := t_names('문구', '주방', '가전', '의류', '식품', '도서', '운동', '완구');
  v_item t_names := t_names('기본형', '프리미엄', '대용량', '휴대용', '한정판', '세트', '친환경', '소형');
BEGIN
  DBMS_RANDOM.SEED(44);
  FOR i IN 1 .. 80 LOOP
    INSERT INTO products (product_id, product_name, category, unit_price)
    VALUES (
      i,
      v_cat(1 + MOD(i, 8)) || ' ' || v_item(1 + MOD(TRUNC(i / 8), 8)) || ' ' || i,
      v_cat(1 + MOD(i, 8)),
      1000 * (1 + TRUNC(DBMS_RANDOM.VALUE(0, 120)))
    );
  END LOOP;
END;
/

DECLARE
  TYPE t_names IS VARRAY(4) OF VARCHAR2(10);
  v_status t_names := t_names('PAID', 'SHIPPED', 'NEW', 'CANCELLED');
  v_pick   NUMBER;
BEGIN
  DBMS_RANDOM.SEED(45);
  FOR i IN 1 .. 20000 LOOP
    v_pick := DBMS_RANDOM.VALUE(0, 100);
    INSERT INTO orders (customer_id, emp_id, order_date, status)
    VALUES (
      1 + TRUNC(DBMS_RANDOM.VALUE(0, 2000)),
      1000 + 1 + TRUNC(DBMS_RANDOM.VALUE(0, 120)),
      DATE '2024-01-01' + TRUNC(DBMS_RANDOM.VALUE(0, 670)),
      CASE WHEN v_pick < 55 THEN v_status(1) WHEN v_pick < 85 THEN v_status(2) WHEN v_pick < 95 THEN v_status(3) ELSE v_status(4) END
    );
  END LOOP;
END;
/

-- 주문마다 1~5줄. 한 주문 안에서 줄마다 다른 상품이 되도록 상품 번호를 주문번호와 줄 번호로 정한다.
INSERT INTO order_items (order_id, line_no, product_id, quantity, unit_price)
SELECT o.order_id, l.line_no, p.product_id, 1 + MOD(o.order_id + l.line_no, 5), p.unit_price
FROM   orders o
JOIN   (SELECT LEVEL AS line_no FROM dual CONNECT BY LEVEL <= 5) l
       ON l.line_no <= 1 + MOD(o.order_id, 5)
JOIN   products p
       ON p.product_id = 1 + MOD(o.order_id * 7 + l.line_no * 13, 80);

UPDATE orders o
SET    total_amount = (SELECT NVL(SUM(i.quantity * i.unit_price), 0) FROM order_items i WHERE i.order_id = o.order_id);

COMMIT;
