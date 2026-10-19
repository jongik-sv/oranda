using Oranda.Core.Composition;
using Oranda.Core.Formatting;
using Oranda.Core.Storage;

namespace Oranda.Platform.Storage;

/// <summary>포매터 프로필(기본 제공 3종 + 사용자) 저장·내보내기·가져오기. 소유: TSK-05-03. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IFormatterProfileStore))]
internal sealed class FormatterProfileStore : IFormatterProfileStore
{
    public Task<IReadOnlyList<FormatterProfile>> ListAsync(CancellationToken ct)
        => throw new NotImplementedException("TSK-05-03");

    public Task SaveAsync(FormatterProfile profile, CancellationToken ct)
        => throw new NotImplementedException("TSK-05-03");

    public Task DeleteAsync(string id, CancellationToken ct)
        => throw new NotImplementedException("TSK-05-03");

    public Task ExportAsync(string id, string path, CancellationToken ct)
        => throw new NotImplementedException("TSK-05-03");

    public Task<FormatterProfile> ImportAsync(string path, CancellationToken ct)
        => throw new NotImplementedException("TSK-05-03");
}
