using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

static class Program
{
    // -p:DefineConstants=STA 면 Main 에 [STAThread] (Avalonia 진입점과 같게)
#if STA
    [STAThread]
#endif
    static void Main()
    {
        var ms = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ORANDA_PERF stage=main ms={ms:F1}"));
        // HELLO_STA_THREAD=1: 새 STA 스레드를 띄우는 비용(= 런타임의 STA 초기화 비용)을 잰다.
        if (Environment.GetEnvironmentVariable("HELLO_STA_THREAD") == "1")
        {
            var sw0 = Stopwatch.StartNew();
            double inThread = 0, co = 0;
            var th = new Thread(() =>
            {
                inThread = sw0.Elapsed.TotalMilliseconds;
                var s1 = Stopwatch.StartNew();
                _ = OleInitialize(IntPtr.Zero);
                co = s1.Elapsed.TotalMilliseconds;
            });
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
            th.Join();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"sta-thread-start={inThread:F1}ms oleinit-on-sta={co:F1}ms"));
        }

        // HELLO_RAW_STA=1: 런타임 STA 설정 없이 새 스레드에서 CoInitializeEx(STA) 를 직접 부른다.
        if (Environment.GetEnvironmentVariable("HELLO_RAW_STA") == "1")
        {
            double co = 0; int hr = 0;
            var th = new Thread(() =>
            {
                var s1 = Stopwatch.StartNew();
                hr = CoInitializeEx(IntPtr.Zero, 2);
                co = s1.Elapsed.TotalMilliseconds;
            });
            th.Start();
            th.Join();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"raw-coinit-sta={co:F1}ms(hr={hr:X})"));
        }

        // HELLO_LOADLIBS=<디렉터리>: Avalonia 네이티브 DLL 적재 시간을 하나씩 잰다(백신 검사 비용 확인).
        var libDir = Environment.GetEnvironmentVariable("HELLO_LOADLIBS");
        if (!string.IsNullOrEmpty(libDir))
        {
            foreach (var lib in new[] { "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll" })
            {
                var s1 = Stopwatch.StartNew();
                NativeLibrary.Load(Path.Combine(libDir, lib));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"load {lib}={s1.Elapsed.TotalMilliseconds:F1}ms"));
            }
        }

        // HELLO_MSGWND=1: 메시지 전용 창 하나(CreateWindowEx, HWND_MESSAGE) 만드는 비용 — Avalonia Win32Platform.CreateMessageWindow 와 같은 호출.
        if (Environment.GetEnvironmentVariable("HELLO_MSGWND") == "1")
        {
            for (var i = 0; i < 2; i++)
            {
                var s1 = Stopwatch.StartNew();
                var h = CreateWindowExW(0, "STATIC", "probe", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"msgwnd#{i}={s1.Elapsed.TotalMilliseconds:F1}ms ok={h != IntPtr.Zero}"));
            }
        }

        // HELLO_OLE=1: STA 없이 시작한 뒤 COM/OLE 초기화 비용만 따로 잰다.
        if (Environment.GetEnvironmentVariable("HELLO_OLE") == "1")
        {
            var sw = Stopwatch.StartNew();
            var hr1 = CoInitializeEx(IntPtr.Zero, 2);
            var co = sw.Elapsed.TotalMilliseconds;
            var hr2 = OleInitialize(IntPtr.Zero);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"coinit={co:F1}ms(hr={hr1:X}) oleinit={sw.Elapsed.TotalMilliseconds - co:F1}ms(hr={hr2:X})"));
        }
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
}
