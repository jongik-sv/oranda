using System.Text;

namespace EditorSpike;

/// <summary>결정적 합성 PL/SQL 패키지(스펙+바디). 대략 targetLines 줄.</summary>
public static class PlSqlGen
{
    public static string Package(int targetLines = 10_000, string name = "PKG_ORANDA_SPIKE")
    {
        var sb = new StringBuilder();
        int lines = 0;
        void L(string s) { sb.Append(s).Append('\n'); lines++; }

        int procs = 0;
        var body = new StringBuilder();
        // 프로시저 하나 ≈ 50줄
        var spec = new List<string>();
        while (lines < targetLines - 10)
        {
            int i = procs++;
            spec.Add($"  PROCEDURE p_proc_{i:D4}(p_id IN NUMBER, p_name IN VARCHAR2 DEFAULT NULL, p_out OUT SYS_REFCURSOR);");
            lines++; // spec 줄
            lines += EmitProc(body, i);
        }
        lines = 0;
        L($"CREATE OR REPLACE PACKAGE {name} AS");
        L("  /* 합성 패키지: TSK-01-05 성능 시험용 */");
        L("  gc_version CONSTANT VARCHAR2(10) := '1.0.0';");
        foreach (var s in spec) L(s);
        L($"END {name};");
        L("/");
        L($"CREATE OR REPLACE PACKAGE BODY {name} AS");
        L("  -- 바디 시작");
        L("  g_counter PLS_INTEGER := 0;");
        sb.Append(body);
        L($"END {name};");
        L("/");
        return sb.ToString();
    }

    static int EmitProc(StringBuilder b, int i)
    {
        int n = 0;
        void L(string s) { b.Append(s).Append('\n'); n++; }
        L($"  /*");
        L($"   * p_proc_{i:D4}: 주문 {i} 처리. 'quote' 와 -- 가 섞인 주석");
        L($"   */");
        L($"  PROCEDURE p_proc_{i:D4}(p_id IN NUMBER, p_name IN VARCHAR2 DEFAULT NULL, p_out OUT SYS_REFCURSOR) IS");
        L($"    v_cnt     NUMBER := 0;");
        L($"    v_total   NUMBER(18,2) := {i}.{i % 100:D2};");
        L($"    v_msg     VARCHAR2(4000) := 'proc {i} it''s ok';");
        L($"    v_q       VARCHAR2(200) := q'[text with 'quotes' {i}]';");
        L($"    CURSOR c_orders IS");
        L($"      SELECT o.order_id, o.customer_id, o.amount, c.cust_name");
        L($"        FROM orders o");
        L($"        JOIN customers c ON c.customer_id = o.customer_id");
        L($"       WHERE o.status = 'OPEN'");
        L($"         AND o.amount > {i * 10}");
        L($"       ORDER BY o.order_id;");
        L($"  BEGIN");
        L($"    g_counter := g_counter + 1; -- 호출 수");
        L($"    FOR r IN c_orders LOOP");
        L($"      v_cnt := v_cnt + 1;");
        L($"      v_total := v_total + NVL(r.amount, 0) * 1.1e0;");
        L($"      IF r.amount >= 1000 THEN");
        L($"        UPDATE orders SET status = 'BIG', updated_at = SYSDATE");
        L($"         WHERE order_id = r.order_id;");
        L($"      ELSIF r.amount BETWEEN 100 AND 999 THEN");
        L($"        INSERT INTO order_log (order_id, msg, created_at)");
        L($"        VALUES (r.order_id, 'mid ' || TO_CHAR(r.amount, 'FM999G990D00'), SYSTIMESTAMP);");
        L($"      ELSE");
        L($"        NULL;");
        L($"      END IF;");
        L($"    END LOOP;");
        L($"    OPEN p_out FOR");
        L($"      SELECT /*+ INDEX(t idx_t_{i}) */ t.id, t.name, :b_{i} AS bind_val");
        L($"        FROM tab_{i % 50} t");
        L($"       WHERE t.id = p_id");
        L($"         AND (p_name IS NULL OR t.name LIKE p_name || '%');");
        L($"    MERGE INTO summary s");
        L($"    USING (SELECT p_id AS id, v_total AS total FROM dual) x");
        L($"       ON (s.id = x.id)");
        L($"     WHEN MATCHED THEN UPDATE SET s.total = x.total");
        L($"     WHEN NOT MATCHED THEN INSERT (id, total) VALUES (x.id, x.total);");
        L($"    DBMS_OUTPUT.PUT_LINE('done ' || v_cnt || ' ' || v_msg);");
        L($"  EXCEPTION");
        L($"    WHEN NO_DATA_FOUND THEN");
        L($"      v_cnt := -1;");
        L($"    WHEN OTHERS THEN");
        L($"      RAISE_APPLICATION_ERROR(-20001, 'p_proc_{i:D4} failed: ' || SQLERRM);");
        L($"  END p_proc_{i:D4};");
        L($"");
        return n;
    }
}
