namespace Oranda.Tibero.Tests;

public class TiberoDriverInfoTests
{
    [Fact]
    public void Info_reports_only_what_the_app_host_has_loaded()
    {
        // 드라이버는 빌드에 없다(D-046-1). 앱 쪽 호스트가 읽기 전에는 기본 컨텍스트에서 찾을 수도 없다.
        // (같은 프로세스의 실제 드라이버 IT 가 먼저 읽었으면 IsLinked 가 참일 수 있어 그 경우는 버전만 본다.)
        Assert.Equal(TiberoDriverInfo.IsLinked, TiberoDriverInfo.Version is not null);
        if (!TiberoDriverInfo.IsLinked)
        {
            Assert.Throws<FileNotFoundException>(() => System.Reflection.Assembly.Load("Tibero.DataAccess"));
        }
    }
}
