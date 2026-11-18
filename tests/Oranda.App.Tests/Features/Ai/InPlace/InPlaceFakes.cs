using Oranda.App.Features.Ai.InPlace;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Settings;
using Oranda.Language.Ai.Literals;

namespace Oranda.App.Tests.Features.Ai.InPlace;

/// <summary>바로 처리 화면 시험의 가짜 연결: 가짜 드라이버(실행 범위 필수)·설정·가림기, 경고 창은 기록만 하고 「보내기」.</summary>
internal static class InPlaceFakes
{
    internal static AiInPlaceHost Host(FakeDriver driver, AiSettings? settings = null, List<string>? confirmed = null, bool confirm = true) => new(null)
    {
        Settings = () => settings ?? new AiSettings { Model = "haiku" },
        Driver = () => driver,
        Masker = new SqlLiteralMasker(),
        Confirm = (request, _) =>
        {
            confirmed?.Add(AiInPlaceHost.ConfirmText(request));
            return Task.FromResult(confirm);
        },
    };
}

/// <summary>편집기 제안을 기록하는 가짜(받아들임 여부는 <see cref="Accept"/>).</summary>
internal sealed class ProposalEditor(Func<string> text) : IEditorContext
{
    public List<TextEdit> Proposals { get; } = [];

    public bool Accept { get; set; } = true;

    public EditorSnapshot? GetActiveDocument() => new(Guid.NewGuid(), text(), new Oranda.Core.Text.TextRange(0, 0), 0);

    public SqlStatement? GetCurrentStatement() => null;

    public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct)
    {
        Proposals.Add(edit);
        return Task.FromResult(Accept);
    }
}
