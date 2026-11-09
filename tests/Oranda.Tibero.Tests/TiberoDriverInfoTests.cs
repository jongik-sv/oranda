namespace Oranda.Tibero.Tests;

public class TiberoDriverInfoTests
{
    [Fact]
    public void IsLinked_matches_whether_the_driver_assembly_is_loadable()
    {
        // 드라이버 있는 빌드(ORANDA_TIBERO_FEED)와 없는 빌드 모두에서 성립한다.
        var loadable = false;
        try
        {
            loadable = System.Reflection.Assembly.Load("Tibero.DataAccess") is not null;
        }
        catch (FileNotFoundException)
        {
            // 드라이버 없음.
        }

        Assert.Equal(loadable, TiberoDriverInfo.IsLinked);
        Assert.Equal(TiberoDriverInfo.IsLinked, TiberoDriverInfo.Version is not null);
    }

#if TIBERO_DRIVER
    [Fact]
    public void Driver_build_links_the_driver()
    {
        Assert.True(TiberoDriverInfo.IsLinked);
        Assert.StartsWith("1.", TiberoDriverInfo.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void Real_driver_exception_has_the_shape_the_classifier_looks_for()
    {
        // 분류기는 형식 이름 TiberoException 과 공개 int Number 로 드라이버 예외를 찾는다(리플렉션). 실제 드라이버 형식이 그대로인지 못박는다.
        var type = typeof(global::Tibero.DataAccess.Client.TiberoException);

        Assert.Equal("TiberoException", type.Name);
        Assert.Equal(typeof(int), type.GetProperty("Number")?.PropertyType);
        Assert.True(typeof(Exception).IsAssignableFrom(type));
    }
#endif
}
