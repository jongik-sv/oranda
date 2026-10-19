namespace Oranda.Language.Tests;

// 뼈대. 렉서·파서·포매터 회귀 시험이 여기에 들어온다(TRD 11).
public class LanguageAssemblyTests
{
    [Fact]
    public void Language_assembly_is_loadable()
    {
        Assert.Equal("Oranda.Language", typeof(LanguageAssembly).Assembly.GetName().Name);
    }
}
