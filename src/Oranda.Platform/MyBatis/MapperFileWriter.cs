using System.Text;
using Oranda.Core.MyBatis;

namespace Oranda.Platform.MyBatis;

/// <summary>매퍼 원자적 쓰기(design.md 5.2) 구현 — TSK-02-01. 같은 폴더 임시 파일에 쓰고 바꾼다.</summary>
public sealed class MapperFileWriter : IMapperFileWriter
{
    private const string TempSuffix = ".oranda-tmp";
    private const int EucKrCodePage = 51949;
    private const int Cp949CodePage = 949;
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <inheritdoc />
    public async Task WriteAsync(string path, string text, MapperFileFormat format, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(format);

        // 바이트를 먼저 만든다: 표현할 수 없는 글자는 임시 파일 전에 IOException(계약대로 부른 쪽이 알리고 고침을 남긴다).
        byte[] bytes;
        try
        {
            bytes = BuildBytes(text, format);
        }
        catch (EncoderFallbackException ex)
        {
            throw new IOException($"{format.EncodingName} 로 쓸 수 없는 글자가 있습니다(위치 {ex.Index}).", ex);
        }

        // 심볼릭 링크면 링크를 일반 파일로 바꾸지 않도록 대상 파일에 쓴다.
        if (new FileInfo(path).LinkTarget is not null)
        {
            path = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }

        var exists = File.Exists(path);
        // Unix 의 rename 은 읽기 전용 파일도 덮어쓰므로 명시적으로 막는다.
        if (exists && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
        {
            throw new UnauthorizedAccessException($"읽기 전용 파일입니다: {path}");
        }

        var tmp = path + TempSuffix;
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (exists)
            {
                // Unix 의 Replace 는 rename 이라 원본 권한 비트를 잃는다. 임시 파일에 먼저 옮겨 둔다.
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(tmp, File.GetUnixFileMode(path));
                }

                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (IOException)
            {
                // 정리 실패는 원래 예외를 가리지 않는다.
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    private static byte[] BuildBytes(string text, MapperFileFormat format)
    {
        var encoding = GetStrictEncoding(format.EncodingName);
        var body = encoding.GetBytes(text);
        if (!format.HasBom)
        {
            return body;
        }

        var result = new byte[Utf8Bom.Length + body.Length];
        Utf8Bom.CopyTo(result, 0);
        body.CopyTo(result, Utf8Bom.Length);
        return result;
    }

    /// <summary>
    /// 예외 fallback 인코딩. euc-kr 은 CP949(코드 페이지 949)로 쓴다: 파서(<c>MapperEncoding</c>)가 같은 규칙으로 읽으므로
    /// CP949 확장 글자(똠·햏 등)가 든 매퍼도 왕복이 같다.
    /// </summary>
    private static Encoding GetStrictEncoding(string name)
    {
        Encoding found;
        try
        {
            found = Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            found = CodePagesEncodingProvider.Instance.GetEncoding(name)
                ?? throw new ArgumentException($"알 수 없는 인코딩입니다: {name}", nameof(name));
        }

        if (found.CodePage == EucKrCodePage)
        {
            found = CodePagesEncodingProvider.Instance.GetEncoding(Cp949CodePage)!;
        }

        var clone = (Encoding)found.Clone();
        clone.EncoderFallback = EncoderFallback.ExceptionFallback;
        clone.DecoderFallback = DecoderFallback.ExceptionFallback;
        return clone;
    }
}
