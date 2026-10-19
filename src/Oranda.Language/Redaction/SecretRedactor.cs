using Oranda.Core.Composition;
using Oranda.Core.Security;

namespace Oranda.Language.Redaction;

/// <summary>이력·로그 비밀 가림(data-model 5.3). 소유: TSK-06-06. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(ISecretRedactor))]
internal sealed class SecretRedactor : ISecretRedactor
{
    public string Redact(string sqlText)
        => throw new NotImplementedException("TSK-06-06");

    public bool IsSensitiveBindName(string bindName)
        => throw new NotImplementedException("TSK-06-06");
}
