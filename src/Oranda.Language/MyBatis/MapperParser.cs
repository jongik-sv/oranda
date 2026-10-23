using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Oranda.Core.MyBatis;
using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

/// <summary>
/// 매퍼 읽기(MB-01, TRD 3.1) — TSK-02-01. XmlReader + <see cref="IXmlLineInfo"/> 로 요소 시작·끝을 얻고 원문을 직접 훑어 범위를 잡는다.
/// 원문은 고치지 않는다: 요소 원문·범위는 모두 입력 글자의 조각이다(XDocument 로 다시 직렬화하지 않는다).
/// DTD·외부 엔티티를 읽지 않는다. 바이트는 손실 없이 읽을 수 있을 때만 받는다(잘못된 바이트를 U+FFFD 로 바꾸면 저장 때 원본이 깨진다).
/// </summary>
public sealed partial class MapperParser : IMyBatisMapperParser
{
    private const string Utf8 = "utf-8";

    public IMyBatisMapperDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // 글자 앞 U+FEFF 는 XmlReader 가 받지 않으므로 뗀다. 계약대로 BOM 은 없음으로 본다(Text 는 뗀 글자).
        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        return Build(text, false, ResolveEncoding(DeclaredEncoding(text)).WebName);
    }

    public IMyBatisMapperDocument Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            throw new FormatException("UTF-16 매퍼는 읽지 않습니다(UTF-8·ASCII 호환 인코딩만).");
        }

        bool bom = bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]);
        var body = bom ? bytes[3..] : bytes;

        // xml 선언은 ASCII 로 먼저 본다(UTF-8·EUC-KR 등 ASCII 호환 인코딩만 받는다).
        var head = Encoding.ASCII.GetString(body[..Math.Min(body.Length, 256)]);
        var (encoding, name) = ResolveEncoding(DeclaredEncoding(head));

        string text;
        try
        {
            text = encoding.GetString(body);
        }
        catch (DecoderFallbackException ex)
        {
            // 위치 = 파일 바이트 기준(BOM 포함).
            int at = ex.Index + (bom ? 3 : 0);
            throw new FormatException($"{name} 로 읽을 수 없는 바이트가 있습니다(파일 {at.ToString(CultureInfo.InvariantCulture)}바이트째).", ex);
        }

        // UTF-8 은 엄격하게 읽으면 왕복이 같다. 코드 페이지 인코딩은 다시 바꿔 대조한다.
        if (encoding is not UTF8Encoding && !encoding.GetBytes(text).AsSpan().SequenceEqual(body))
        {
            throw new FormatException($"{name} 로 읽고 다시 쓰면 원본과 바이트가 달라집니다.");
        }

        return Build(text, bom, name);
    }

    private static MapperDocument Build(string text, bool bom, string encodingName)
    {
        var scan = Scan(text);
        var format = new MapperFileFormat(encodingName, bom, MainNewLine(text), text.EndsWith('\n') || text.EndsWith('\r'));
        return new MapperDocument(text, format, scan);
    }

    /// <summary>
    /// 원문을 읽어 루트·최상위 요소의 범위를 잡는다. 깨진 xml·루트가 mapper 가 아니면 줄·칸을 담은 <see cref="FormatException"/>.
    /// <see cref="MapperDocument.Check"/> 도 새 원문 검사에 이 함수를 쓴다.
    /// </summary>
    internal static MapperScan Scan(string text)
    {
        var lines = new LineStarts(text);
        var elements = new List<MyBatisElement>();
        string? ns = null;
        int rootTagEnd = -1;
        int rootCloseStart = -1;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };

        try
        {
            using var sr = new StringReader(text);
            using var reader = XmlReader.Create(sr, settings);
            var info = (IXmlLineInfo)reader;
            MyBatisElement? open = null;
            int bodyStart = 0;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
                {
                    if (reader.Name != "mapper")
                    {
                        throw new FormatException(
                            $"루트 요소가 mapper 가 아닙니다: <{reader.Name}> ({Position(info)}).");
                    }

                    ns = reader.GetAttribute("namespace");
                    rootTagEnd = StartTagEnd(text, lines.NameOffset(info) - 1);
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1)
                {
                    int start = lines.NameOffset(info) - 1;
                    int tagEnd = StartTagEnd(text, start);
                    var element = new MyBatisElement
                    {
                        Kind = KindOf(reader.Name),
                        Name = reader.Name,
                        Id = reader.GetAttribute("id"),
                        Attributes = ReadAttributes(reader),
                        Text = string.Empty,
                    };
                    reader.MoveToElement();
                    if (reader.IsEmptyElement)
                    {
                        elements.Add(element with
                        {
                            Text = text[start..tagEnd],
                            ElementRange = new TextRange(start, tagEnd - start),
                            BodyRange = new TextRange(tagEnd, 0),
                        });
                    }
                    else
                    {
                        open = element with { ElementRange = new TextRange(start, 0) };
                        bodyStart = tagEnd;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 1 && open is not null)
                {
                    // 닫는 태그 위치 = 이름 첫 글자 − 2(「</」).
                    int closeStart = lines.NameOffset(info) - 2;
                    int end = text.IndexOf('>', closeStart) + 1;
                    int start = open.ElementRange.Start;
                    elements.Add(open with
                    {
                        Text = text[start..end],
                        ElementRange = new TextRange(start, end - start),
                        BodyRange = new TextRange(bodyStart, closeStart - bodyStart),
                    });
                    open = null;
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0)
                {
                    rootCloseStart = lines.NameOffset(info) - 2;
                }
            }
        }
        catch (XmlException ex)
        {
            throw new FormatException(
                $"xml 형식 오류({ex.LineNumber.ToString(CultureInfo.InvariantCulture)}줄 {ex.LinePosition.ToString(CultureInfo.InvariantCulture)}칸): {ex.Message}", ex);
        }

        if (rootTagEnd < 0)
        {
            throw new FormatException("루트 요소가 없습니다.");
        }

        return new MapperScan(ns, elements, rootTagEnd, rootCloseStart);
    }

    private static string Position(IXmlLineInfo info) =>
        $"{info.LineNumber.ToString(CultureInfo.InvariantCulture)}줄 {info.LinePosition.ToString(CultureInfo.InvariantCulture)}칸";

    private static List<KeyValuePair<string, string>> ReadAttributes(XmlReader reader)
    {
        var list = new List<KeyValuePair<string, string>>(reader.AttributeCount);
        while (reader.MoveToNextAttribute())
        {
            list.Add(new KeyValuePair<string, string>(reader.Name, reader.Value));
        }

        return list;
    }

    private static MyBatisElementKind KindOf(string name) => name switch
    {
        "select" => MyBatisElementKind.Select,
        "insert" => MyBatisElementKind.Insert,
        "update" => MyBatisElementKind.Update,
        "delete" => MyBatisElementKind.Delete,
        "sql" => MyBatisElementKind.Sql,
        "resultMap" => MyBatisElementKind.ResultMap,
        _ => MyBatisElementKind.Other,
    };

    /// <summary><paramref name="lt"/>(「&lt;」)에서 시작한 여는 태그의 끝(「&gt;」 다음). 속성 값 따옴표 안의 「&gt;」 는 건너뛴다.</summary>
    internal static int StartTagEnd(string text, int lt)
    {
        char quote = '\0';
        for (int i = lt + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i + 1;
            }
        }

        throw new FormatException("여는 태그가 닫히지 않았습니다.");
    }

    /// <summary>문서가 주로 쓰는 줄 끝(「\r\n」·「\n」·홀로 「\r」 중 가장 많은 것). 줄 끝이 없으면 이 OS 의 줄 끝.</summary>
    private static string MainNewLine(string text)
    {
        int crlf = 0;
        int lf = 0;
        int cr = 0;
        for (int i = text.AsSpan().IndexOfAny('\r', '\n'); i >= 0 && i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0 && cr == 0)
        {
            return Environment.NewLine;
        }

        return crlf >= lf && crlf >= cr ? "\r\n" : lf >= cr ? "\n" : "\r";
    }

    /// <summary>
    /// 선언한 인코딩(없으면 utf-8)을 찾는다. 모르는 이름·ASCII 호환이 아닌 UTF-16·UTF-32 는 <see cref="FormatException"/>.
    /// 형식에는 별칭(<c>UTF8</c> 등) 대신 웹 이름을 남긴다.
    /// </summary>
    private static (Encoding Encoding, string WebName) ResolveEncoding(string? declared)
    {
        var name = declared ?? Utf8;
        Encoding encoding;
        string webName;
        try
        {
            encoding = MapperEncoding.Get(name, out webName);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"모르는 인코딩입니다: {name}", ex);
        }

        if (encoding.CodePage is 1200 or 1201 or 12000 or 12001)
        {
            throw new FormatException($"{webName} 매퍼는 읽지 않습니다(UTF-8·ASCII 호환 인코딩만).");
        }

        return (encoding, webName);
    }

    private static string? DeclaredEncoding(string head)
    {
        var m = DeclarationEncoding().Match(head);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex("""^<\?xml[^>]*?\sencoding\s*=\s*["']([A-Za-z0-9._-]+)["']""", RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationEncoding();

    /// <summary>XmlReader 와 같은 줄 나눔(「\r\n」·홀로 「\r」·「\n」)으로 줄 시작 오프셋을 들고, 줄·칸을 원문 오프셋으로 바꾼다.</summary>
    private sealed class LineStarts
    {
        private readonly List<int> starts;

        public LineStarts(string text)
        {
            starts = new List<int>((text.Length / 32) + 1) { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    starts.Add(i + 1);
                }
                else if (c == '\n')
                {
                    starts.Add(i + 1);
                }
            }
        }

        /// <summary>요소 노드의 줄·칸은 이름 첫 글자를 가리킨다(1부터). 그 글자의 오프셋.</summary>
        public int NameOffset(IXmlLineInfo info) => starts[info.LineNumber - 1] + info.LinePosition - 1;
    }
}

/// <summary>원문을 훑은 결과. <see cref="RootCloseStart"/> 는 「&lt;/mapper&gt;」 의 「&lt;」, 루트가 빈 요소(<c>&lt;mapper/&gt;</c>)면 -1.</summary>
internal sealed record MapperScan(string? Namespace, IReadOnlyList<MyBatisElement> Elements, int RootTagEnd, int RootCloseStart);

/// <summary>
/// 매퍼 인코딩 찾기. 잘못된 바이트·표현 못 하는 글자를 조용히 바꾸지 않도록 예외 fallback 을 건다.
/// euc-kr 선언은 CP949(코드 페이지 949)로 읽고 쓴다: 한국 Windows 에서 만든 매퍼는 EUC-KR 이라고 적고 CP949 확장 글자(똠·햏 등)를 담는 일이 많고,
/// KS X 1001 글자의 바이트는 둘이 같다. 쓰기(<c>MapperFileWriter</c>)도 같은 규칙을 쓴다.
/// </summary>
internal static class MapperEncoding
{
    private const int EucKr = 51949;
    private const int Cp949 = 949;

    /// <summary>이름으로 인코딩을 찾는다. <paramref name="webName"/> = 형식에 남길 이름(euc-kr 은 euc-kr 그대로).</summary>
    public static Encoding Get(string name, out string webName)
    {
        var encoding = Strict(name);
        webName = encoding.WebName;
        return encoding.CodePage == EucKr ? Strict(Cp949) : encoding;
    }

    private static Encoding Strict(string name)
    {
        try
        {
            return Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            // 코드 페이지 인코딩. 전역 등록(Encoding.RegisterProvider)은 하지 않는다.
            return WithExceptionFallback(CodePagesEncodingProvider.Instance.GetEncoding(name)
                ?? throw new ArgumentException($"모르는 인코딩입니다: {name}", nameof(name)));
        }
    }

    private static Encoding Strict(int codePage) =>
        WithExceptionFallback(CodePagesEncodingProvider.Instance.GetEncoding(codePage)
            ?? throw new ArgumentException($"모르는 코드 페이지입니다: {codePage}", nameof(codePage)));

    private static Encoding WithExceptionFallback(Encoding found)
    {
        var encoding = (Encoding)found.Clone();
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
        return encoding;
    }
}
