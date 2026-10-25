using System.Diagnostics;
using System.Globalization;

namespace Oranda.Perf.Common;

/// <summary>시작 측정 옵션. <c>Oranda.Perf</c> 의 명령줄을 읽는다.</summary>
/// <param name="Exe">게시본 실행 파일.</param>
/// <param name="Runs">측정 횟수(버리는 횟수 제외).</param>
/// <param name="Out">결과 폴더.</param>
/// <param name="BudgetMs">첫 렌더 중앙값 예산(ms). <c>--enforce</c> 일 때만 실패로 친다.</param>
/// <param name="Enforce">예산 초과·실패 실행이 있으면 종료 코드 1.</param>
/// <param name="TimeoutS">한 번 실행의 제한 시간(초).</param>
/// <param name="ColdCopy">매 실행 전에 게시 폴더를 새 임시 폴더에 복사해 거기서 띄운다(새 파일 = 백신 검사·디스크 캐시 없음에 가깝다).</param>
/// <param name="DataDir">고정 데이터 폴더. null 이면 실행마다 빈 임시 폴더.</param>
/// <param name="IdleSeconds">입력 가능 뒤 메모리를 읽기까지 기다리는 시간(초).</param>
/// <param name="Discard">앞에서 버리는 실행 횟수(예열·첫 실행). 결과 요약에 넣지 않는다.</param>
/// <param name="InputGraceSeconds">first-render 표식 뒤 input-ready 표식을 더 기다리는 시간(초). 느린 PC·부하 중에는 늘린다. 표식이 없는 옛 앱은 이만큼 기다린 뒤 끝낸다.</param>
/// <param name="Features">기능 구성(<see cref="FeaturePreset"/> 글). 있으면 실행마다 데이터 폴더 settings.json 에 features 를 쓰고 띄운다(TSK-07-07).</param>
public sealed record StartupOptions(
    string? Exe, int Runs, string Out, double BudgetMs, bool Enforce, int TimeoutS, bool ColdCopy, string? DataDir, int IdleSeconds, int Discard,
    string? Features = null, int InputGraceSeconds = 1)
{
    public static StartupOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? exe = null, dataDir = null, features = null;
        var outDir = "perf-results";
        int runs = 5, timeout = 30, idle = 2, discard = 0, grace = 1;
        double budget = 1000;
        bool enforce = false, cold = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{args[i]} 뒤에 값이 필요하다.");
                }

                return args[++i];
            }

            int NextInt(int min)
            {
                var name = args[i];
                var text = Next();
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min)
                {
                    throw new ArgumentException($"{name} 값은 {min} 이상의 정수여야 한다: {text}");
                }

                return v;
            }

            switch (args[i])
            {
                case "--exe": exe = Next(); break;
                case "--runs": runs = NextInt(1); break;
                case "--out": outDir = Next(); break;
                case "--budget-ms":
                    var b = Next();
                    if (!double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out budget) || budget <= 0)
                    {
                        throw new ArgumentException($"--budget-ms 값은 0 보다 큰 수여야 한다: {b}");
                    }

                    break;
                case "--timeout-s": timeout = NextInt(1); break;
                case "--idle-s": idle = NextInt(0); break;
                case "--discard": discard = NextInt(0); break;
                case "--input-grace-s": grace = NextInt(0); break;
                case "--data-dir": dataDir = Next(); break;
                case "--features":
                    features = Next();
                    _ = FeaturePreset.Parse(features); // 잘못된 값은 여기서 바로 알린다
                    break;
                case "--cold-copy": cold = true; break;
                case "--enforce": enforce = true; break;
                default: throw new ArgumentException($"모르는 옵션: {args[i]}");
            }
        }

        return new StartupOptions(exe, runs, outDir, budget, enforce, timeout, cold, dataDir, idle, discard, features, grace);
    }
}

/// <summary>
/// 실행 한 번의 결과. 표식은 프로세스 시작 뒤 경과 ms. 뒤쪽 칸은 TSK-07-07 이 더했다:
/// 앱 표식 app-init·composed·settings-loaded·shell-attached, 유휴 뒤 스레드·핸들 수, Trace 로그에서 읽은 돈/건너뛴 시작 작업·상태 칸 수.
/// </summary>
public sealed record StartupRun(
    int Index, double? AppBuilderMs, double? WindowCreatedMs, double? FirstRenderMs, double? InputReadyMs,
    double? PrivateMb, double? CommitMb, double? WorkingSetMb, bool Ok,
    double? AppInitMs = null, double? ComposedMs = null, double? SettingsLoadedMs = null, double? ShellAttachedMs = null,
    int? Threads = null, int? Handles = null, int? TasksRan = null, int? TasksSkipped = null, int? StatusShown = null, int StatusSkipped = 0);

/// <summary>
/// 게시본을 띄워 시작 표식(<c>ORANDA_PERF</c>, App 의 PerfMarks)을 읽는다.
/// 표식 규약: 환경 변수 ORANDA_PERF=1 이면 App 이 <c>ORANDA_PERF stage=&lt;appbuilder|window-created|first-render|input-ready&gt; ms=&lt;경과&gt;</c> 한 줄씩
/// stdout 과 ORANDA_PERF_FILE 에 쓴다. first-render = 실제 첫 프레임 뒤, input-ready = 그 뒤 디스패처가 비어 입력을 받는 시점.
/// </summary>
public static class StartupRunner
{
    public static StartupRun RunOnce(StartupOptions opt, int index)
    {
        ArgumentNullException.ThrowIfNull(opt);
        if (opt.Exe is null || !File.Exists(opt.Exe))
        {
            throw new FileNotFoundException("게시본 실행 파일이 없다.", opt.Exe);
        }

        var exe = Path.GetFullPath(opt.Exe);
        string? copyDir = null;
        string? tempData = null;
        try
        {
            if (opt.ColdCopy)
            {
                copyDir = Path.Combine(Path.GetTempPath(), "oranda-perf-cold-" + Guid.NewGuid().ToString("N"));
                CopyDirectory(Path.GetDirectoryName(exe)!, copyDir);
                exe = Path.Combine(copyDir, Path.GetFileName(exe));
            }

            var dataDir = opt.DataDir is null ? null : Path.GetFullPath(opt.DataDir); // 앱은 실행 파일 폴더에서 뜨므로 상대 경로면 도구와 앱이 다른 폴더를 본다
            if (dataDir is null)
            {
                tempData = Path.Combine(Path.GetTempPath(), "oranda-perf-data-" + Guid.NewGuid().ToString("N"));
                dataDir = tempData;
            }

            if (opt.Features is not null)
            {
                FeaturePreset.Parse(opt.Features).WriteSettings(dataDir);
            }

            return Launch(opt, exe, dataDir, index);
        }
        finally
        {
            TryDelete(copyDir);
            TryDelete(tempData);
        }
    }

    private static StartupRun Launch(StartupOptions opt, string exe, string dataDir, int index)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        var markFile = Path.Combine(Path.GetTempPath(), $"oranda-perf-{Environment.ProcessId}-{index}-{Guid.NewGuid():N}.txt");
        psi.Environment["ORANDA_PERF"] = "1";
        psi.Environment["ORANDA_PERF_FILE"] = markFile;
        psi.Environment["ORANDA_DATA_DIR"] = dataDir;
        var marks = new Dictionary<string, double>();
        var logOffset = LogLength(dataDir);
        using var firstRender = new ManualResetEventSlim();
        using var inputReady = new ManualResetEventSlim();
        using var p = new Process { StartInfo = psi };

        void Parse(string? line)
        {
            if (!TryParseMark(line, out var stage, out var ms))
            {
                return;
            }

            lock (marks)
            {
                marks[stage] = ms;
            }

            if (stage == "first-render")
            {
                firstRender.Set();
            }

            if (stage == "input-ready")
            {
                inputReady.Set();
            }
        }

        p.OutputDataReceived += (_, e) => Parse(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        var deadline = DateTime.UtcNow.AddSeconds(opt.TimeoutS);
        DateTime? graceUntil = null; // input-ready 표식이 없는 옛 App 이면 first-render 뒤 InputGraceSeconds 만 더 기다린다.
        while (!inputReady.IsSet && DateTime.UtcNow < deadline && !p.HasExited && (graceUntil is null || DateTime.UtcNow < graceUntil))
        {
            if (firstRender.IsSet)
            {
                graceUntil ??= DateTime.UtcNow.AddSeconds(opt.InputGraceSeconds);
            }

            ReadMarkFile(markFile, Parse);
            Thread.Sleep(20);
        }

        ReadMarkFile(markFile, Parse);
        var got = firstRender.IsSet;
        MemorySample mem = new(null, null, null);
        int? threads = null, handles = null;
        if (got && !p.HasExited)
        {
            Thread.Sleep(opt.IdleSeconds * 1000);
            mem = MemoryProbe.Read(p.Id);
            try
            {
                p.Refresh();
                threads = p.Threads.Count;
                handles = p.HandleCount;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // 읽는 사이에 끝났거나 이 OS 가 알려 주지 않는다.
            }
        }

        var counts = LogCounts.Parse(ReadLogSince(dataDir, logOffset));

        try
        {
            if (!p.HasExited)
            {
                p.Kill(true);
                p.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // 이미 끝났다.
        }

        TryDelete(markFile);
        lock (marks)
        {
            double? Get(string k) => marks.TryGetValue(k, out var v) ? v : null;
            return new StartupRun(index, Get("appbuilder"), Get("window-created"), Get("first-render"), Get("input-ready"),
                mem.PrivateMb, mem.CommitMb, mem.WorkingSetMb, got,
                Get("app-init"), Get("composed"), Get("settings-loaded"), Get("shell-attached"),
                threads, handles, counts.TasksRan, counts.TasksSkipped, counts.StatusShown, counts.StatusSkipped);
        }
    }

    // ---- Trace 로그 (고정 데이터 폴더는 여러 실행의 줄이 쌓이므로 이번 실행이 시작할 때의 길이부터 읽는다) ----

    private static long LogLength(string dataDir)
    {
        try
        {
            var dir = Path.Combine(dataDir, "logs");
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "oranda-*.log").Sum(f => new FileInfo(f).Length) : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>시작 때 길이 이후에 덧붙은 로그 글. 날짜가 바뀌어 파일이 둘이어도 전체 길이 기준으로 맨 뒤 글만 돌려준다. 읽을 수 없으면 null.</summary>
    public static string? ReadLogSince(string dataDir, long offset)
    {
        try
        {
            var dir = Path.Combine(dataDir, "logs");
            if (!Directory.Exists(dir))
            {
                return null;
            }

            var sb = new System.Text.StringBuilder();
            foreach (var f in Directory.EnumerateFiles(dir, "oranda-*.log").OrderBy(f => f, StringComparer.Ordinal))
            {
                using var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (offset >= stream.Length)
                {
                    offset -= stream.Length;
                    continue;
                }

                stream.Seek(offset, SeekOrigin.Begin);
                offset = 0;
                using var reader = new StreamReader(stream);
                sb.Append(reader.ReadToEnd());
            }

            return sb.ToString();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>표식 한 줄을 읽는다. 형식이 아니면 false.</summary>
    public static bool TryParseMark(string? line, out string stage, out double ms)
    {
        stage = string.Empty;
        ms = 0;
        if (line is null || !line.StartsWith("ORANDA_PERF ", StringComparison.Ordinal))
        {
            return false;
        }

        string? s = null;
        double? v = null;
        foreach (var kv in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var parts = kv.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            if (parts[0] == "stage")
            {
                s = parts[1];
            }
            else if (parts[0] == "ms" && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                v = d;
            }
        }

        if (s is null || v is null)
        {
            return false;
        }

        stage = s;
        ms = v.Value;
        return true;
    }

    private static void ReadMarkFile(string path, Action<string?> parse)
    {
        try
        {
            // 앱이 같은 파일에 덧붙이는 중이므로 쓰기 공유로 연다(안 그러면 앱의 PerfMarks.Mark 가 그 표식을 버린다, TSK-01-10).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } l)
            {
                parse(l);
            }
        }
        catch (IOException)
        {
            // 아직 쓰는 중이거나 없다.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
