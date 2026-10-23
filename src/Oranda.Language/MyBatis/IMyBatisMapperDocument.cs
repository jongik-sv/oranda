using Oranda.Core.MyBatis;

namespace Oranda.Language.MyBatis;

/// <summary>
/// 매퍼 원문 모델(MB-01, TRD 3.1·4). 계약(TSK-01-02) — 구현은 TSK-02-01.
/// 원문을 그대로 들고 요소마다 범위를 가진다. 주석·CDATA·엔티티·공백·인코딩·줄 끝은 원문 그대로다.
/// 편집은 범위 바꾸기를 뒤에서부터 적용해 새 원문을 만든다(XDocument 로 다시 직렬화하지 않는다). 바꾸지 않은 부분은 바이트까지 같다.
/// 한 번 만든 문서는 바뀌지 않는다: <see cref="Apply"/> 는 새 원문을 돌려줄 뿐이고, 새 문서가 필요하면 그 원문을 다시 Parse 한다.
/// </summary>
public interface IMyBatisMapperDocument
{
    /// <summary>매퍼 원문(BOM 없는 글자).</summary>
    string Text { get; }

    /// <summary>연 때의 바이트 모양(인코딩·BOM·줄 끝·끝 줄바꿈).</summary>
    MapperFileFormat Format { get; }

    /// <summary>루트 <c>mapper</c> 의 namespace. 없으면 null.</summary>
    string? MapperNamespace { get; }

    /// <summary>최상위 요소(원문 순서).</summary>
    IReadOnlyList<MyBatisElement> Elements { get; }

    /// <summary>
    /// 원문 해시 = <see cref="MapperHash.Compute"/>(<see cref="Text"/>). 디스크 해시도 같은 함수로(파일 바이트를 <see cref="Format"/> 으로 글자로 바꾼 뒤) 만든다.
    /// BOM·인코딩 차이로 「밖에서 바뀜」이 잘못 나지 않게 바이트가 아니라 글자를 해시한다.
    /// </summary>
    string Hash { get; }

    /// <summary>id 로 최상위 요소를 찾는다. 없으면 null.</summary>
    MyBatisElement? Find(string id);

    /// <summary>편집 목록을 적용한 새 원문. 없는 id·겹치는 범위는 <see cref="ArgumentException"/>.</summary>
    string Apply(IReadOnlyList<MapperEdit> edits);

    /// <summary>쓰기 전 검사: <paramref name="newText"/> 가 잘 짜인 매퍼인지, 문장 id 가 겹치는지, 연 때 해시와 <paramref name="diskHash"/> 가 다른지.</summary>
    MapperWriteCheck Check(string newText, string diskHash);
}

/// <summary>
/// 매퍼 읽기 진입점(TRD 4 의 Parse). 계약(TSK-01-02) — 구현은 TSK-02-01.
/// DTD·외부 엔티티를 읽지 않는다(<c>DtdProcessing.Ignore</c>, <c>XmlResolver = null</c>).
/// 깨진 xml·루트가 <c>mapper</c> 가 아닌 파일은 줄·칸을 담은 <see cref="FormatException"/>.
/// </summary>
public interface IMyBatisMapperParser
{
    /// <summary>글자로 읽는다. <see cref="IMyBatisMapperDocument.Format"/> 은 xml 선언·줄 끝에서 정하고 BOM 은 없음으로 본다.</summary>
    IMyBatisMapperDocument Parse(string text);

    /// <summary>파일 바이트로 읽는다. BOM·xml 선언의 인코딩을 <see cref="IMyBatisMapperDocument.Format"/> 에 남긴다.</summary>
    IMyBatisMapperDocument Parse(ReadOnlySpan<byte> bytes);
}
