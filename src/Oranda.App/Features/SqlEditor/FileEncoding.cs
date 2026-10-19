using System.Text;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// SQL 파일 읽기·쓰기용 인코딩 판별. 이름은 작업 공간 <c>tabs[].encoding</c> 에 그대로 저장된다.
/// 이름: <c>utf-8</c>(BOM 없음), <c>utf-8-bom</c>, <c>utf-16</c>(LE), <c>utf-16be</c>, <c>cp949</c>(UTF-8 로 읽히지 않는 파일의 기본).
/// </summary>
public static class FileEncoding
{
    static FileEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Get(string name) => name switch
    {
        "utf-8-bom" => new UTF8Encoding(true),
        "utf-16" => new UnicodeEncoding(false, true),
        "utf-16be" => new UnicodeEncoding(true, true),
        "cp949" => Encoding.GetEncoding(949),
        _ => new UTF8Encoding(false),
    };

    /// <summary>바이트에서 글자와 인코딩 이름을 얻는다.</summary>
    public static (string Text, string Name) Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(true).GetString(bytes, 3, bytes.Length - 3), "utf-8-bom");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "utf-16");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "utf-16be");
        }

        try
        {
            return (new UTF8Encoding(false, true).GetString(bytes), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(949).GetString(bytes), "cp949");
        }
    }

    /// <summary>글자를 인코딩 이름에 맞게 바이트로(BOM 포함 여부도 이름대로).</summary>
    public static byte[] Encode(string text, string name)
    {
        var enc = Get(name);
        var preamble = enc.GetPreamble();
        var body = enc.GetBytes(text);
        if (preamble.Length == 0)
        {
            return body;
        }

        var all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        return all;
    }
}
