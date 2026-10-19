#if HAS_PLSQL_ANTLR
using System.Diagnostics;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;

namespace EditorSpike.Antlr;

/// <summary>
/// e3-ANTLR: grammars-v4 PL/SQL 문법 첫 파싱(콜드)·워밍업 곡선. 콜드 값은 새 프로세스마다 1회만 의미가 있으므로
/// "antlr" 모드가 자기 자신을 자식 프로세스로 여러 번 띄워 모은다.
/// </summary>
public static class AntlrProbe
{
    public sealed class Errs : BaseErrorListener
    {
        public int Count;
        public override void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e) => Count++;
    }

    /// <summary>SLL 먼저, 실패하면 LL 로 다시(일반적인 2단계 전략). 돌려주는 값 = (ms, 오류 수, SLL 실패 여부)</summary>
    public static (double ms, int errors, bool fellBack, int tokens) Parse(string text)
    {
        long t0 = Stopwatch.GetTimestamp();
        var lexer = new PlSqlLexer(new AntlrInputStream(text));
        lexer.RemoveErrorListeners();
        var tokens = new CommonTokenStream(lexer);
        tokens.Fill();
        var parser = new PlSqlParser(tokens);
        parser.RemoveErrorListeners();
        parser.Interpreter.PredictionMode = PredictionMode.SLL;
        parser.ErrorHandler = new BailErrorStrategy();
        bool fell = false;
        int errs = 0;
        try { parser.sql_script(); }
        catch (Exception)
        {
            fell = true;
            tokens.Seek(0);
            parser.Reset();
            var el = new Errs();
            parser.AddErrorListener(el);
            parser.ErrorHandler = new DefaultErrorStrategy();
            parser.Interpreter.PredictionMode = PredictionMode.LL;
            parser.sql_script();
            errs = el.Count;
        }
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        return (ms, errs, fell, tokens.Size);
    }

    public static string OneProc(string package)
    {
        // 바디의 프로시저 하나(대략 50줄)를 독립 블록으로 잘라 낸다.
        int a = package.IndexOf("  PROCEDURE p_proc_0100(p_id IN NUMBER, p_name IN VARCHAR2 DEFAULT NULL, p_out OUT SYS_REFCURSOR) IS");
        int b = package.IndexOf("END p_proc_0100;", a) + "END p_proc_0100;".Length;
        return "CREATE OR REPLACE " + package[(a + 2)..b] + "\n/\n";
    }

    /// <summary>자식 프로세스 1회: 콜드 첫 파싱 + 이어지는 워밍업 곡선을 한 줄 JSON 비슷하게 출력.</summary>
    public static void Child(string scenario)
    {
        var pkg = PlSqlGen.Package();
        var proc = OneProc(pkg);
        var sb = new System.Text.StringBuilder();
        if (scenario == "proc")
        {
            // 작은 단위(프로시저 1개)로 처음부터: 콜드 1회 + 반복 9회
            for (int i = 0; i < 10; i++) { var r = Parse(proc); sb.Append($"{r.ms:0.0}{(r.fellBack ? "*" : "")} "); }
        }
        else
        {
            // 1만 줄 패키지 통째: 콜드 1회 + 반복 4회, 그 뒤 프로시저 1개 파싱 10회
            for (int i = 0; i < 5; i++) { var r = Parse(pkg); sb.Append($"{r.ms:0.0}{(r.fellBack ? "*" : "")}(e{r.errors},t{r.tokens}) "); }
            sb.Append("| proc: ");
            for (int i = 0; i < 10; i++) { var r = Parse(proc); sb.Append($"{r.ms:0.00} "); }
        }
        Console.WriteLine($"RESULT {scenario} {sb}");
        Console.WriteLine($"WS {Environment.WorkingSet / 1024 / 1024} MB");
    }

    public static void Orchestrate(int launches)
    {
        var exe = Environment.ProcessPath!;
        foreach (var sc in new[] { "proc", "package" })
        {
            Console.WriteLine($"# antlr cold {sc} x{launches}  (* = SLL 실패 후 LL 재파싱, e=LL 구문 오류 수, t=토큰 수, ms)");
            for (int l = 0; l < launches; l++)
            {
                var psi = new ProcessStartInfo(exe, $"antlr-child {sc}") { RedirectStandardOutput = true, UseShellExecute = false };
                long t0 = Stopwatch.GetTimestamp();
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                double wall = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("  " + line.TrimEnd());
                Console.WriteLine($"  process wall {wall:0} ms");
            }
        }
    }
}
#endif
