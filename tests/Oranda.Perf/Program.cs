using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

// 사용: Oranda.Perf --exe <게시본 실행파일> [--runs 5] [--out <디렉터리>] [--budget-ms 1000] [--enforce] [--timeout-s 30]
// App 계측 훅 규약: 환경 변수 ORANDA_PERF=1 이면 App 이 stdout 에 한 줄씩
//   ORANDA_PERF stage=<appbuilder|window-created|first-render> ms=<프로세스 시작 후 경과 ms>
// 를 stdout 또는 ORANDA_PERF_FILE(도구가 항상 지정)에 쓴다. 훅이 없으면 wall-clock(Process.Start→표식 없음)이라 first-render 는 null 로 기록된다.
var opt = Options.Parse(args);
if (opt.Exe is null || !File.Exists(opt.Exe))
{
    Console.Error.WriteLine("--exe <게시본 실행파일> 필요");
    return 2;
}

var runs = new List<RunResult>();
for (var i = 1; i <= opt.Runs; i++)
{
    var r = RunOnce(opt, i);
    runs.Add(r);
    Console.WriteLine($"run {i}: appbuilder={Fmt(r.AppBuilderMs)} window={Fmt(r.WindowCreatedMs)} firstRender={Fmt(r.FirstRenderMs)} privateMB={r.PrivateMemoryMb:F1} ok={r.Ok}");
}

var ok = runs.Where(r => r.FirstRenderMs is not null).Select(r => r.FirstRenderMs!.Value).OrderBy(x => x).ToList();
var summary = new Summary(
    DateTime.UtcNow, Environment.OSVersion.ToString(), RuntimeInformation(), opt.Runs,
    Median(runs.Select(r => r.AppBuilderMs)), Median(runs.Select(r => r.WindowCreatedMs)),
    ok.Count > 0 ? Median(ok.Select(x => (double?)x)) : null,
    ok.Count > 0 ? ok[^1] : null,
    Median(runs.Select(r => (double?)r.PrivateMemoryMb)), opt.BudgetMs, runs);

Directory.CreateDirectory(opt.Out);
File.WriteAllText(Path.Combine(opt.Out, "perf-startup.json"),
    JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(Path.Combine(opt.Out, "perf-startup.md"), ToMarkdown(summary));
Console.WriteLine($"median firstRender={Fmt(summary.MedianFirstRenderMs)} ms, private={Fmt(summary.MedianPrivateMemoryMb)} MB -> {opt.Out}");

var failed = runs.Any(r => !r.Ok);
var overBudget = summary.MedianFirstRenderMs is { } m && m > opt.BudgetMs;
if (failed) { Console.Error.WriteLine("일부 실행이 표식 없이 종료/시간 초과"); }
if (overBudget) { Console.Error.WriteLine($"예산 초과: {summary.MedianFirstRenderMs:F0} > {opt.BudgetMs} ms"); }
return opt.Enforce && (failed || overBudget) ? 1 : 0;

static RunResult RunOnce(Options opt, int index)
{
    var psi = new ProcessStartInfo(opt.Exe!)
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(opt.Exe!))!,
    };
    var markFile = Path.Combine(Path.GetTempPath(), $"oranda-perf-{Environment.ProcessId}-{index}.txt");
    File.Delete(markFile);
    psi.Environment["ORANDA_PERF"] = "1";
    psi.Environment["ORANDA_PERF_FILE"] = markFile;
    var marks = new Dictionary<string, double>();
    var firstRender = new ManualResetEventSlim();
    using var p = new Process { StartInfo = psi };
    void Parse(string? line)
    {
        if (line is null || !line.StartsWith("ORANDA_PERF ", StringComparison.Ordinal)) return;
        string? stage = null; double? ms = null;
        foreach (var kv in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var parts = kv.Split('=', 2);
            if (parts.Length != 2) continue;
            if (parts[0] == "stage") stage = parts[1];
            else if (parts[0] == "ms" && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) ms = v;
        }
        if (stage is null || ms is null) return;
        lock (marks) marks[stage] = ms.Value;
        if (stage == "first-render") firstRender.Set();
    }
    p.OutputDataReceived += (_, e) => Parse(e.Data);
    p.Start();
    p.BeginOutputReadLine();
    var deadline = DateTime.UtcNow.AddSeconds(opt.TimeoutS);
    while (!firstRender.IsSet && DateTime.UtcNow < deadline && !p.HasExited)
    {
        try { foreach (var l in File.ReadAllLines(markFile)) Parse(l); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Thread.Sleep(50);
    }
    try { foreach (var l in File.ReadAllLines(markFile)) Parse(l); } catch (IOException) { }
    var got = firstRender.IsSet;
    double mem = 0;
    if (got)
    {
        Thread.Sleep(2000); // 첫 렌더 직후 안정화
        try { p.Refresh(); mem = p.PrivateMemorySize64 / 1024.0 / 1024.0; } catch { }
    }
    try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch { }
    lock (marks)
    {
        double? Get(string k) => marks.TryGetValue(k, out var v) ? v : null;
        return new RunResult(index, Get("appbuilder"), Get("window-created"), Get("first-render"), mem, got);
    }
}

static double? Median(IEnumerable<double?> xs)
{
    var l = xs.Where(x => x is not null).Select(x => x!.Value).OrderBy(x => x).ToList();
    if (l.Count == 0) return null;
    return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
}
static string Fmt(double? v) => v is null ? "n/a" : v.Value.ToString("F0", CultureInfo.InvariantCulture);
static string RuntimeInformation() => System.Runtime.InteropServices.RuntimeInformation.OSDescription + " / " + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;

static string ToMarkdown(Summary s)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Oranda 시작 측정");
    sb.AppendLine();
    sb.AppendLine(CultureInfo.InvariantCulture, $"- 시각(UTC): {s.TimestampUtc:O}");
    sb.AppendLine(CultureInfo.InvariantCulture, $"- 환경: {s.Environment}");
    sb.AppendLine(CultureInfo.InvariantCulture, $"- 실행 횟수: {s.Runs}, 예산(첫 렌더 중앙값): {s.BudgetMs} ms");
    sb.AppendLine();
    sb.AppendLine("| 지표 | 값 |");
    sb.AppendLine("|---|---|");
    sb.AppendLine(CultureInfo.InvariantCulture, $"| AppBuilder 설정 완료(중앙값 ms) | {Fmt(s.MedianAppBuilderMs)} |");
    sb.AppendLine(CultureInfo.InvariantCulture, $"| 창 생성(중앙값 ms) | {Fmt(s.MedianWindowCreatedMs)} |");
    sb.AppendLine(CultureInfo.InvariantCulture, $"| 첫 렌더(중앙값 ms) | {Fmt(s.MedianFirstRenderMs)} |");
    sb.AppendLine(CultureInfo.InvariantCulture, $"| 첫 렌더(최대 ms) | {Fmt(s.MaxFirstRenderMs)} |");
    sb.AppendLine(CultureInfo.InvariantCulture, $"| 전용 메모리(중앙값 MB) | {Fmt(s.MedianPrivateMemoryMb)} |");
    sb.AppendLine();
    sb.AppendLine("| # | AppBuilder | 창 생성 | 첫 렌더 | 전용 MB | 성공 |");
    sb.AppendLine("|---|---|---|---|---|---|");
    foreach (var r in s.Details)
        sb.AppendLine(CultureInfo.InvariantCulture, $"| {r.Index} | {Fmt(r.AppBuilderMs)} | {Fmt(r.WindowCreatedMs)} | {Fmt(r.FirstRenderMs)} | {r.PrivateMemoryMb:F1} | {r.Ok} |");
    return sb.ToString();
}

sealed record RunResult(int Index, double? AppBuilderMs, double? WindowCreatedMs, double? FirstRenderMs, double PrivateMemoryMb, bool Ok);
sealed record Summary(DateTime TimestampUtc, string Environment, string Runtime, int Runs,
    double? MedianAppBuilderMs, double? MedianWindowCreatedMs, double? MedianFirstRenderMs, double? MaxFirstRenderMs,
    double? MedianPrivateMemoryMb, double BudgetMs, List<RunResult> Details);

sealed record Options(string? Exe, int Runs, string Out, double BudgetMs, bool Enforce, int TimeoutS)
{
    public static Options Parse(string[] a)
    {
        string? exe = null, outDir = "perf-results"; int runs = 5, timeout = 30; double budget = 1000; bool enforce = false;
        for (var i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--exe": exe = a[++i]; break;
                case "--runs": runs = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--out": outDir = a[++i]; break;
                case "--budget-ms": budget = double.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--timeout-s": timeout = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--enforce": enforce = true; break;
            }
        }
        return new Options(exe, runs, outDir, budget, enforce, timeout);
    }
}
