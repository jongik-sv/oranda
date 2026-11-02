using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Oranda.App.Features.Ai.Chat;

/// <summary>클립보드 읽기 결과: 글이 함께 있는지, 읽어 낸 이미지 바이트(형식은 정규화가 가린다), 이미지 파일 목록에서 읽었는지.</summary>
public sealed record AiClipboardRead(bool HasText, IReadOnlyList<byte[]> Images, bool FromFiles = false)
{
    /// <summary>붙여 넣기를 이미지로 받는다: 글이 없거나, 명시적으로 복사한 이미지 파일이다(파일을 복사하면 파일 이름 글이 함께 오는 환경이 있다).</summary>
    public bool PreferImages => Images.Count > 0 && (!HasText || FromFiles);
}

/// <summary>
/// 대화 이미지의 입구 세 곳(붙여 넣기·끌어 놓기·파일 선택)에서 이미지 바이트를 꺼낸다. 형식 판정·축소는 <see cref="AiImageNormalizer"/> 가 맡는다.
/// 클립보드는 OS 마다 형식 이름이 달라 다음 순서로 시도하고, 어느 단계에서 읽히면 멈춘다.
/// <list type="number">
/// <item>이미지 파일 목록(탐색기·Finder 에서 복사한 파일). 이때 함께 오는 아이콘 그림은 쓰지 않는다.</item>
/// <item>Avalonia 가 해석하는 비트맵(PNG 로 다시 저장한다).</item>
/// <item>플랫폼 형식 바이트: PNG·JPEG·GIF·BMP, Windows 의 DIB(파일 머리가 없는 BMP), macOS 의 TIFF(Skia 가 못 읽어 <c>sips</c> 로 PNG 변환).</item>
/// </list>
/// </summary>
public static class AiImageSources
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    /// <summary>플랫폼 형식 이름 후보(소문자 부분 일치), 앞에 있을수록 먼저 읽는다.</summary>
    private static readonly string[] RawFormatHints = ["png", "jpeg", "jpg", "gif", "webp", "bmp", "dib", "tiff"];

    /// <summary>이름 끝의 확장자가 이미지 파일이다.</summary>
    public static bool IsImageFileName(string? name) =>
        !string.IsNullOrEmpty(name) && ImageExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    /// <summary>클립보드에서 이미지를 읽는다. 이미지가 없으면 <see cref="AiClipboardRead.Images"/> 가 비어 있다. 읽다가 실패해도 예외를 내지 않는다.</summary>
    public static async Task<AiClipboardRead> ReadClipboardAsync(IClipboard clipboard, int max = AiImageNormalizer.MaxCount)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        IReadOnlyList<DataFormat> formats;
        try
        {
            formats = await clipboard.GetDataFormatsAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // 클립보드는 다른 프로세스가 점유하면 어떤 예외든 낸다: 붙여 넣기를 읽지 못한 것으로만 다룬다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return new AiClipboardRead(false, []);
        }

        var hasText = formats.Contains(DataFormat.Text);
        if (await TryReadAsync(() => ReadFilesAsync(clipboard, formats, max)).ConfigureAwait(true) is { Count: > 0 } files)
        {
            return new AiClipboardRead(hasText, files, FromFiles: true);
        }

        if (await TryReadAsync(() => ReadBitmapAsync(clipboard, formats)).ConfigureAwait(true) is { Count: > 0 } bitmap)
        {
            return new AiClipboardRead(hasText, bitmap);
        }

        var raw = await TryReadAsync(() => ReadRawAsync(clipboard, formats)).ConfigureAwait(true);
        return new AiClipboardRead(hasText, raw ?? []);
    }

    /// <summary>
    /// 끌어 놓은 데이터에 이미지가 있다: 이미지 파일이 있거나, 글 없이 비트맵만 있다. 글을 끌어 놓는 동작은 입력칸이 그대로 받도록 이미지가 아니라고 답한다
    /// (파일을 끌어 놓을 때 파일 이름 글이 함께 와도 이미지 파일이면 이미지로 본다).
    /// </summary>
    public static bool HasImage(IDataTransfer data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Contains(DataFormat.File)
            ? data.TryGetFiles() is { } files && files.OfType<IStorageFile>().Any(f => IsImageFileName(f.Name))
            : !data.Contains(DataFormat.Text) && data.Contains(DataFormat.Bitmap);
    }

    /// <summary>끌어 놓은 데이터에서 이미지를 읽는다(이미지 파일, 없으면 비트맵). 최대 <paramref name="max"/> 장. 이미지가 없으면 빈 목록.</summary>
    public static async Task<IReadOnlyList<byte[]>> ReadDroppedAsync(IDataTransfer data, int max = AiImageNormalizer.MaxCount)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            if (data.TryGetFiles() is { Length: > 0 } files)
            {
                var read = await ReadFilesAsync(files, max).ConfigureAwait(true);
                if (read.Count > 0)
                {
                    return read;
                }
            }

            if (data.TryGetBitmap() is { } bitmap)
            {
                using (bitmap)
                {
                    return [await Task.Run(() => EncodePng(bitmap)).ConfigureAwait(true)];
                }
            }
        }
#pragma warning disable CA1031 // 끌어 놓기 데이터를 못 읽는 것은 이미지가 없는 것과 같게 다룬다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
        }

        return [];
    }

    /// <summary>이미지 파일들의 내용을 읽는다. 이미지 확장자가 아니거나 못 읽는 파일은 건너뛰고, 너무 큰 파일은 한도를 넘긴 만큼만 읽어 넘긴다(정규화가 거절한다).</summary>
    public static async Task<IReadOnlyList<byte[]>> ReadFilesAsync(IEnumerable<IStorageItem> items, int max = AiImageNormalizer.MaxCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        List<byte[]> result = [];

        // 읽기 전에 장수를 자른다(폴더째 끌어 놓아도 파일당 최대 40 MB 를 수십 장 읽지 않게). 넘친 장수는 붙일 때 「5장까지」 안내가 나가도록 한 장 더 읽는다.
        foreach (var file in items.OfType<IStorageFile>().Where(f => IsImageFileName(f.Name)).Take(Math.Max(0, max) + 1))
        {
            try
            {
                await using var stream = await file.OpenReadAsync().ConfigureAwait(true);
                result.Add(await ReadAllAsync(stream).ConfigureAwait(true));
            }
#pragma warning disable CA1031 // 파일 하나를 못 읽어도 나머지 첨부는 계속한다.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
#pragma warning restore CA1031
            {
            }
        }

        return result;
    }

    /// <summary>
    /// Windows 클립보드의 DIB(<c>BITMAPINFOHEADER</c> 로 시작하는 파일 머리 없는 BMP)에 BMP 파일 머리를 붙인다. DIB 가 아니면 null.
    /// </summary>
    public static byte[]? DibToBmp(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize is not (12 or 40 or 52 or 56 or 108 or 124) || headerSize > dib.Length)
        {
            return null;
        }

        int bitCount;
        var compression = 0;
        var colorsUsed = 0;
        if (headerSize == 12)
        {
            bitCount = BinaryPrimitives.ReadInt16LittleEndian(dib[10..]);
        }
        else
        {
            bitCount = BinaryPrimitives.ReadInt16LittleEndian(dib[14..]);
            compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
            colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(dib[32..]);
        }

        var entrySize = headerSize == 12 ? 3 : 4;
        var tableColors = bitCount is > 0 and <= 8 ? (colorsUsed > 0 ? colorsUsed : 1 << bitCount) : colorsUsed;
        var masks = headerSize == 40 && compression is 3 or 6 ? (compression == 3 ? 12 : 16) : 0; // BI_BITFIELDS·BI_ALPHABITFIELDS 의 색 마스크
        var offset = 14 + headerSize + masks + (tableColors * entrySize);
        if (offset > 14 + dib.Length)
        {
            return null;
        }

        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), offset);
        dib.CopyTo(bmp.AsSpan(14));
        return bmp;
    }

    private static async Task<IReadOnlyList<byte[]>?> TryReadAsync(Func<Task<IReadOnlyList<byte[]>?>> read)
    {
        try
        {
            return await read().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // 한 단계가 실패하면 다음 단계(다른 형식)로 넘어간다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<byte[]>?> ReadFilesAsync(IClipboard clipboard, IReadOnlyList<DataFormat> formats, int max)
    {
        if (!formats.Contains(DataFormat.File))
        {
            return null;
        }

        var files = await clipboard.TryGetFilesAsync().ConfigureAwait(true);
        return files is { Length: > 0 } ? await ReadFilesAsync(files, max).ConfigureAwait(true) : null;
    }

    private static async Task<IReadOnlyList<byte[]>?> ReadBitmapAsync(IClipboard clipboard, IReadOnlyList<DataFormat> formats)
    {
        if (!formats.Contains(DataFormat.Bitmap))
        {
            return null;
        }

        using var bitmap = await clipboard.TryGetBitmapAsync().ConfigureAwait(true);
        return bitmap is null ? null : [await Task.Run(() => EncodePng(bitmap)).ConfigureAwait(true)];
    }

    private static async Task<IReadOnlyList<byte[]>?> ReadRawAsync(IClipboard clipboard, IReadOnlyList<DataFormat> formats)
    {
        var candidates = formats
            .Where(f => f != DataFormat.Text && f != DataFormat.File && f != DataFormat.Bitmap)
            .Select(f => (Format: f, Rank: Array.FindIndex(RawFormatHints, hint => f.Identifier.Contains(hint, StringComparison.OrdinalIgnoreCase))))
            .Where(c => c.Rank >= 0)
            .OrderBy(c => c.Rank);
        foreach (var (format, _) in candidates)
        {
            var bytes = await clipboard.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat(format.Identifier)).ConfigureAwait(true);
            if (bytes is not { Length: > 0 } || bytes.Length > AiImageNormalizer.MaxInputBytes)
            {
                continue; // 비었거나 한도를 넘는 원시 데이터는 읽지 않는다
            }

            var usable = await Task.Run(() => Prepare(bytes, format.Identifier)).ConfigureAwait(true); // TIFF 변환은 외부 프로세스를 기다리므로 화면 스레드에서 돌리지 않는다
            if (usable is not null)
            {
                return [usable];
            }
        }

        return null;
    }

    /// <summary>형식 이름에 맞게 정규화가 읽을 수 있는 바이트로 바꾼다(DIB → BMP, TIFF → PNG). 바꿀 수 없으면 null.</summary>
    private static byte[]? Prepare(byte[] bytes, string identifier)
    {
        if (identifier.Contains("tif", StringComparison.OrdinalIgnoreCase))
        {
            return TiffToPng(bytes); // 호출 쪽(ReadRawAsync)이 백그라운드에서 부른다
        }

        if (AiImageNormalizer.SniffMediaType(bytes) is not null || bytes.AsSpan().StartsWith("BM"u8))
        {
            return bytes;
        }

        return DibToBmp(bytes);
    }

    /// <summary>macOS 에 기본으로 있는 <c>sips</c> 로 TIFF 를 PNG 로 바꾼다(Skia 가 TIFF 를 못 읽는다). macOS 가 아니거나 실패하면 null.</summary>
    internal static byte[]? TiffToPng(byte[] tiff)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var directory = Path.Combine(Path.GetTempPath(), "oranda-clip-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "in.tiff");
            var target = Path.Combine(directory, "out.png");
            File.WriteAllBytes(source, tiff);
            var start = new ProcessStartInfo("/usr/bin/sips") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-s", "format", "png", source, "--out", target })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 && File.Exists(target) ? File.ReadAllBytes(target) : null;
        }
#pragma warning disable CA1031 // 변환을 못 하면 이 형식은 건너뛰고 다른 입구(파일 선택)를 안내한다.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
#pragma warning restore CA1031
        {
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 임시 폴더 정리 실패는 무시한다(다음 실행에서 운영체제가 정리).
            }
        }
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, CancellationToken.None).ConfigureAwait(true)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > AiImageNormalizer.MaxInputBytes)
            {
                break; // 너무 큰 파일은 끝까지 읽지 않는다: 넘친 만큼만 넘기면 정규화가 「너무 큽니다」 로 거절한다
            }
        }

        return buffer.ToArray();
    }
}
