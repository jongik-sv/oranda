using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Language.Lexing;

namespace Oranda.Language;

/// <summary>언어 서비스 본체(문장 분리·분류). 분석은 .Analysis.cs(TSK-04-03), 자동완성 문맥은 .Completion.cs(TSK-04-05). 소유: TSK-04-01.</summary>
[OrandaService(typeof(ILanguageService))]
internal sealed partial class LanguageService : ILanguageService
{
    public IReadOnlyList<SqlStatement> Split(string text, SplitOptions o)
        => StatementSplitter.Split(text, o);

    public SqlStatement? GetStatementAt(string text, int offset, SplitOptions o)
        => StatementSplitter.GetStatementAt(text, offset, o);
}
