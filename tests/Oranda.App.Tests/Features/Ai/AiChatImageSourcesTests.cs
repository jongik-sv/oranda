using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Tests.Shell.Headless;
using SkiaSharp;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>대화 이미지 입구(img0): Windows DIB·macOS TIFF·PNG 클립보드 형식을 읽고, 글이 함께 있으면 알린다.</summary>
public class AiChatImageSourcesTests
{
    private static byte[] Png(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>2×2 24비트 DIB(BITMAPINFOHEADER + 아래 줄부터 BGR 픽셀, 줄은 4바이트 배수).</summary>
    private static byte[] Dib2x2()
    {
        var dib = new byte[40 + 16];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 2);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 24);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20), 16);
        for (var i = 0; i < 4; i++)
        {
            dib[40 + ((i / 2) * 8) + ((i % 2) * 3)] = 0;       // B
            dib[40 + ((i / 2) * 8) + ((i % 2) * 3) + 1] = 0;   // G
            dib[40 + ((i / 2) * 8) + ((i % 2) * 3) + 2] = 255; // R
        }

        return dib;
    }

    [Fact]
    public void A_windows_dib_gets_a_file_header_and_normalizes_to_png()
    {
        var bmp = AiImageSources.DibToBmp(Dib2x2());

        Assert.NotNull(bmp);
        Assert.Equal((byte)'B', bmp![0]);
        var image = AiImageNormalizer.Normalize(bmp).Image!;
        Assert.Equal("image/png", image.Attachment.MediaType);
        Assert.Equal((2, 2), (image.Width, image.Height));
        using var decoded = SKBitmap.Decode(image.Attachment.Data);
        Assert.Equal(255, decoded.GetPixel(0, 0).Red);
    }

    [Fact]
    public void Bytes_that_are_not_a_dib_are_not_converted()
    {
        Assert.Null(AiImageSources.DibToBmp("not a dib at all, definitely not"u8));
        Assert.Null(AiImageSources.DibToBmp([1, 2, 3]));
    }

    [Fact]
    public void A_macos_tiff_is_converted_to_png_with_sips()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return; // sips 는 macOS 에만 있다
        }

        var dir = Path.Combine(Path.GetTempPath(), "oranda-tiff-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = Path.Combine(dir, "a.png");
            var tiff = Path.Combine(dir, "a.tiff");
            File.WriteAllBytes(png, Png(40, 30, SKColors.CornflowerBlue));
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/sips") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "-s", "format", "tiff", png, "--out", tiff })
            {
                start.ArgumentList.Add(a);
            }

            using (var p = System.Diagnostics.Process.Start(start)!)
            {
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }

            var converted = AiImageSources.TiffToPng(File.ReadAllBytes(tiff));

            Assert.NotNull(converted);
            Assert.Equal("image/png", AiImageNormalizer.SniffMediaType(converted));
            Assert.Equal((40, 30), (AiImageNormalizer.Normalize(converted!).Image!.Width, AiImageNormalizer.Normalize(converted).Image!.Height));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Image_files_win_over_the_file_name_text_that_comes_with_them_but_plain_text_wins_over_a_bitmap()
    {
        Assert.True(new AiClipboardRead(HasText: true, [[1]], FromFiles: true).PreferImages); // 탐색기·Finder 복사: 파일 이름 글이 함께 옴
        Assert.False(new AiClipboardRead(HasText: true, [[1]]).PreferImages); // 글과 그림이 함께 있는 앱(표 셀 등)은 글 우선
        Assert.True(new AiClipboardRead(HasText: false, [[1]]).PreferImages);
        Assert.False(new AiClipboardRead(HasText: false, []).PreferImages);
    }

    [Fact]
    public Task Only_image_extensions_are_taken_from_files() => HeadlessHost.Run(() =>
    {
        Assert.True(AiImageSources.IsImageFileName("plan.PNG"));
        Assert.True(AiImageSources.IsImageFileName("a.jpeg"));
        Assert.False(AiImageSources.IsImageFileName("notes.txt"));
        Assert.False(AiImageSources.IsImageFileName(null));
    });

    [Fact]
    public Task A_png_on_the_clipboard_is_read_and_reports_no_text() => HeadlessHost.Run(async () =>
    {
        var window = new Window { Width = 100, Height = 100 };
        window.Show();
        var clipboard = window.Clipboard;
        Assert.NotNull(clipboard);

        var png = Png(20, 10, SKColors.Orange);
        var item = new global::Avalonia.Input.DataTransferItem();
        item.Set(DataFormat.CreateBytesPlatformFormat("image/png"), png);
        var transfer = new global::Avalonia.Input.DataTransfer();
        transfer.Add(item);
        await clipboard!.SetDataAsync(transfer);

        var read = await AiImageSources.ReadClipboardAsync(clipboard);

        Assert.False(read.HasText);
        var bytes = Assert.Single(read.Images);
        Assert.Equal((20, 10), (AiImageNormalizer.Normalize(bytes).Image!.Width, AiImageNormalizer.Normalize(bytes).Image!.Height));
        window.Close();
    });

    [Fact]
    public Task A_text_only_clipboard_yields_no_image_and_reports_text() => HeadlessHost.Run(async () =>
    {
        var window = new Window { Width = 100, Height = 100 };
        window.Show();
        var clipboard = window.Clipboard;
        Assert.NotNull(clipboard);

        await clipboard!.SetTextAsync("SELECT 1 FROM dual");

        var read = await AiImageSources.ReadClipboardAsync(clipboard);

        Assert.True(read.HasText);
        Assert.Empty(read.Images);
        window.Close();
    });
}
