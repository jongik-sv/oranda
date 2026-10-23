using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;

/// <summary>
/// <see cref="IMyBatisMapperDocument"/> 가짜(TSK-01-02, 동결). 파서가 없어도 세션 가짜가 문서를 가질 수 있게 원문·요소 목록을 그대로 든다.
/// <see cref="Apply"/> 는 원문을 바꾸지 않고, <see cref="Check"/> 는 항상 쓸 수 있다고 답한다.
/// </summary>
internal sealed class FakeMapperDocument(string text = "<mapper namespace=\"demo.UserMapper\"/>", IReadOnlyList<MyBatisElement>? elements = null) : IMyBatisMapperDocument
{
    public string Text { get; } = text;

    public MapperFileFormat Format => MapperFileFormat.Default;

    public string? MapperNamespace => "demo.UserMapper";

    public IReadOnlyList<MyBatisElement> Elements { get; } = elements ?? [];

    public string Hash => "fake-hash";

    public MyBatisElement? Find(string id) => Elements.FirstOrDefault(e => e.Id == id);

    public string Apply(IReadOnlyList<MapperEdit> edits) => Text;

    public MapperWriteCheck Check(string newText, string diskHash) => new(null, [], false);
}
