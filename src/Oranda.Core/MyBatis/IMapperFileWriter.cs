namespace Oranda.Core.MyBatis;

/// <summary>
/// 매퍼 파일의 바이트 모양(MB-01): 인코딩·BOM·줄 끝. 파서가 연 때 읽고, 쓰기가 그대로 되돌린다.
/// <see cref="NewLine"/> 은 문서가 주로 쓰는 줄 끝(<c>"\r\n"</c> 또는 <c>"\n"</c>)이고 새 문장·새 파일에 쓴다.
/// </summary>
/// <param name="EncodingName">인코딩 웹 이름(<c>utf-8</c>, <c>euc-kr</c> …). xml 선언이 없으면 <c>utf-8</c>.</param>
/// <param name="HasBom">파일 앞에 BOM 이 있었다.</param>
/// <param name="NewLine">문서의 줄 끝.</param>
/// <param name="EndsWithNewLine">파일이 줄 끝으로 끝났다.</param>
public sealed record MapperFileFormat(string EncodingName, bool HasBom, string NewLine, bool EndsWithNewLine)
{
    /// <summary>새 매퍼 파일의 기본 모양: UTF-8, BOM 없음, 이 OS 의 줄 끝, 끝 줄바꿈 있음.</summary>
    public static MapperFileFormat Default { get; } = new("utf-8", false, Environment.NewLine, true);
}

/// <summary>
/// 매퍼 원자적 쓰기(design.md 5.2, TRD 5). 계약(TSK-01-02) — 구현은 TSK-02-01(<c>src/Oranda.Platform/MyBatis/</c>),
/// 쓰는 쪽은 TSK-03-03(저장)·TSK-03-05(바꾸기).
/// 같은 폴더의 임시 파일에 쓰고 바꾼다(대상이 있으면 <c>File.Replace</c>, 없으면 <c>File.Move</c>). 실패하면 임시 파일을 지우고 원본을 그대로 둔다.
/// 글자는 이미 완성된 새 원문이다(파일 전체를 다시 직렬화하지 않는다). <paramref name="format"/> 의 인코딩·BOM 으로 바이트를 만든다.
/// </summary>
public interface IMapperFileWriter
{
    /// <summary>
    /// <paramref name="text"/> 를 <paramref name="path"/> 에 원자적으로 쓴다. 읽기 전용·권한·디스크 오류는
    /// <see cref="IOException"/>·<see cref="UnauthorizedAccessException"/> 그대로 던진다(부른 쪽이 알리고 고침을 남긴다).
    /// </summary>
    Task WriteAsync(string path, string text, MapperFileFormat format, CancellationToken ct);
}
