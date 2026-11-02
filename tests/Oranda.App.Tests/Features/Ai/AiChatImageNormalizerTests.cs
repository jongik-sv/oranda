using Oranda.App.Features.Ai.Chat;
using SkiaSharp;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>대화 이미지 정규화(img1): 고칠 것이 없으면 원본 그대로, 긴 변 4000px 초과는 축소, 5 MB 초과는 JPEG, 그래도 넘거나 깨졌으면 거절.</summary>
public class AiChatImageNormalizerTests
{
    private static byte[] Png(int width, int height, Func<int, int, SKColor> pixel, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var span = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = pixel(x, y);
                var offset = (y * width + x) * 4;
                span[offset] = color.Red;
                span[offset + 1] = color.Green;
                span[offset + 2] = color.Blue;
                span[offset + 3] = 255;
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    [Fact]
    public void A_small_png_is_kept_byte_for_byte_and_gets_a_preview()
    {
        var png = Png(300, 200, (x, y) => new SKColor((byte)(x % 256), (byte)(y % 256), 80));
        var result = AiImageNormalizer.Normalize(png);

        Assert.Null(result.Error);
        var image = result.Image!;
        Assert.Equal("image/png", image.Attachment.MediaType);
        Assert.Same(png, image.Attachment.Data);
        Assert.False(image.Changed);
        Assert.Equal((300, 200), (image.Width, image.Height));
        using var preview = SKBitmap.Decode(image.Preview);
        Assert.True(Math.Max(preview.Width, preview.Height) <= AiImageNormalizer.PreviewMaxEdge);
    }

    [Fact]
    public void A_tall_capture_over_4000_pixels_is_shrunk_to_the_limit_and_stays_png()
    {
        var png = Png(800, 5000, (x, y) => (y / 40 % 2 == 0) ? SKColors.White : new SKColor(30, 60, (byte)(x % 256)));
        var result = AiImageNormalizer.Normalize(png);

        var image = result.Image!;
        Assert.True(image.Changed);
        Assert.Equal("image/png", image.Attachment.MediaType);
        Assert.Equal(AiImageNormalizer.MaxEdge, image.Height);
        Assert.Equal(640, image.Width);
        using var decoded = SKBitmap.Decode(image.Attachment.Data);
        Assert.Equal((640, 4000), (decoded.Width, decoded.Height));
    }

    [Fact]
    public void A_png_over_5_MB_becomes_a_jpeg_within_the_limit()
    {
        // 완만한 색 위에 잔 무늬를 얹어 PNG 는 크고(압축이 안 됨) JPEG 는 작게 나오는 사진 같은 이미지
        var random = new Random(7);
        var png = Png(2400, 1800, (x, y) => new SKColor((byte)(x / 10 + random.Next(0, 24)), (byte)(y / 8 + random.Next(0, 24)), (byte)(120 + random.Next(0, 24))));
        Assert.True(png.Length > AiImageNormalizer.MaxBytes, $"시험 재료가 5 MB 이하입니다: {png.Length}");

        var image = AiImageNormalizer.Normalize(png).Image!;

        Assert.Equal("image/jpeg", image.Attachment.MediaType);
        Assert.True(image.Attachment.Data.Length <= AiImageNormalizer.MaxBytes);
        Assert.True(image.Changed);
    }

    [Fact]
    public void An_image_that_still_exceeds_5_MB_as_jpeg_is_refused_with_a_reason()
    {
        var random = new Random(11);
        var png = Png(3600, 3600, (_, _) => new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));

        var result = AiImageNormalizer.Normalize(png);

        Assert.Null(result.Image);
        Assert.Contains("5.0 MB", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Broken_empty_and_unsupported_data_are_refused_without_throwing()
    {
        Assert.NotNull(AiImageNormalizer.Normalize([]).Error);
        Assert.NotNull(AiImageNormalizer.Normalize("이건 이미지가 아닙니다"u8.ToArray()).Error);
        var truncated = Png(100, 100, (_, _) => SKColors.Red)[..40]; // 머리만 남은 PNG
        Assert.Null(AiImageNormalizer.Normalize(truncated).Image);
    }

    [Fact]
    public void Jpeg_and_gif_headers_are_recognised_and_other_formats_are_not()
    {
        Assert.Equal("image/jpeg", AiImageNormalizer.SniffMediaType([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]));
        Assert.Equal("image/gif", AiImageNormalizer.SniffMediaType("GIF89a...."u8));
        Assert.Equal("image/webp", AiImageNormalizer.SniffMediaType("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(AiImageNormalizer.SniffMediaType("BM......"u8));
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>가로·세로가 큰 1비트 회색 PNG(전부 0 이라 압축하면 작다). 디코딩 전 픽셀 수 상한 검사만 보려는 것이다.</summary>
    private static byte[] PngHeaderClaiming(int width, int height)
    {
        static void Chunk(MemoryStream stream, string type, byte[] data)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length);
            var body = new byte[4 + data.Length];
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
            data.CopyTo(body, 4);
            stream.Write(body);
            var checksum = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(checksum, Crc32(body));
            stream.Write(checksum);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 1; // 비트 깊이 1
        ihdr[9] = 0; // 회색
        Chunk(png, "IHDR", ihdr);
        using var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[1 + ((width + 7) / 8)]; // 필터 0 + 전부 0
            for (var y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }

        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    [Fact]
    public void An_image_claiming_more_than_the_pixel_limit_is_refused_before_decoding()
    {
        var result = AiImageNormalizer.Normalize(PngHeaderClaiming(8000, 8000)); // 6,400만 픽셀 > MaxPixels

        Assert.Null(result.Image);
        Assert.Contains("너무 큽니다", result.Error, StringComparison.Ordinal);
        Assert.Contains("8000×8000", result.Error, StringComparison.Ordinal);
    }
}

