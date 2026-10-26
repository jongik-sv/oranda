using System.Globalization;
using System.Text;
using Oranda.App.Features.Binds;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.App.Features.Execution;

// ExecutionController 의 일부: 결과 반영(ExecutionController.cs 에서 나눔, 동작 불변).
public sealed partial class ExecutionController
{
    // ---- 결과 반영 ---------------------------------------------------------------------------

    /// <param name="binds">이 실행이 쓴 바인드 값. 결과 탭이 문장에 나온 이름의 값만 남겨 두었다가 서버 재조회(내보내기 등)에 그대로 쓴다.</param>
    private void Present(SqlEditorViewModel tab, ResultTabsViewModel results, SqlStatement stmt, ExecutionResult r, int ordinal,
                         IReadOnlyDictionary<string, BindValue>? binds = null, bool scriptMode = false)
    {
        var runBinds = SnapshotBinds(stmt, binds);
        var title = "결과 " + ordinal.ToString(CultureInfo.InvariantCulture);
        var text = Describe(r);
        switch (r.Status)
        {
            case ExecutionStatus.Succeeded:
                Log.AddMessage(MessageKind.Info, $"{Brief(stmt)} — {text}");

                // SQL*Plus 명령이 낸 글(PROMPT, DEFINE 목록, SPOOL 상태 등)은 메시지 창에 그대로 보인다.
                if (stmt.Kind == StatementKind.SqlPlus)
                {
                    foreach (var line in r.DbmsOutput)
                    {
                        Log.AddMessage(MessageKind.Info, line);
                    }
                }
                if (r.ResultSet is { } rs)
                {
                    results.AddQuery(rs, title, stmt, runBinds: runBinds);
                }
                else if (!scriptMode)
                {
                    results.AddMessage("메시지", text, isError: false, stmt);
                }

                foreach (var (name, value) in r.OutValues)
                {
                    if (value is IResultSet cursor)
                    {
                        results.AddQuery(cursor, ":" + name, stmt, runBinds: runBinds);
                    }
                    else
                    {
                        Log.AddMessage(MessageKind.Info, $":{name} = {value ?? "NULL"}");
                    }
                }

                break;

            case ExecutionStatus.Failed:
                State.SetError(stmt, r.Error, tab.TabId);
                Log.AddMessage(MessageKind.Error, $"{Brief(stmt)} — {text}");
                if (!scriptMode)
                {
                    results.AddMessage("오류", ErrorText(r), isError: true, stmt);
                }
                else
                {
                    results.AddMessage("오류 " + ordinal.ToString(CultureInfo.InvariantCulture), ErrorText(r), isError: true, stmt);
                }

                break;

            case ExecutionStatus.Denied:
                Log.AddMessage(MessageKind.Warning, $"{Brief(stmt)} — 거부됨: {r.Policy?.Message}");
                results.AddMessage("거부됨", r.Policy?.Message ?? "정책 관문이 거부했습니다.", isError: true, stmt);
                break;

            default:
                Log.AddMessage(MessageKind.Info, $"{Brief(stmt)} — 취소됨");
                if (!scriptMode)
                {
                    results.AddMessage("메시지", "취소되었습니다.", isError: false, stmt);
                }

                break;
        }
    }

    /// <summary>실행에 쓴 값 가운데 문장에 나온 이름의 것만(스크립트는 문장마다 이름이 다르다). 없으면 null.</summary>
    internal static IReadOnlyDictionary<string, BindValue>? SnapshotBinds(SqlStatement stmt, IReadOnlyDictionary<string, BindValue>? binds)
    {
        if (binds is not { Count: > 0 } || stmt.BindNames.Count == 0)
        {
            return null;
        }

        var snapshot = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in stmt.BindNames)
        {
            if (binds.TryGetValue(name, out var value))
            {
                snapshot[name] = value;
            }
        }

        return snapshot.Count == 0 ? null : snapshot;
    }

    private static string StatusMark(ExecutionStatus s) => s switch
    {
        ExecutionStatus.Succeeded => "[성공]",
        ExecutionStatus.Failed => "[실패]",
        ExecutionStatus.Denied => "[거부]",
        _ => "[취소]",
    };

    private static string Describe(ExecutionResult r)
    {
        var time = r.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "초";
        return r.Status switch
        {
            ExecutionStatus.Succeeded when r.RowsAffected is { } n => $"{n.ToString("N0", CultureInfo.InvariantCulture)}행 처리됨 ({time})",
            ExecutionStatus.Succeeded when r.ResultSet is not null => $"조회 완료 ({time})",
            ExecutionStatus.Succeeded => $"완료 ({time})",
            ExecutionStatus.Failed => DbErrorText.Format(r.Error),
            ExecutionStatus.Denied => "거부됨",
            _ => "취소됨",
        };
    }

    private static string ErrorText(ExecutionResult r)
    {
        var e = r.Error;
        if (e is null)
        {
            return "실행에 실패했습니다.";
        }

        var sb = new StringBuilder();
        sb.AppendLine(DbErrorText.Format(e));
        if (e.Line is { } l && e.Column is { } c)
        {
            sb.Append(CultureInfo.InvariantCulture, $"위치: 문장 안 줄 {l}, 열 {c}").AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string Brief(SqlStatement s)
    {
        var line = s.Text.TrimStart().Split('\n', 2)[0].Trim();
        return line.Length > 60 ? line[..60] + "…" : line;
    }
}
