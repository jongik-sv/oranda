using System.Globalization;

// 가짜 앱: FAKE_MODE 에 따라 App 의 PerfMarks 와 같은 표식을 쓴다.
//   ok      : appbuilder/window-created/first-render/input-ready 를 파일(ORANDA_PERF_FILE)과 stdout 에 쓰고 60 초 기다린다(도구가 끈다).
//   nomarks : 표식 없이 60 초 기다린다(시간 초과 경로).
//   crash   : 표식 하나 쓰고 종료 코드 3 으로 끝난다.
//   old     : first-render 까지만 쓴다(input-ready 없는 옛 App).
// 데이터 폴더(ORANDA_DATA_DIR)에 data.txt 를 만들어 도구가 폴더를 넘겼는지 시험이 확인하게 한다.
var mode = Environment.GetEnvironmentVariable("FAKE_MODE") ?? "ok";
var file = Environment.GetEnvironmentVariable("ORANDA_PERF_FILE");
var data = Environment.GetEnvironmentVariable("ORANDA_DATA_DIR");
if (!string.IsNullOrEmpty(data))
{
    Directory.CreateDirectory(data);
    File.WriteAllText(Path.Combine(data, "data.txt"), AppContext.BaseDirectory);
}

void Mark(string stage, double ms)
{
    var line = string.Create(CultureInfo.InvariantCulture, $"ORANDA_PERF stage={stage} ms={ms:F1}");
    Console.Out.WriteLine(line);
    Console.Out.Flush();
    if (!string.IsNullOrEmpty(file))
    {
        File.AppendAllText(file, line + Environment.NewLine);
    }
}

switch (mode)
{
    case "ok":
        Mark("appbuilder", 10);
        Mark("window-created", 20);
        Mark("first-render", 30);
        Mark("input-ready", 40);
        Thread.Sleep(60000);
        break;
    case "old":
        Mark("appbuilder", 10);
        Mark("first-render", 30);
        Thread.Sleep(60000);
        break;
    case "nomarks":
        Thread.Sleep(60000);
        break;
    case "crash":
        Mark("appbuilder", 10);
        return 3;
    default:
        return 4;
}

return 0;
