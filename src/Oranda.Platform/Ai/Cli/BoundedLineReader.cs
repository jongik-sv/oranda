using System.Text;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// 한 줄의 길이에 상한을 둔 줄 읽기. <see cref="TextReader.ReadLineAsync()"/> 는 줄바꿈이 오기 전까지 한 줄을 끝없이 쌓으므로,
/// 줄바꿈 없이 큰 글을 쏟아내는 CLI 에서는 메모리 상한이 듣지 않는다. 줄이 상한을 넘으면 앞부분을 버리고 뒤 <c>maxChars</c> 글자를 남긴다(결과는 보통 끝에 온다).
/// 줄 끝은 <see cref="TextReader.ReadLineAsync()"/> 와 같이 <c>\n</c>·<c>\r</c>·<c>\r\n</c> 이다. 버리는 글을 쌓지 않도록 상한의 두 배를 넘을 때마다 한꺼번에 깎는다
/// (한 줄이 차지하는 메모리는 상한의 두 배와 읽기 덩어리 하나 안쪽이다).
/// </summary>
internal sealed class BoundedLineReader(TextReader reader, int maxChars)
{
    private const int ChunkChars = 4096;

    private readonly char[] _chunk = new char[ChunkChars];
    private int _position;
    private int _length;
    private bool _skipLineFeed;

    /// <summary>지금까지 읽은 줄 중 상한을 넘어 앞부분을 버린 것이 있다.</summary>
    public bool Truncated { get; private set; }

    /// <summary>다음 줄. 입력이 끝났으면 null.</summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var any = false;
        while (true)
        {
            if (_position >= _length)
            {
                _length = await reader.ReadAsync(_chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                _position = 0;
                if (_length == 0)
                {
                    return any ? Finish(line) : null;
                }
            }

            if (_skipLineFeed)
            {
                _skipLineFeed = false;
                if (_chunk[_position] == '\n')
                {
                    _position++;
                    continue;
                }
            }

            var rest = _chunk.AsSpan(_position, _length - _position);
            var end = rest.IndexOfAny('\r', '\n');
            if (end < 0)
            {
                Append(line, rest);
                _position = _length;
                any = true;
                continue;
            }

            Append(line, rest[..end]);
            _skipLineFeed = rest[end] == '\r';
            _position += end + 1;
            return Finish(line);
        }
    }

    private void Append(StringBuilder line, ReadOnlySpan<char> text)
    {
        line.Append(text);
        if (line.Length > maxChars * 2L)
        {
            line.Remove(0, line.Length - maxChars);
            Truncated = true;
        }
    }

    private string Finish(StringBuilder line)
    {
        if (line.Length > maxChars)
        {
            line.Remove(0, line.Length - maxChars);
            Truncated = true;
        }

        return line.ToString();
    }
}
