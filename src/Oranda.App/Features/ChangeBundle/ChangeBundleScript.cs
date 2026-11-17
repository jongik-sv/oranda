using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Execution;
using Oranda.Core.Language;

namespace Oranda.App.Features.ChangeBundle;

/// <summary>묶음 문장 하나를 실행·저장할 꼴로 바꾼 것. <see cref="Statement"/> 의 글은 소유자 바꾸기를 거친 글이다.</summary>
public sealed record ChangeBundlePrepared(ChangeBundleItem Item, SqlStatement Statement, bool OwnerUncertain);

/// <summary>
/// 묶음 「실행…」·「의뢰용 스크립트 저장…」 이 같이 쓰는 문장 만들기(설계 9.2·9.4): 체크한 문장을 화면 순서대로, 소유자 바꾸기(TE-22)를 거쳐
/// 문장 분류(<see cref="ILanguageService.Split"/>)까지. 묶음 문장 하나 = 실행 문장 하나 — 나눠지면 분류 불확실한 문장 하나로 본다.
/// </summary>
internal static class ChangeBundleScript
{
    public static IReadOnlyList<ChangeBundlePrepared> Prepare(IEnumerable<ChangeBundleItem> items, IReadOnlyDictionary<string, string> ownerMap,
        IOwnerRewriter? rewriter, ILanguageService? language)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(ownerMap);
        var list = new List<ChangeBundlePrepared>();
        foreach (var item in items)
        {
            var text = item.Text;
            var uncertain = false;
            if (ownerMap.Count > 0 && rewriter is not null)
            {
                var r = rewriter.Rewrite(text, ownerMap);
                text = r.Text;
                uncertain = r.Uncertain;
            }

            list.Add(new ChangeBundlePrepared(item, ToStatement(text, language), uncertain));
        }

        return list;
    }

    /// <summary>
    /// 글 하나를 실행 문장 하나로. 분류기가 하나로 읽으면 그 결과(글도 분류기 것 — 끝 <c>;</c>·앞뒤 주석은 분리기 규칙대로, PL/SQL 단위는 <c>END;</c> 까지).
    /// 하나로 읽지 못하면 원문 그대로 <see cref="StatementKind.Other"/>·분류 불확실(정책 관문은 가장 엄격하게 보고, 의뢰 스크립트 저장은 막는다).
    /// 분류기가 없으면(시험·조립 전) 원문 그대로 DDL.
    /// </summary>
    internal static SqlStatement ToStatement(string text, ILanguageService? language)
    {
        var trimmed = text.Trim();
        var verb = trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant();
        if (language is null)
        {
            return new SqlStatement(trimmed, default, StatementKind.Ddl) { Verb = verb };
        }

        if (SplitOne(trimmed, language) is { } one)
        {
            return one;
        }

        return new SqlStatement(trimmed, default, StatementKind.Other) { Verb = verb, IsClassificationUncertain = true };
    }

    /// <summary>
    /// 묶음 창에서 고친 글을 저장할 꼴로(한 문장, 분리기 글). 문장이 하나가 아니면 null(받지 않는다). 분류기가 없으면 앞뒤 공백과
    /// 끝 <c>;</c> 만 뗀다(PL/SQL 단위는 끝 <c>END;</c> 를 남긴다).
    /// </summary>
    internal static string? NormalizeEdited(string text, ILanguageService? language)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (language is null)
        {
            return trimmed;
        }

        return SplitOne(trimmed, language)?.Text.Trim() is { Length: > 0 } one ? one : null;
    }

    private static SqlStatement? SplitOne(string text, ILanguageService language)
    {
        try
        {
            var split = language.Split(text, SplitOptions.Default);
            return split.Count == 1 ? split[0] with { Range = default } : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>소유자를 붙이지 못한 문장(<see cref="ChangeBundleCautions.OwnerNotQualified"/>) 수 — 소유자 바꾸기가 닿지 않는다.</summary>
    internal static int UnqualifiedCount(IEnumerable<ChangeBundleItem> items) =>
        items.Count(i => i.Cautions.HasFlag(ChangeBundleCautions.OwnerNotQualified));

    /// <summary>비밀을 가린 문장(<see cref="ChangeBundleCautions.SecretRedacted"/>) 수.</summary>
    internal static int RedactedCount(IEnumerable<ChangeBundleItem> items) =>
        items.Count(i => i.Cautions.HasFlag(ChangeBundleCautions.SecretRedacted));
}
