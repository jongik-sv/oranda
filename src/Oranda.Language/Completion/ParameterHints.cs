using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Lexing;

namespace Oranda.Language.Completion;

/// <summary>커서가 든 호출(<c>pkg.proc(a, |</c>). <see cref="ArgumentIndex"/> 는 0부터.</summary>
/// <param name="Owner">이름에 스키마가 있으면 그 스키마(대문자), 없으면 null</param>
/// <param name="Package">패키지 이름(단독 프로시저면 null)</param>
/// <param name="Name">프로시저·함수 이름</param>
/// <param name="OpenParen">여는 괄호 오프셋</param>
public sealed record CallSite(string? Owner, string? Package, string Name, int ArgumentIndex, int OpenParen);

/// <summary>
/// 인자 도움말(<c>oranda.edit.parameterHints</c>). 커서를 감싼 가장 안쪽 호출을 L1 토큰으로 찾고, <see cref="IMetadataProvider"/> 로 시그니처를 읽는다.
/// </summary>
public static class ParameterHints
{
    /// <summary>커서를 감싼 호출. 호출 안이 아니면 null.</summary>
    public static CallSite? FindCallSite(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (offset < 0 || offset > text.Length)
        {
            return null;
        }

        // 문장 시작은 커서 앞 마지막 ';' 뒤. 토큰은 문장 시작~커서만 본다.
        var tokens = SqlLexer.Tokenize(text[..offset]);
        int start = 0;
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            if (tokens[i].Kind == TokenKind.Punct && text[tokens[i].Start] == ';')
            {
                start = i + 1;
                break;
            }
        }

        int depth = 0, commas = 0;
        for (int i = tokens.Count - 1; i >= start; i--)
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.Punct)
            {
                continue;
            }

            char c = text[t.Start];
            if (c == ')')
            {
                depth++;
            }
            else if (c == ',' && depth == 0)
            {
                commas++;
            }
            else if (c == '(')
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }

                var parts = ReadName(text, tokens, i - 1, start);
                if (parts.Count == 0)
                {
                    commas = 0; // 이름 없는 괄호(식 묶음): 바깥 호출을 계속 찾는다
                    continue;
                }

                return parts.Count switch
                {
                    1 => new CallSite(null, null, parts[0], commas, t.Start),
                    2 => new CallSite(null, parts[0], parts[1], commas, t.Start),
                    _ => new CallSite(parts[^3], parts[^2], parts[^1], commas, t.Start),
                };
            }
        }

        return null;
    }

    /// <summary>호출의 시그니처(오버로드 전부). 못 찾으면 빈 목록. 이름이 하나면 내 스키마의 단독 프로시저·함수로 본다.</summary>
    public static async Task<IReadOnlyList<ProcedureInfo>> GetSignaturesAsync(
        IMetadataProvider metadata, ISession session, CallSite call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(call);
        var me = (session.Server.SessionUser ?? session.Profile.User).ToUpperInvariant();
        IReadOnlyList<ProcedureInfo> found;
        if (call.Package is null)
        {
            found = await TryAsync(metadata.GetProceduresAsync(session, new DbObjectRef(me, call.Name, "PROCEDURE"), ct)).ConfigureAwait(false);
            if (found.Count == 0)
            {
                found = await TryAsync(metadata.GetProceduresAsync(session, new DbObjectRef(me, call.Name, "FUNCTION"), ct)).ConfigureAwait(false);
            }

            return found;
        }

        var owners = call.Owner is { } o ? [o] : new[] { me, "SYS" };
        foreach (var owner in owners)
        {
            found = await TryAsync(metadata.GetProceduresAsync(session, new DbObjectRef(owner, call.Package, "PACKAGE"), ct)).ConfigureAwait(false);
            var members = found.Where(p => string.Equals(p.Name, call.Name, StringComparison.Ordinal)).ToList();
            if (members.Count > 0)
            {
                return members;
            }
        }

        // pkg 가 사실 스키마였던 경우(hr.proc()).
        if (call.Owner is null)
        {
            found = await TryAsync(metadata.GetProceduresAsync(session, new DbObjectRef(call.Package, call.Name, "PROCEDURE"), ct)).ConfigureAwait(false);
        }
        else
        {
            found = [];
        }

        return found;
    }

    /// <summary>시그니처 한 줄(<c>NAME(P1 IN NUMBER, …) RETURN T</c>).</summary>
    public static string Format(ProcedureInfo procedure)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        return CompletionProvider.Signature(procedure);
    }

    private static async Task<IReadOnlyList<ProcedureInfo>> TryAsync(Task<IReadOnlyList<ProcedureInfo>> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or OperationCanceledException))
        {
            return [];
        }
    }

    /// <summary><paramref name="last"/> 에서 뒤로 <c>a.b.c</c> 이름 사슬. 대문자(따옴표 이름은 그대로).</summary>
    private static List<string> ReadName(string text, List<Token> tokens, int last, int start)
    {
        var parts = new List<string>();
        int i = last;
        while (i >= start && tokens[i].Kind is TokenKind.Identifier or TokenKind.QuotedIdent)
        {
            var t = tokens[i];
            var s = text.Substring(t.Start, t.Length);
            parts.Insert(0, t.Kind == TokenKind.QuotedIdent ? s.Trim('"') : s.ToUpperInvariant());
            if (i - 2 >= start && tokens[i - 1].Kind == TokenKind.Punct && text[tokens[i - 1].Start] == '.')
            {
                i -= 2;
                continue;
            }

            break;
        }

        return parts;
    }
}
