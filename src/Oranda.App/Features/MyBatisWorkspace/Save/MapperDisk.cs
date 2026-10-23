using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>디스크 매퍼 한 번 읽은 결과. 파일이 없으면 <see cref="Exists"/> 거짓, 읽었지만 매퍼로 읽지 못하면 <see cref="Error"/>.</summary>
internal sealed record MapperDiskSnapshot(bool Exists, IMyBatisMapperDocument? Document, string? Error)
{
    public string? Hash => Document?.Hash;
}

/// <summary>
/// 디스크 매퍼 읽기(design.md 5.3). 반드시 바이트로 읽는다(<c>Parse(string)</c> 은 euc-kr 매퍼를 잘못 읽음, 02-01 후속 메모).
/// Language 형은 이 클래스 안에만 둔다: 시작 때 훑는 감시 서비스·처리기 필드에 두면 Language 가 로드된다(D-006).
/// </summary>
internal static class MapperDisk
{
    private const int Retries = 3;

    public static IMyBatisMapperParser Parser { get; } = new MapperParser();

    /// <summary>
    /// 지금 디스크 내용. IDE 가 쓰는 도중이라 잠겨 있으면 잠깐 기다려 다시 읽는다. 끝까지 못 읽으면 <see cref="MapperDiskSnapshot.Error"/>.
    /// 깨진 xml(쓰는 도중 포함)은 <see cref="FormatException"/> 의 글을 <see cref="MapperDiskSnapshot.Error"/> 로 담는다.
    /// </summary>
    public static MapperDiskSnapshot Read(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (!File.Exists(path))
            {
                return new MapperDiskSnapshot(false, null, null);
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException) when (attempt < Retries)
            {
                Thread.Sleep(40);
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new MapperDiskSnapshot(File.Exists(path), null, ex.Message);
            }

            try
            {
                return new MapperDiskSnapshot(true, Parser.Parse(bytes), null);
            }
            catch (FormatException ex)
            {
                return new MapperDiskSnapshot(true, null, ex.Message);
            }
        }
    }
}
