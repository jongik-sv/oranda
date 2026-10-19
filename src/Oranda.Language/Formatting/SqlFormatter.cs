using Oranda.Core.Composition;
using Oranda.Core.Formatting;
using Oranda.Core.Text;

namespace Oranda.Language.Formatting;

/// <summary>포매터 엔진(대소문자·들여쓰기·주석 보존·토큰 동일성·멱등). 확장 규칙은 LayoutRules.cs(TSK-05-02). 소유: TSK-05-01. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(ISqlFormatter))]
internal sealed class SqlFormatter : ISqlFormatter
{
    public FormatResult Format(string text, FormatterOptions o, TextRange? range = null)
        => throw new NotImplementedException("TSK-05-01");
}
