using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Oranda.Perf.Common;

// 사용: Oranda.Perf --exe <게시본 실행파일> [--runs 5] [--discard 0] [--out <디렉터리>] [--budget-ms 1000] [--enforce] [--timeout-s 30]
//                  [--cold-copy] [--data-dir <폴더>] [--idle-s 2]
// 시작 표식 규약은 StartupRunner 주석 참고(App 의 PerfMarks).
// 기본: 실행마다 빈 임시 데이터 폴더(ORANDA_DATA_DIR). --data-dir 이면 같은 폴더를 계속 쓴다(레이아웃 복원이 있는 「두 번째 실행」).
// --cold-copy: 실행마다 게시 폴더를 새 임시 폴더에 복사해 띄운다(「최초 실행」 근사. macOS 는 대신 실행 직전 sudo purge).
// --idle-s: 입력 가능 뒤 메모리를 읽기까지 기다리는 시간(유휴 메모리는 10 이상으로).
StartupOptions opt;
try
{
    opt = StartupOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

if (opt.Exe is null || !File.Exists(opt.Exe))
{
    Console.Error.WriteLine("--exe <게시본 실행파일> 필요");
    return 2;
}

var all = new List<StartupRun>();
for (var i = 1; i <= opt.Discard + opt.Runs; i++)
{
    var r = StartupRunner.RunOnce(opt, i);
    all.Add(r);
    var tag = i <= opt.Discard ? " (버림)" : string.Empty;
    Console.WriteLine($"run {i}{tag}: appbuilder={Fmt(r.AppBuilderMs)} window={Fmt(r.WindowCreatedMs)} firstRender={Fmt(r.FirstRenderMs)} inputReady={Fmt(r.InputReadyMs)} privateMB={Fmt(r.PrivateMb)} commitMB={Fmt(r.CommitMb)} ok={r.Ok}");
}

var runs = all.Skip(opt.Discard).ToList();
var report = new Report("Oranda 시작·유휴 메모리 측정", $"{RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture} / {Environment.ProcessorCount} 논리 코어 / 실행 {opt.Runs}회(앞 {opt.Discard}회 버림), cold-copy={opt.ColdCopy}, 데이터 폴더={(opt.DataDir is null ? "실행마다 빈 폴더" : "고정")}");
report.Rows.Add(RowOf("앱 시작 → 입력 가능", "ms", 1500, runs.Select(r => r.InputReadyMs), "median", "목표 1.5 s(두 번째 실행 이후). 중앙값 기준"));
report.Rows.Add(RowOf("첫 렌더", "ms", null, runs.Select(r => r.FirstRenderMs), "median", "참고"));
report.Rows.Add(RowOf("창 생성", "ms", null, runs.Select(r => r.WindowCreatedMs), "median", "참고"));
report.Rows.Add(RowOf("AppBuilder 설정 완료", "ms", null, runs.Select(r => r.AppBuilderMs), "median", "참고(런타임 시작 + 어셈블리 로드)"));
report.Rows.Add(RowOf($"전용 메모리(입력 가능 뒤 {opt.IdleSeconds}s)", "MB", 150, runs.Select(r => r.PrivateMb), "median", "Windows 전용 작업 집합, macOS phys_footprint. 접속·탭 없음"));
report.Rows.Add(RowOf("커밋 메모리(Windows 전용 바이트)", "MB", null, runs.Select(r => r.CommitMb), "median", "참고"));
report.WriteTo(opt.Out, "perf-startup");
File.WriteAllText(Path.Combine(opt.Out, "perf-startup-runs.json"), JsonSerializer.Serialize(runs, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(report.ToMarkdown());

var failed = runs.Any(r => !r.Ok);
var firstRenders = runs.Where(r => r.FirstRenderMs is not null).Select(r => r.FirstRenderMs!.Value).ToList();
var overBudget = firstRenders.Count > 0 && Stats.Median(firstRenders) > opt.BudgetMs;
if (failed)
{
    Console.Error.WriteLine("일부 실행이 표식 없이 종료/시간 초과");
}

if (overBudget)
{
    Console.Error.WriteLine($"예산 초과: 첫 렌더 중앙값 {Stats.Median(firstRenders):F0} > {opt.BudgetMs} ms");
}

return opt.Enforce && (failed || overBudget) ? 1 : 0;

static string Fmt(double? v) => v is null ? "n/a" : v.Value.ToString("F0", CultureInfo.InvariantCulture);

static ReportRow RowOf(string item, string unit, double? limit, IEnumerable<double?> values, string judged, string note)
{
    var list = values.Where(v => v is not null).Select(v => v!.Value).ToList();
    return ReportRow.Create(item, unit, limit, list, judged, note);
}
