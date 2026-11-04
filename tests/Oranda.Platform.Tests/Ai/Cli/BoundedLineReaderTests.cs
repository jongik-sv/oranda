using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>줄 길이에 상한을 둔 읽기: 줄 끝 규칙은 <see cref="TextReader.ReadLineAsync()"/> 와 같고, 상한을 넘는 줄은 뒤를 남긴다.</summary>
public class BoundedLineReaderTests
{
    private static async Task<List<string>> ReadAllAsync(BoundedLineReader reader)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    [Fact]
    public async Task Line_ends_follow_the_text_reader_rules_including_a_crlf_pair()
    {
        var reader = new BoundedLineReader(new StringReader("a\nb\r\nc\rd\n\ne"), 100);

        Assert.Equal(["a", "b", "c", "d", "", "e"], await ReadAllAsync(reader));
        Assert.False(reader.Truncated);
    }

    [Fact]
    public async Task A_crlf_pair_split_across_two_reads_is_one_line_end()
    {
        var reader = new BoundedLineReader(new ChunkedReader("a\r", "\nb\n"), 100);

        Assert.Equal(["a", "b"], await ReadAllAsync(reader));
    }

    [Fact]
    public async Task An_endless_line_is_cut_to_its_tail_without_being_kept_whole()
    {
        var source = new GeneratedReader(totalChars: 50_000_000);
        var reader = new BoundedLineReader(source, 1000);

        var lines = await ReadAllAsync(reader);

        var line = Assert.Single(lines);
        Assert.Equal(1000, line.Length);
        Assert.True(reader.Truncated);
        Assert.Equal(50_000_000, source.Delivered);
        Assert.EndsWith("Z", line, StringComparison.Ordinal); // 마지막 글자(뒤)가 남는다
    }

    [Fact]
    public async Task Cancellation_stops_the_read()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var reader = new BoundedLineReader(new GeneratedReader(1_000_000), 10);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadLineAsync(cts.Token));
    }

    // 글을 만들어 내며 주는 읽기(전체를 메모리에 두지 않는다). 마지막 글자는 'Z' 이다.
    private sealed class GeneratedReader(long totalChars) : TextReader
    {
        public long Delivered { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, totalChars - Delivered);
            buffer.Span[..count].Fill('x');
            if (count > 0 && Delivered + count == totalChars)
            {
                buffer.Span[count - 1] = 'Z';
            }

            Delivered += count;
            return new ValueTask<int>(count);
        }
    }

    private sealed class ChunkedReader(params string[] chunks) : TextReader
    {
        private int _next;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (_next >= chunks.Length)
            {
                return new ValueTask<int>(0);
            }

            var chunk = chunks[_next++];
            chunk.CopyTo(buffer.Span);
            return new ValueTask<int>(chunk.Length);
        }
    }
}
