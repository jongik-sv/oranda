namespace Oranda.Testing;

/// <summary>
/// 시험용 임시 폴더 경로. 만들지는 않고(필요한 시험이 <see cref="Directory.CreateDirectory(string)"/> 로 만든다) 경로만 정한다.
/// <see cref="Dispose"/> 는 폴더를 통째로 지우되 <see cref="IOException"/>(다른 프로세스가 잡고 있는 파일 등)은 무시한다.
/// 문자열이 필요한 자리에는 그대로 쓸 수 있다(<see cref="string"/> 으로 암시 변환).
/// </summary>
public sealed class TempDirectory(string prefix) : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    public static implicit operator string(TempDirectory directory) => directory.Path;

    public override string ToString() => Path;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }
}
