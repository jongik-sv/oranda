using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace Oranda.App.Shell;

/// <summary>
/// <see cref="Trace"/> 로 남기는 오류(명령 실행 실패·시작 작업 실패 등)를 데이터 폴더 <c>logs/oranda-yyyyMMdd.log</c> 에 쓴다.
/// 받는 곳이 없으면 오류가 조용히 사라져 원인을 알 수 없다. 처리되지 않은 예외도 같은 파일에 남긴다.
/// </summary>
public static class TraceLog
{
    private static bool s_started;

    public static string? FilePath { get; private set; }

    public static void Start(string logsDirectory)
    {
        if (s_started)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(logsDirectory);
            FilePath = Path.Combine(logsDirectory, "oranda-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            var writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            Trace.Listeners.Add(new TextWriterTraceListener(writer) { TraceOutputOptions = TraceOptions.DateTime });
            Trace.AutoFlush = true;
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Trace.TraceError("처리되지 않은 예외: {0}", e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, e) => Trace.TraceError("관찰되지 않은 작업 예외: {0}", e.Exception);
            s_started = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 로그를 못 남겨도 앱은 계속 쓴다.
        }
    }
}
