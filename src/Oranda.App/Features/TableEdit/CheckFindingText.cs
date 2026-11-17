using System.Globalization;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.TableEdit;

/// <summary>실행 전 검사 한 줄의 문구(테이블 편집기 설계 10장 표, 시안 화면 ⑤). 편집기 아래 줄·운영 확인 창·검사 창이 같이 쓴다.</summary>
public static class CheckFindingText
{
    /// <summary>판정 표시(✔ / ⚠ / ✖).</summary>
    public static string Glyph(CheckSeverity severity) => severity switch
    {
        CheckSeverity.Block => "✖",
        CheckSeverity.Warning => "⚠",
        _ => "✔",
    };

    /// <summary>한 줄 문구(앞에 판정 표시).</summary>
    /// <param name="purgesOnDrop">DROP 이 휴지통을 거치지 않는 DBMS(Tibero 기본, 설계 12장)</param>
    public static string Format(CheckFinding finding, bool purgesOnDrop = false)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.Subject is null && finding.Code is CheckCode.Irreversible && finding.Detail is { Length: > 0 } line)
        {
            return Glyph(finding.Severity) + " " + line; // 이미 문구가 된 줄(파티션 지우기·비우기 행 수)
        }

        var s = finding.Subject is { Length: > 0 } subject ? subject : TableEditText.T("테이블", "table");
        var n = finding.Count?.ToString("N0", CultureInfo.InvariantCulture) ?? "?";
        var detail = finding.Detail is { Length: > 0 } d ? d : null;
        var text = finding.Code switch
        {
            CheckCode.Passed => s + (detail is null ? string.Empty : ": " + detail),
            CheckCode.Overflow => TableEditText.T($"{s}: 새 길이를 넘는 행 {n}건 — 이대로는 실패합니다", $"{s}: {n} row(s) exceed the new length — this will fail"),
            CheckCode.IncompatibleTypeChange => TableEditText.T($"{s}: 데이터가 있어 형식을 바꿀 수 없습니다", $"{s}: the column has data, so its type cannot change")
                                                + (detail is null ? string.Empty : " (" + detail + ")"),
            CheckCode.NullRows => TableEditText.T($"{s} NOT NULL 추가: NULL 행 {n}건 — 기본값을 넣거나 먼저 채우세요", $"{s} NOT NULL: {n} NULL row(s) — fill them first"),
            CheckCode.DuplicateKeys => TableEditText.T($"{s}: 키가 겹치는 행 {n}건", $"{s}: {n} row(s) with duplicate keys"),
            CheckCode.OrphanRows => TableEditText.T($"{s}: 짝이 없는 행 {n}건", $"{s}: {n} row(s) without a parent"),
            CheckCode.CheckViolations => TableEditText.T($"{s}: 조건을 어기는 행 {n}건", $"{s}: {n} row(s) violate the condition"),
            CheckCode.ExpressionFails => TableEditText.T($"{s}: 식을 계산하지 못했습니다", $"{s}: the expression fails") + (detail is null ? string.Empty : " — " + detail),
            CheckCode.NameExists => TableEditText.T($"이름이 이미 있습니다: {s}", $"The name already exists: {s}"),
            CheckCode.LongLock => TableEditText.T(
                $"행이 많은 테이블입니다(약 {n}행). 형식 변경·NOT NULL·제약·인덱스는 오래 잠글 수 있습니다. 운영은 「의뢰용 스크립트 저장」을 권합니다",
                $"Large table (about {n} rows). Type changes, NOT NULL, constraints and indexes can lock it for long. Saving a request script is recommended on production"),
            CheckCode.Irreversible => TableEditText.T($"{s}: 지우면 되돌릴 수 없습니다", $"{s}: dropping cannot be undone")
                                      + (purgesOnDrop ? TableEditText.T("(휴지통 없이 바로 지워짐)", " (no recycle bin)") : string.Empty)
                                      + Dependents(finding),
            CheckCode.ServerName => TableEditText.T($"{s}: 서버가 지은 이름입니다 — 다른 DB 에서는 이름이 달라 실패합니다", $"{s}: server-generated name — fails on other databases"),
            CheckCode.NotChecked => TableEditText.T($"{s}: 확인 못 함", $"{s}: not checked") + (detail is null ? string.Empty : " — " + detail),
            CheckCode.VirtualRecreated => TableEditText.T($"{s}: 가상 식 변경을 열 지우고 다시 넣기로 합니다", $"{s}: the virtual expression is changed by dropping and re-adding the column"),
            CheckCode.InvalidDefinition => TableEditText.T($"{s}: 서버가 받지 않는 정의입니다", $"{s}: the server does not accept this definition")
                                           + (detail is null ? string.Empty : " (" + detail + ")"),
            CheckCode.DependentsInvalidated => TableEditText.T($"{s}: 이 테이블을 쓰는 뷰·PL/SQL·시노님이 무효가 됩니다", $"{s}: views, PL/SQL and synonyms using this table become invalid")
                                               + Dependents(finding),
            CheckCode.ColumnOrderChanges => TableEditText.T($"{s}: 숨김을 바꾸면 열 순서가 바뀝니다(맨 뒤로)", $"{s}: changing visibility moves the column to the end"),
            CheckCode.SettingIgnored => TableEditText.T($"{s}: 쓴 설정을 문장에 넣지 않습니다", $"{s}: this setting is not used") + (detail is null ? string.Empty : " (" + detail + ")"),
            CheckCode.TableRewrite => TableEditText.T(
                finding.Count is null ? "MOVE: 테이블 전체를 다시 씁니다" : $"MOVE: 테이블 전체를 다시 씁니다(약 {n}행)",
                finding.Count is null ? "MOVE: the whole table is rewritten" : $"MOVE: the whole table is rewritten (about {n} rows)"),
            CheckCode.MoveLocksDml => TableEditText.T("이 서버는 ONLINE MOVE 가 안 되어 그동안 DML 이 잠깁니다", "This server cannot MOVE ONLINE — DML is locked meanwhile"),
            CheckCode.ConversionFails => finding.Count is 0
                ? TableEditText.T($"{s}: 변환 안 되는 행 없음", $"{s}: every row converts")
                : TableEditText.T($"{s}: 변환 안 되는 행 {(detail == "atLeast" ? n + "건 이상" : n + "건")}", $"{s}: {n}{(detail == "atLeast" ? "+" : string.Empty)} row(s) do not convert")
                  + (detail is null or "atLeast" ? string.Empty : " — " + detail),
            CheckCode.ConversionNeedsQuiet => TableEditText.T($"{s}: 변환하는 동안 이 테이블에 쓰지 않게 하세요", $"{s}: keep writes off this table while converting")
                                              + (detail is null ? string.Empty : " (" + detail + ")"),
            CheckCode.Unsupported => TableEditText.T($"{s}: 이 서버에서는 할 수 없습니다", $"{s}: not supported on this server") + (detail is null ? string.Empty : " (" + UiText(detail) + ")"),
            _ => s,
        };
        return Glyph(finding.Severity) + " " + text;
    }

    /// <summary>여러 줄(✖ → ⚠ → ✔ 순서는 부르는 쪽이 정한 그대로).</summary>
    public static string FormatAll(IEnumerable<CheckFinding> findings, bool purgesOnDrop = false) =>
        string.Join(Environment.NewLine, findings.Select(f => Format(f, purgesOnDrop)));

    private static string Dependents(CheckFinding finding) => finding.Dependents.Count == 0
        ? string.Empty
        : TableEditText.T($" · 이것을 쓰는 객체 {finding.Dependents.Count}개: ", $" · {finding.Dependents.Count} dependent object(s): ")
          + string.Join(", ", finding.Dependents.Take(5).Select(d => $"{d.Owner}.{d.Name}")) + (finding.Dependents.Count > 5 ? " …" : string.Empty);

    private static string UiText(string key) => Oranda.App.Features.Localization.UiText.Get(key, key);
}

/// <summary>검사 한 줄의 화면 꼴(문구 + 「그 행 보기」 SELECT).</summary>
public sealed record FindingRow(string Text, string? ViewRowsSql)
{
    public bool HasViewRows => !string.IsNullOrWhiteSpace(ViewRowsSql);

    /// <summary>「변환해서 바꾸기…」 대상 열 줄 키(데이터가 있어 형식을 못 바꾸는 ✖, 설계 18.9). 없으면 null.</summary>
    public string? ConversionRowKey { get; init; }

    public bool HasConversion => ConversionRowKey is not null;
}
