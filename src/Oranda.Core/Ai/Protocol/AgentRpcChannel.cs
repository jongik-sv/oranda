using System.Text;

namespace Oranda.Core.Ai.Protocol;

/// <summary>
/// 스트림(파이프·소켓) 위의 줄 단위 틀. 읽기는 한 번에 한 곳에서, 쓰기는 여러 곳에서 불러도 된다(쓰기끼리는 순서대로 한 줄씩).
/// 플랫폼 API 를 부르지 않고 <see cref="Stream"/> 만 쓴다. 파이프를 여는 일은 호출하는 쪽 몫이다.
/// <b>취소는 채널을 끝내는 일이다</b>: 읽기·쓰기 도중 취소되면 줄 틀이 깨질 수 있으므로 호출한 쪽은 연결을 닫고 이 채널을 다시 쓰지 않는다.
/// </summary>
public sealed class AgentRpcChannel : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    public AgentRpcChannel(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public void Dispose() => _writeGate.Dispose();

    /// <summary>
    /// 줄 하나를 읽는다(끝의 <c>\n</c>·<c>\r</c> 제외, 빈 줄은 건너뜀). 연결이 닫혔으면 null.
    /// 한 줄이 <see cref="AgentRpc.MaxMessageBytes"/> 를 넘으면 <see cref="InvalidDataException"/> — 호출한 쪽이 연결을 닫는다.
    /// </summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken ct)
    {
        var line = new MemoryStream();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _stream.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                if (_end == 0)
                {
                    // 연결이 닫혔다. 개행 없이 끝난 마지막 조각은 불완전한 메시지로 보고 버린다.
                    return null;
                }
            }

            var span = _buffer.AsSpan(_start, _end - _start);
            var newline = span.IndexOf((byte)'\n');
            var take = newline < 0 ? span.Length : newline;
            if (line.Length + take > AgentRpc.MaxMessageBytes)
            {
                throw new InvalidDataException($"메시지가 {AgentRpc.MaxMessageBytes} 바이트를 넘는다.");
            }

            line.Write(span[..take]);
            if (newline < 0)
            {
                _start = _end;
                continue;
            }

            _start += newline + 1;
            var text = Utf8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
            if (text.Length == 0)
            {
                line.SetLength(0);
                continue;
            }

            return text;
        }
    }

    /// <summary>메시지를 한 줄로 쓴다. 직렬화 결과에 줄바꿈이 있으면 틀이 깨지므로 거부한다.</summary>
    public async ValueTask WriteAsync(AgentRpcMessage message, CancellationToken ct)
    {
        var json = AgentRpc.Serialize(message);
        if (json.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("직렬화 결과에 줄바꿈이 들어 있다.");
        }

        var bytes = Utf8.GetBytes(json + "\n");
        if (bytes.Length > AgentRpc.MaxMessageBytes + 1)
        {
            throw new InvalidDataException($"메시지가 {AgentRpc.MaxMessageBytes} 바이트를 넘는다.");
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
