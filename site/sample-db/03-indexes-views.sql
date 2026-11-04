-- Oranda 사용 설명서 샘플 DB (3/4): 인덱스, 뷰, 통계
-- 주문 상태(status)에는 일부러 인덱스를 만들지 않는다. 실행 계획 장에서 전체 표 읽기와 인덱스 읽기를 비교하기 위해서다.

CREATE INDEX orders_customer_ix ON orders (customer_id);
CREATE INDEX orders_date_ix     ON orders (order_date);
CREATE INDEX order_items_product_ix ON order_items (product_id);
CREATE INDEX employees_dept_ix  ON employees (dept_id);

CREATE OR REPLACE VIEW v_order_summary AS
SELECT o.order_id,
       o.order_date,
       o.status,
       o.total_amount,
       c.customer_name,
       c.region,
       e.emp_name AS sales_rep
FROM   orders o
JOIN   customers c ON c.customer_id = o.customer_id
LEFT JOIN employees e ON e.emp_id = o.emp_id;

CREATE OR REPLACE VIEW v_dept_salary AS
SELECT d.dept_id,
       d.dept_name,
       COUNT(e.emp_id)  AS emp_count,
       ROUND(AVG(e.salary)) AS avg_salary,
       MAX(e.salary)    AS max_salary
FROM   departments d
LEFT JOIN employees e ON e.dept_id = d.dept_id
GROUP BY d.dept_id, d.dept_name;

BEGIN
  DBMS_STATS.GATHER_SCHEMA_STATS(ownname => USER, cascade => TRUE);
END;
/
