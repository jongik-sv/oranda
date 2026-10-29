using System.Text;
using Oranda.Core.MyBatis;
using Oranda.Platform.MyBatis;

namespace Oranda.Platform.Tests.MyBatis;

// 소유: TSK-02-01. 매퍼 원자적 쓰기(design.md 5.2): 임시 파일 → 바꾸기, 인코딩·BOM·권한 유지, 실패 때 원본 보존.
public sealed class MapperFileWriterTests : IDisposable
{
    private static readonly byte[] Original = [1, 2, 3, 4];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-mfw-" + Guid.NewGuid().ToString("N"));
    private readonly MapperFileWriter _writer = new();

    public MapperFileWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(_dir))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }

        Directory.Delete(_dir, recursive: true);
    }

    private string P(string name = "m.xml") => Path.Combine(_dir, name);

    private static MapperFileFormat Fmt(string enc, bool bom) => new(enc, bom, "\n", true);

    [Fact]
    public async Task WriteAsync_ExistingFile_ReplacedNoTempLeft()
    {
        var path = P();
        await File.WriteAllBytesAsync(path, Original);
        await _writer.WriteAsync(path, "<a/>", Fmt("utf-8", false), CancellationToken.None);
        Assert.Equal(Encoding.UTF8.GetBytes("<a/>"), await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".oranda-tmp"));
    }

    [Fact]
    public async Task WriteAsync_NewFile_Created()
    {
        var path = P("new.xml");
        await _writer.WriteAsync(path, "<b/>", Fmt("utf-8", false), CancellationToken.None);
        Assert.Equal(Encoding.UTF8.GetBytes("<b/>"), await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".oranda-tmp"));
    }

    [Theory]
    [InlineData("utf-8", true, "가a\r\nb\r\n")]
    [InlineData("utf-8", false, "가a\nb")]
    [InlineData("euc-kr", false, "한글 매퍼\r\n")]
    [InlineData("euc-kr", false, "CP949 확장 똠햏\n")]
    public async Task WriteAsync_PreservesEncodingAndBom(string enc, bool bom, string text)
    {
        var path = P();
        await _writer.WriteAsync(path, text, Fmt(enc, bom), CancellationToken.None);
        // euc-kr 선언은 CP949 로 쓴다(KS X 1001 글자는 euc-kr 과 같은 바이트).
        var encoding = enc == "euc-kr" ? CodePagesEncodingProvider.Instance.GetEncoding(949)! : new UTF8Encoding(false);
        var expected = (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : []).Concat(encoding.GetBytes(text)).ToArray();
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task WriteAsync_UnencodableChar_ThrowsBeforeTemp()
    {
        var path = P();
        await File.WriteAllBytesAsync(path, Original);
        var ex = await Assert.ThrowsAsync<IOException>(
            () => _writer.WriteAsync(path, "😀", Fmt("euc-kr", false), CancellationToken.None));
        Assert.IsType<EncoderFallbackException>(ex.InnerException);
        Assert.Equal(Original, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".oranda-tmp"));
    }

    [Fact]
    public async Task WriteAsync_ReadOnlyTarget_ThrowsAndCleansTemp()
    {
        var path = P();
        await File.WriteAllBytesAsync(path, Original);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _writer.WriteAsync(path, "x", Fmt("utf-8", false), CancellationToken.None));
        Assert.Equal(Original, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".oranda-tmp"));
    }

    [Fact]
    public async Task WriteAsync_Cancelled_NoChange()
    {
        var path = P();
        await File.WriteAllBytesAsync(path, Original);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _writer.WriteAsync(path, "x", Fmt("utf-8", false), cts.Token));
        Assert.Equal(Original, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".oranda-tmp"));
    }

    [Fact]
    public async Task WriteAsync_FailsAfterTempWritten_DeletesTempKeepsTarget()
    {
        // 대상 경로가 폴더면 임시 파일 쓰기는 되고 바꾸기(File.Move)에서 실패한다.
        var path = P("folder.xml");
        Directory.CreateDirectory(path);
        await Assert.ThrowsAnyAsync<IOException>(
            () => _writer.WriteAsync(path, "<a/>", Fmt("utf-8", false), CancellationToken.None));
        Assert.True(Directory.Exists(path));
        Assert.False(File.Exists(path + ".oranda-tmp"));
    }

    [Fact]
    public async Task WriteAsync_ExistingFile_KeepsUnixMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = P();
        await File.WriteAllBytesAsync(path, Original);
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        File.SetUnixFileMode(path, mode);
        await _writer.WriteAsync(path, "<a/>", Fmt("utf-8", false), CancellationToken.None);
        Assert.Equal(mode, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task WriteAsync_SymbolicLink_WritesTargetKeepsLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = P("target.xml");
        var link = P("link.xml");
        await File.WriteAllBytesAsync(target, Original);
        File.CreateSymbolicLink(link, target);
        await _writer.WriteAsync(link, "<a/>", Fmt("utf-8", false), CancellationToken.None);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(Encoding.UTF8.GetBytes("<a/>"), await File.ReadAllBytesAsync(target));
    }

    // Windows 에서는 다른 쪽(폴더 트리 색인·IDE·검색기)이 대상을 잠깐 열어 두면 바꾸기가 IOException(공유 위반, 1175 「바꿀 파일을 제거할 수 없습니다」)으로 실패한다.
    // 임시 파일을 남긴 채 몇 번 다시 바꾼다. 잡은 쪽이 놓는 순간은 재시도 알림(BeforeReplaceRetry)으로 정해 시간에 기대지 않는다.
    [Fact]
    public async Task WriteAsync_TargetBrieflyHeldOpen_RetriesAndReplaces()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // rename 은 열린 파일도 바꾼다(공유 위반이 없다)
        }

        var path = P();
        await File.WriteAllBytesAsync(path, Original);
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var retries = 0;
        var writer = new MapperFileWriter
        {
            ReplaceRetryDelay = TimeSpan.Zero,
            BeforeReplaceRetry = () =>
            {
                retries++;
                holder.Dispose(); // 첫 실패 뒤 잡은 쪽이 놓는다
            },
        };
        try
        {
            await writer.WriteAsync(path, "<a/>", Fmt("utf-8", false), CancellationToken.None);
        }
        finally
        {
            holder.Dispose();
        }

        Assert.Equal(1, retries);
        Assert.Equal(Encoding.UTF8.GetBytes("<a/>"), await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".oranda-tmp"));
    }

    [Fact]
    public async Task WriteAsync_TargetHeldOpenThroughAllRetries_ThrowsKeepsTargetAndCleansTemp()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = P();
        await File.WriteAllBytesAsync(path, Original);
        var retries = 0;
        var writer = new MapperFileWriter { ReplaceRetryDelay = TimeSpan.Zero, BeforeReplaceRetry = () => retries++ };
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => writer.WriteAsync(path, "<a/>", Fmt("utf-8", false), CancellationToken.None));
        }

        Assert.Equal(MapperFileWriter.ReplaceAttempts - 1, retries);
        Assert.Equal(Original, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".oranda-tmp"));
    }
}
