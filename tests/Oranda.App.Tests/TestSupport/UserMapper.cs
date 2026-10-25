namespace Oranda.App.Tests.TestSupport;

/// <summary>
/// 사용자 매퍼(저장소 밖 저장소 밖 파일) 시험용 경로. 환경 변수 ORANDA_USER_MAPPER 에 경로를 주면 쓰고,
/// 없거나 파일이 없으면 null 이라 해당 시험은 아무것도 하지 않고 끝난다.
/// </summary>
internal static class UserMapper
{
    public const string EnvironmentVariable = "ORANDA_USER_MAPPER";

    public static string? Path
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return !string.IsNullOrWhiteSpace(value) && File.Exists(value) ? value : null;
        }
    }
}
