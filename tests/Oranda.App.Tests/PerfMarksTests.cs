namespace Oranda.App.Tests;

// TSK-01-10: 시작 표식 기록(ORANDA_PERF_FILE)이 실패해도 앱을 죽이지 않는다 (docs/itest/TSK-11-03.md DEF-03).
public class PerfMarksTests
{
    [Fact]
    public void Append_does_not_throw_when_another_stream_holds_the_file_exclusively()
    {
        var path = Path.Combine(Path.GetTempPath(), "oranda-perfmarks-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            using (var held = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                // 전제: 이 OS 에서도 잠금이 실제로 쓰기를 막는다(Unix 는 flock 으로 흉내 낸다). 막지 못하면 시험이 아무것도 증명하지 못한다.
                Assert.Throws<IOException>(() => File.AppendAllText(path, "x"));

                var error = Record.Exception(() => PerfMarks.Append(path, "ORANDA_PERF stage=test ms=1.0"));

                Assert.Null(error);
            }

            // 잠금이 풀리면 다시 기록된다.
            PerfMarks.Append(path, "ORANDA_PERF stage=after ms=2.0");
            Assert.Equal("ORANDA_PERF stage=after ms=2.0" + Environment.NewLine, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Append_does_not_throw_when_the_folder_does_not_exist()
    {
        var path = Path.Combine(Path.GetTempPath(), "oranda-perfmarks-" + Guid.NewGuid().ToString("N"), "missing", "marks.txt");

        var error = Record.Exception(() => PerfMarks.Append(path, "ORANDA_PERF stage=test ms=1.0"));

        Assert.Null(error);
    }
}
