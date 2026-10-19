using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Threading;

namespace EditorSpike.Ui;

/// <summary>입력 처리 비용 분해(원인 찾기용).</summary>
public static class Diag
{
    static double T(Action a) { var t = Stopwatch.GetTimestamp(); a(); return Stopwatch.GetElapsedTime(t).TotalMilliseconds; }

    public static void Run(int lines)
    {
        HeadlessHost.Start();
        var txt = PlSqlGen.Package(lines);
        if (Environment.GetEnvironmentVariable("ASCII") != null) txt = new string(txt.Select(c => c < 128 ? c : 'k').ToArray());
        var (w, ed) = HeadlessHost.CreateEditorWindow(txt);
        if (Environment.GetEnvironmentVariable("PRIO") != null)
        {
            var prios = new[] { DispatcherPriority.Send, DispatcherPriority.Render, DispatcherPriority.Loaded, DispatcherPriority.Input, DispatcherPriority.Default, DispatcherPriority.Background, DispatcherPriority.ContextIdle, DispatcherPriority.ApplicationIdle, DispatcherPriority.SystemIdle };
            for (int r = 0; r < 4; r++)
            {
                ed.Document.Insert(ed.CaretOffset, "z");
                var sb = new System.Text.StringBuilder($"layout={T(() => w.UpdateLayout()):0.00} ");
                foreach (var pr in prios) sb.Append($"{pr}={T(() => Dispatcher.UIThread.RunJobs(pr)):0.00} ");
                sb.Append($"frame={T(() => HeadlessHost.Frame(w)):0.00}");
                Console.WriteLine(sb);
            }
        }
        if (Environment.GetEnvironmentVariable("LAYOUT") != null)
            for (int r = 0; r < 3; r++) { ed.Document.Insert(ed.CaretOffset, "z"); Console.WriteLine($"UpdateLayout={T(() => w.UpdateLayout()):0.00} then jobs={T(() => Dispatcher.UIThread.RunJobs()):0.00}"); }
        var dl = ed.Document.GetLineByNumber(Math.Min(5000, ed.Document.LineCount / 2));
        ed.CaretOffset = dl.Offset + 4;
        ed.ScrollToLine(dl.LineNumber);
        HeadlessHost.Frame(w);
        foreach (var f in typeof(AvaloniaEdit.TextEditor).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            if (f.Name.Contains("Text")) Console.WriteLine("field " + f.Name + " : " + f.FieldType.Name);
        var ev = typeof(AvaloniaEdit.Document.TextDocument).GetField("Changed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(ed.Document) as Delegate;
        if (ev != null) foreach (var d in ev.GetInvocationList()) Console.WriteLine("Changed -> " + d.Method.DeclaringType + "." + d.Method.Name);
        var ev2 = typeof(AvaloniaEdit.Document.TextDocument).GetField("TextChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(ed.Document) as Delegate;
        if (ev2 != null) foreach (var d in ev2.GetInvocationList()) Console.WriteLine("TextChanged -> " + d.Method.DeclaringType + "." + d.Method.Name);
        if (Environment.GetEnvironmentVariable("NOSEARCH") != null) { ed.SearchPanel?.Uninstall(); Console.WriteLine("search panel uninstalled"); }
        ed.ShowLineNumbers = Environment.GetEnvironmentVariable("NOLN") == null;
        var bare = new AvaloniaEdit.Document.TextDocument(PlSqlGen.Package(lines));
        for (int r = 0; r < 3; r++) Console.WriteLine($"bare doc.Insert={T(() => bare.Insert(bare.TextLength / 2, "x")):0.000}");
        var sw = Stopwatch.StartNew(); long spin = 0; while (sw.ElapsedMilliseconds < 200) spin++;
        Console.WriteLine($"spin/200ms={spin}");
        for (int r = 0; r < 5; r++)
        {
            double insert = T(() => ed.Document.Insert(ed.CaretOffset, "x"));
            double jobs = T(() => Dispatcher.UIThread.RunJobs());
            double frame = T(() => HeadlessHost.Frame(w));
            double keyOnly = T(() => w.KeyTextInput("y"));
            double jobs2 = T(() => Dispatcher.UIThread.RunJobs());
            double frame2 = T(() => HeadlessHost.Frame(w));
            Console.WriteLine($"lines={ed.Document.LineCount} doc.Insert={insert:0.00} jobs={jobs:0.00} frame={frame:0.00} | KeyTextInput={keyOnly:0.00} jobs={jobs2:0.00} frame={frame2:0.00}");
        }
    }
}
