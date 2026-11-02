using System;
using System.Globalization;
using System.IO;
using Oranda.Core.Ai.Contracts;
using SkiaSharp;

namespace Oranda.App.Features.Ai.Chat;

/// <summary>정규화를 마친 이미지 한 장: 보낼 첨부, 입력칸·보낸 줄에 보일 축소 PNG, 보낼 이미지의 가로·세로.</summary>
/// <param name="Attachment">모델로 보낼 이미지(PNG·JPEG·WebP·GIF 중 하나).</param>
/// <param name="Preview">화면에 보일 축소 PNG(긴 변 <see cref="AiImageNormalizer.PreviewMaxEdge"/> 이하).</param>
/// <param name="Width">보낼 이미지의 가로 픽셀.</param>
/// <param name="Height">보낼 이미지의 세로 픽셀.</param>
/// <param name="Changed">원본과 다른 바이트로 다시 만들었다(축소·재인코딩).</param>
public sealed record AiNormalizedImage(AiImageAttachment Attachment, byte[] Preview, int Width, int Height, bool Changed);

/// <summary>정규화 결과. 성공이면 <see cref="Image"/>, 실패면 사용자에게 보일 이유 <see cref="Error"/> 한 가지가 있다.</summary>
public sealed record AiImageNormalizeResult(AiNormalizedImage? Image, string? Error)
{
    public static AiImageNormalizeResult Ok(AiNormalizedImage image) => new(image, null);

    public static AiImageNormalizeResult Fail(string error) => new(null, error);
}

/// <summary>
/// 대화에 붙이는 이미지를 보내기 전에 다듬는다(설계 §6.6): 허용 형식은 PNG·JPEG·WebP·GIF(디코딩할 수 있는 BMP 는 PNG 로 바꾼다),
/// 긴 변이 <see cref="MaxEdge"/> 를 넘으면 줄이고, 인코딩은 PNG 를 유지하되 <see cref="MaxBytes"/> 를 넘으면 JPEG 로 다시 인코딩하고, 그래도 넘으면 거절한다.
/// 고칠 것이 없으면 원본 바이트를 그대로 쓴다. 깨진 데이터·지원하지 않는 형식은 예외 없이 이유 글로 돌려준다. SkiaSharp(Avalonia 가 이미 쓰는 구현)만 쓴다.
/// </summary>
public static class AiImageNormalizer
{
    /// <summary>한 번에 붙일 수 있는 장수.</summary>
    public const int MaxCount = 5;

    /// <summary>긴 변 상한(픽셀). 세로로 긴 실행 계획 캡처의 글자를 읽을 수 있도록 넉넉히 둔다.</summary>
    public const int MaxEdge = 4000;

    /// <summary>한 장의 인코딩 크기 상한.</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>
    /// API 경로가 대화 하나에 받는 메시지 크기 상한(JSON 글자 수, 이미지 data URL 포함, 36 MB). 한 번에 붙일 수 있는 최대치(<see cref="MaxCount"/>장 × <see cref="MaxBytes"/>)는
    /// base64 로 약 35 MB 라 첫 요청이 상한 안에 들어간다. 같은 대화에 이미지를 거듭 보내 합이 이 상한을 넘으면 드라이버가 <c>context_limit</c> 으로 막고 새 대화를 안내한다.
    /// </summary>
    public const long ApiConversationLimitBytes = 36L * 1024 * 1024;

    /// <summary>한 번에 붙일 수 있는 최대치를 base64 로 인코딩한 글자 수(API 첫 요청에 실리는 이미지 크기).</summary>
    public const long MaxRequestBase64Chars = (long)MaxCount * ((MaxBytes + 2) / 3) * 4;

    /// <summary>입력으로 받는 파일 크기 상한(이보다 큰 파일은 읽지 않고 거절한다).</summary>
    public const int MaxInputBytes = 40 * 1024 * 1024;

    /// <summary>디코딩 전에 확인하는 픽셀 수 상한(압축 폭탄이 메모리를 채우지 않게).</summary>
    public const long MaxPixels = 50_000_000;

    /// <summary>화면에 보일 축소 이미지의 긴 변(입력칸 칸 크기 132×84 의 2배).</summary>
    public const int PreviewMaxEdge = 264;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>JPEG 로 다시 인코딩할 때 시도할 품질(앞에서부터).</summary>
    private static readonly int[] JpegQualities = [85, 60];

    public static AiImageNormalizeResult Normalize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            return AiImageNormalizeResult.Fail("이미지가 비어 있습니다.");
        }

        if (data.Length > MaxInputBytes)
        {
            return AiImageNormalizeResult.Fail(string.Create(CultureInfo.CurrentCulture, $"이미지가 너무 큽니다({SizeText(data.Length)}). {SizeText(MaxInputBytes)} 이하만 붙일 수 있습니다."));
        }

        try
        {
            return NormalizeCore(data);
        }
#pragma warning disable CA1031 // 디코더가 깨진 데이터에 어떤 예외를 내든 첨부 한 장의 실패로 끝내야 한다(대화 창이 죽지 않게).
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return AiImageNormalizeResult.Fail("이미지를 읽지 못했습니다. PNG·JPEG·WebP·GIF 파일인지 확인하세요.");
        }
    }

    /// <summary>바이트 수를 「1.8 MB」 처럼 보인다.</summary>
    public static string SizeText(long bytes) => bytes switch
    {
        >= 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024d / 1024d:0.0} MB"),
        >= 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024d:0} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes} B"),
    };

    /// <summary>파일 머리 바이트로 형식을 알아본다(확장자는 믿지 않는다). 허용 형식이 아니면 null.</summary>
    public static string? SniffMediaType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(PngSignature))
        {
            return "image/png";
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        {
            return "image/gif";
        }

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }

    private static AiImageNormalizeResult NormalizeCore(byte[] data)
    {
        using var codec = SKCodec.Create(new MemoryStream(data, writable: false));
        if (codec is null)
        {
            return AiImageNormalizeResult.Fail("이미지를 읽지 못했습니다. PNG·JPEG·WebP·GIF 파일인지 확인하세요.");
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
        {
            return AiImageNormalizeResult.Fail(string.Create(CultureInfo.CurrentCulture, $"이미지가 너무 큽니다({info.Width}×{info.Height} 픽셀)."));
        }

        var sniffed = SniffMediaType(data);
        var needsResize = Math.Max(info.Width, info.Height) > MaxEdge;
        if (sniffed is not null && !needsResize && data.Length <= MaxBytes)
        {
            using var original = SKBitmap.Decode(codec, info.WithAlphaType(SKAlphaType.Premul));
            return original is null
                ? AiImageNormalizeResult.Fail("이미지를 읽지 못했습니다.")
                : AiImageNormalizeResult.Ok(new AiNormalizedImage(new AiImageAttachment(sniffed, data), PreviewOf(original), info.Width, info.Height, Changed: false));
        }

        using var decoded = SKBitmap.Decode(codec, info.WithAlphaType(SKAlphaType.Premul));
        if (decoded is null)
        {
            return AiImageNormalizeResult.Fail("이미지를 읽지 못했습니다.");
        }

        using var fitted = needsResize ? Resize(decoded, MaxEdge) : null;
        var bitmap = fitted ?? decoded;

        // PNG 를 유지한다. 허용 형식이 아니었던(BMP 등) 것도 PNG 로 바꾸고, 크기가 넘을 때만 JPEG 로 다시 인코딩한다.
        var png = Encode(bitmap, SKEncodedImageFormat.Png, 100);
        if (png.Length <= MaxBytes)
        {
            return AiImageNormalizeResult.Ok(new AiNormalizedImage(new AiImageAttachment("image/png", png), PreviewOf(bitmap), bitmap.Width, bitmap.Height, Changed: true));
        }

        using var flat = Flatten(bitmap);
        foreach (var quality in JpegQualities)
        {
            var jpeg = Encode(flat, SKEncodedImageFormat.Jpeg, quality);
            if (jpeg.Length <= MaxBytes)
            {
                return AiImageNormalizeResult.Ok(new AiNormalizedImage(new AiImageAttachment("image/jpeg", jpeg), PreviewOf(bitmap), bitmap.Width, bitmap.Height, Changed: true));
            }
        }

        return AiImageNormalizeResult.Fail(string.Create(CultureInfo.CurrentCulture, $"이미지를 {SizeText(MaxBytes)} 이하로 줄이지 못했습니다. 더 작은 영역을 캡처해 주세요."));
    }

    private static SKBitmap Resize(SKBitmap source, int maxEdge)
    {
        var scale = (double)maxEdge / Math.Max(source.Width, source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var resized = source.Resize(new SKImageInfo(width, height, source.ColorType, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return resized ?? throw new InvalidOperationException("resize failed");
    }

    /// <summary>투명한 곳을 흰색으로 채운 사본(JPEG 는 투명을 못 담아 검게 나오기 때문).</summary>
    private static SKBitmap Flatten(SKBitmap source)
    {
        var flat = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(flat);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, 0, 0);
        return flat;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, quality) ?? throw new InvalidOperationException("encode failed");
        return encoded.ToArray();
    }

    private static byte[] PreviewOf(SKBitmap source)
    {
        if (Math.Max(source.Width, source.Height) <= PreviewMaxEdge)
        {
            return Encode(source, SKEncodedImageFormat.Png, 100);
        }

        using var small = Resize(source, PreviewMaxEdge);
        return Encode(small, SKEncodedImageFormat.Png, 100);
    }
}
