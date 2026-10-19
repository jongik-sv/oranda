using System.Globalization;
using System.Text;

namespace Oranda.App.Features.LobRowView;

/// <summary>LOB 내용 도우미(TSK-06-05): 16진 보기, 이미지 판별, 파일 인코딩, 스트리밍 파일 저장·읽기. 화면과 무관해 단위 시험한다.</summary>
public static class LobContent
{
    /// <summary>CLOB 편집기에 올리는 최대 문자 수. 넘으면 앞부분만 보여 주고 읽기 전용(파일 저장은 전체를 스트림으로).</summary>
    public const int MaxEditChars = 1_000_000;

    /// <summary>16진 보기에 싣는 최대 바이트.</summary>
    public const int MaxHexBytes = 64 * 1024;

    /// <summary>이미지 미리보기를 위해 읽는 최대 BLOB 크기.</summary>
    public const long MaxImageBytes = 16L * 1024 * 1024;

    /// <summary>파일에서 불러와 DB 로 보낼 수 있는 최대 크기(바인드가 메모리 배열·문자열을 쓴다).</summary>
    public const long MaxUploadBytes = 256L * 1024 * 1024;

    public const int CopyBufferSize = 81920;

    /// <summary>CLOB 파일 인코딩 이름(화면 목록). 첫 항목이 기본.</summary>
    public static IReadOnlyList<string> EncodingNames { get; } = ["UTF-8", "UTF-8 (BOM)", "UTF-16 LE", "UTF-16 BE"];

    public static Encoding GetEncoding(string? name) => name switch
    {
        "UTF-8 (BOM)" => new UTF8Encoding(true),
        "UTF-16 LE" => new UnicodeEncoding(false, true),
        "UTF-16 BE" => new UnicodeEncoding(true, true),
        _ => new UTF8Encoding(false),
    };

    /// <summary>16진 덤프: <c>00000000  41 42 ...  |AB..|</c> 한 줄 16바이트.</summary>
    public static string HexDump(ReadOnlySpan<byte> data, long baseOffset = 0)
    {
        var sb = new StringBuilder(data.Length * 4);
        for (var i = 0; i < data.Length; i += 16)
        {
            var n = Math.Min(16, data.Length - i);
            sb.Append((baseOffset + i).ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (var j = 0; j < 16; j++)
            {
                if (j < n)
                {
                    sb.Append(data[i + j].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                }
                else
                {
                    sb.Append("   ");
                }

                if (j == 7)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(" |");
            for (var j = 0; j < n; j++)
            {
                var b = data[i + j];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            sb.AppendLine("|");
        }

        return sb.ToString();
    }

    /// <summary>머리 바이트로 PNG·JPEG·GIF·BMP·WebP 를 알아본다. 아니면 null.</summary>
    public static string? DetectImageMime(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (head.Length >= 6 && (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8)))
        {
            return "image/gif";
        }

        if (head.Length >= 2 && head[0] == (byte)'B' && head[1] == (byte)'M')
        {
            return "image/bmp";
        }

        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>스트림을 파일로 그대로 복사한다(전체를 메모리에 올리지 않는다). 중간에 실패하면 반쯤 쓴 파일을 지운다.</summary>
    public static async Task<long> CopyToFileAsync(Stream source, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(path);
        long total = 0;
        try
        {
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
            var buffer = new byte[CopyBufferSize];
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                total += n;
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        return total;
    }

    /// <summary>UTF-8 바이트 스트림(CLOB 읽기 스트림)을 지정 인코딩 텍스트 파일로 저장한다. UTF-8(BOM 없음)이면 바이트 그대로.</summary>
    public static async Task<long> SaveTextFileAsync(Stream utf8, string path, Encoding encoding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        ArgumentNullException.ThrowIfNull(encoding);
        if (encoding is UTF8Encoding && encoding.GetPreamble().Length == 0)
        {
            return await CopyToFileAsync(utf8, path, ct).ConfigureAwait(false);
        }

        long chars = 0;
        try
        {
            using var reader = new StreamReader(utf8, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, CopyBufferSize, leaveOpen: true);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
            await using var writer = new StreamWriter(target, encoding, CopyBufferSize);
            var buffer = new char[CopyBufferSize];
            int n;
            while ((n = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                await writer.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                chars += n;
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        return chars;
    }

    /// <summary>텍스트 파일 읽기. BOM 이 있으면 그 인코딩, 없으면 UTF-8.</summary>
    public static async Task<string> ReadTextFileAsync(string path, CancellationToken ct)
    {
        EnsureUploadable(path);
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadBinaryFileAsync(string path, CancellationToken ct)
    {
        EnsureUploadable(path);
        return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
    }

    private static void EnsureUploadable(string path)
    {
        var len = new FileInfo(path).Length;
        if (len > MaxUploadBytes)
        {
            throw new InvalidOperationException($"파일이 너무 큽니다({len:N0} B). 한 번에 {MaxUploadBytes / (1024 * 1024)} MB 까지 불러올 수 있습니다.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
