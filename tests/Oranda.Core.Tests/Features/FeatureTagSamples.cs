// 꼬리표 규칙(TRD 10.2)은 형의 네임스페이스 문자열만 본다. Core 시험은 App 을 참조하지 않으므로
// App 기능 폴더와 같은 네임스페이스에 빈 표본 형을 둔다(FeatureCatalogTests 가 쓴다).

namespace Oranda.App.Features.MyBatisWorkspace.Save
{
    internal sealed class SampleStartup
    {
        internal sealed class Nested
        {
        }
    }
}

namespace Oranda.App.Features.Tuning.Assistant
{
    internal sealed class SampleHandler
    {
    }
}
