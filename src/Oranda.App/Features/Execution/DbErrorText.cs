using System;
using Oranda.Core.Execution;
using Oranda.Language.Errors;

namespace Oranda.App.Features.Execution;

/// <summary>오류를 한 줄 글로: 메시지가 이미 「ORA-00942: …」(Tibero 는 「TBR-8033: …」)로 시작하면 번호를 다시 붙이지 않는다(「ORA-00942: ORA-00942:」 방지).</summary>
internal static class DbErrorText
{
    public static string Format(DbError? error, string fallback = "실패")
    {
        if (error is null)
        {
            return fallback;
        }

        var code = error.Number > 0 ? DbErrorCodes.Display(error.Dbms, error.Number) : null;
        return code is not null && !error.Message.StartsWith(code, StringComparison.Ordinal)
            ? $"{code}: {error.Message}"
            : error.Message;
    }
}
