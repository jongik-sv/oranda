-- Oranda 사용 설명서 샘플 DB (1/4): 표
-- 빈 계정(CONNECT, RESOURCE, CREATE VIEW 권한, 표 공간 할당량 약 100 MB 이상)에서 실행한다. 이 스크립트는 사용자 계정이나 비밀번호를 만들지 않는다.
-- 이름과 숫자는 모두 이 설명서를 위해 지어낸 값이다.

CREATE TABLE departments (
  dept_id    NUMBER(4)     CONSTRAINT departments_pk PRIMARY KEY,
  dept_name  VARCHAR2(40)  NOT NULL,
  location   VARCHAR2(40)
);

CREATE TABLE employees (
  emp_id      NUMBER(6)     CONSTRAINT employees_pk PRIMARY KEY,
  emp_name    VARCHAR2(40)  NOT NULL,
  dept_id     NUMBER(4)     CONSTRAINT employees_dept_fk REFERENCES departments (dept_id),
  manager_id  NUMBER(6)     CONSTRAINT employees_mgr_fk REFERENCES employees (emp_id),
  job_title   VARCHAR2(30),
  salary      NUMBER(9)     NOT NULL,
  hire_date   DATE          NOT NULL,
  email       VARCHAR2(60)
);

CREATE TABLE customers (
  customer_id    NUMBER(8)     GENERATED ALWAYS AS IDENTITY CONSTRAINT customers_pk PRIMARY KEY,
  customer_name  VARCHAR2(40)  NOT NULL,
  region         VARCHAR2(20)  NOT NULL,
  grade          VARCHAR2(10)  NOT NULL,
  joined_on      DATE          NOT NULL
);

CREATE TABLE products (
  product_id    NUMBER(6)     CONSTRAINT products_pk PRIMARY KEY,
  product_name  VARCHAR2(60)  NOT NULL,
  category      VARCHAR2(20)  NOT NULL,
  unit_price    NUMBER(9)     NOT NULL
);

CREATE TABLE orders (
  order_id      NUMBER(10)    GENERATED ALWAYS AS IDENTITY CONSTRAINT orders_pk PRIMARY KEY,
  customer_id   NUMBER(8)     NOT NULL CONSTRAINT orders_customer_fk REFERENCES customers (customer_id),
  emp_id        NUMBER(6)     CONSTRAINT orders_emp_fk REFERENCES employees (emp_id),
  order_date    DATE          NOT NULL,
  status        VARCHAR2(10)  NOT NULL,
  total_amount  NUMBER(12)    DEFAULT 0 NOT NULL
);

CREATE TABLE order_items (
  order_id    NUMBER(10)  NOT NULL CONSTRAINT order_items_order_fk REFERENCES orders (order_id),
  line_no     NUMBER(3)   NOT NULL,
  product_id  NUMBER(6)   NOT NULL CONSTRAINT order_items_product_fk REFERENCES products (product_id),
  quantity    NUMBER(5)   NOT NULL,
  unit_price  NUMBER(9)   NOT NULL,
  CONSTRAINT order_items_pk PRIMARY KEY (order_id, line_no)
);

COMMENT ON TABLE departments IS '부서';
COMMENT ON TABLE employees   IS '사원';
COMMENT ON TABLE customers   IS '고객';
COMMENT ON TABLE products    IS '상품';
COMMENT ON TABLE orders      IS '주문';
COMMENT ON TABLE order_items IS '주문 상세';
COMMENT ON COLUMN orders.status IS '상태: NEW, PAID, SHIPPED, CANCELLED';
