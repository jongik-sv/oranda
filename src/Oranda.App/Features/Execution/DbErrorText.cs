using System;
using Oranda.Core.Execution;

namespace Oranda.App.Features.Execution;

/// <summary>오류를 한 줄 글로: Oracle 메시지가 이미 「ORA-00942: …」로 시작하면 번호를 다시 붙이지 않는다(「ORA-00942: ORA-00942:」 방지).</summary>
internal static class DbErrorText
{
    public static string Format(DbError? error, string fallback = "실패")
    {
        if (error is null)
        {
            return fallback;
        }

        var code = error.Number > 0 ? $"ORA-{error.Number:00000}" : null;
        return code is not null && !error.Message.StartsWith(code, StringComparison.Ordinal)
            ? $"{code}: {error.Message}"
            : error.Message;
    }
}
