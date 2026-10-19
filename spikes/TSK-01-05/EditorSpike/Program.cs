using BenchmarkDotNet.Running;
using EditorSpike;
using EditorSpike.Ui;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var mode = args.Length > 0 ? args[0] : "help";
var rest = args.Skip(1).ToArray();
int Arg(int i, int d) => rest.Length > i && int.TryParse(rest[i], out var v) ? v : d;
try
{
    switch (mode)
    {
        case "compat": Compat.Run(); break;
        case "diag": Diag.Run(Arg(0, 10_000)); break;
        case "typing-window": WindowTyping.Run(samples: Arg(0, 300), warmup: Arg(1, 30)); break;
        case "selftest": SelfTest.Run(); break;
        case "typing": TypingLatency.Run(samples: Arg(0, 500), warmup: Arg(1, 50)); break;
        case "bench-lexer": BenchmarkRunner.Run<EditorSpike.Bench.LexerBenchmarks>(); break;
#if HAS_PLSQL_ANTLR
        case "bench-antlr": BenchmarkRunner.Run<EditorSpike.Bench.AntlrBenchmarks>(); break;
        case "antlr": EditorSpike.Antlr.AntlrProbe.Orchestrate(Arg(0, 5)); break;
        case "antlr-child": EditorSpike.Antlr.AntlrProbe.Child(rest[0]); break;
#endif
        default:
            Console.WriteLine("modes: compat | selftest | typing [samples warmup] | bench-lexer | bench-antlr | antlr [launches]");
            break;
    }
}
catch (Exception ex)
{
    Console.WriteLine("FAILED: " + ex);
    return 1;
}
return 0;

static class Compat
{
    public static void Run()
    {
        var ver = typeof(AvaloniaEdit.TextEditor).Assembly.GetName().Version;
        var av = typeof(Avalonia.Application).Assembly.GetName().Version;
        Console.WriteLine($"AvaloniaEdit {ver} / Avalonia {av}");
        HeadlessHost.Start();
        var text = PlSqlGen.Package();
        var (w, ed) = HeadlessHost.CreateEditorWindow(text);
        Console.WriteLine($"lines={ed.Document.LineCount} visualLines={ed.TextArea.TextView.VisualLines.Count}");
        ed.CaretOffset = ed.Document.GetLineByNumber(5000).Offset;
        Avalonia.Headless.HeadlessWindowExtensions.KeyTextInput(w, "x");
        HeadlessHost.Frame(w);
        Console.WriteLine($"after type: line5000 starts with '{ed.Document.GetText(ed.Document.GetLineByNumber(5000)).Substring(0, 5)}'");
        Console.WriteLine("COMPAT_OK");
    }
}
