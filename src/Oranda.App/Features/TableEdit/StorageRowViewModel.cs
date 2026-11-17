using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.TableEdit;

/// <summary>「저장」 페이지 테이블 표의 속성(설계 18.2). 초기·다음 익스텐트는 새 테이블에서만(TE-39).</summary>
public enum StorageProperty
{
    Tablespace,
    PctFree,
    InitTrans,
    Compression,
    Logging,
    Parallel,
    InitialExtent,
    NextExtent,
}

/// <summary>
/// 「저장」 페이지 테이블 표 한 줄(설계 18.2, 시안 ⑦): 속성 / 지금 값 / 바꿀 값 / 효과. 지금 값 = 원본(모르면 「알 수 없음」), 바꿀 값 = 편집 모델.
/// 「바꿀 값」 칸은 편집 값이 비었거나(null) 지금 값과 같으면 비어 보인다 — 칸을 비우면 편집 값을 지금 값으로 되돌린다(문장 없음).
/// 칸이 보이던 글 그대로 다시 들어오면 모델을 건드리지 않는다(<see cref="TableEditRowViewModel"/> 와 같은 규칙).
/// </summary>
public sealed class StorageRowViewModel : ObservableObject
{
    private static readonly string[] RefreshedProperties =
        [nameof(CurrentText), nameof(Text), nameof(ChoiceIndex), nameof(IsChanged), nameof(EffectText), nameof(IsWarning), nameof(CanEdit), nameof(Placeholder), nameof(StateMark), nameof(Tip)];

    private string? _error;
    private IReadOnlyList<string>? _options;
    private bool _optionsForNew;

    internal StorageRowViewModel(TableEditSession session, StorageProperty attribute)
    {
        Session = session;
        Attribute = attribute;
    }

    public TableEditSession Session { get; }

    public StorageProperty Attribute { get; }

    /// <summary>속성 이름 칸.</summary>
    public string Label => Attribute switch
    {
        StorageProperty.Tablespace => TableEditText.T("테이블스페이스", "Tablespace"),
        StorageProperty.PctFree => "PCTFREE",
        StorageProperty.InitTrans => "INITRANS",
        StorageProperty.Compression => TableEditText.T("압축", "Compression"),
        StorageProperty.Logging => "LOGGING",
        StorageProperty.Parallel => TableEditText.T("병렬", "Parallel"),
        StorageProperty.InitialExtent => TableEditText.T("초기 익스텐트(INITIAL)", "Initial extent (INITIAL)"),
        _ => TableEditText.T("다음 익스텐트(NEXT)", "Next extent (NEXT)"),
    };

    /// <summary>바꿀 값이 고르는 칸(압축·LOGGING).</summary>
    public bool IsChoice => Attribute is StorageProperty.Compression or StorageProperty.Logging;

    /// <summary>바꿀 값이 글 칸(테이블스페이스·숫자·익스텐트).</summary>
    public bool IsText => !IsChoice;

    private bool IsNew => Session.IsNewTable;

    private TableStorage Now => Session.Original.Storage;

    private TableStorage Want => Session.Draft.Storage;

    /// <summary>칸을 고칠 수 있다(특수 테이블·방언이 저장 속성을 못 하면 흐림).</summary>
    public bool CanEdit => Session.CanEditStorageAttributes;

    /// <summary>지금 값 칸(새 테이블은 빈 글, 모르는 값은 「알 수 없음」).</summary>
    public string CurrentText
    {
        get
        {
            if (IsNew)
            {
                return string.Empty;
            }

            var text = Attribute switch
            {
                StorageProperty.Tablespace => Session.Original.Tablespace is { Length: > 0 } ts ? TableEditCellText.Show(ts) : null,
                StorageProperty.PctFree => Number(Now.PctFree),
                StorageProperty.InitTrans => Number(Now.InitTrans),
                StorageProperty.Compression => Now.Compression is { } c ? CompressionName(c) : null,
                StorageProperty.Logging => Now.Logging is { } l ? YesNo(l) : null,
                StorageProperty.Parallel => ParallelText(Now.Parallel),
                StorageProperty.InitialExtent => Now.InitialExtent,
                _ => Now.NextExtent,
            };
            return text ?? TableEditSession.UnknownText;
        }
    }

    /// <summary>빈 「바꿀 값」 칸의 흐린 글: 기존 테이블 = 그대로, 새 테이블 = 서버 기본.</summary>
    public string Placeholder => IsNew ? TableEditText.T("(기본)", "(default)") : TableEditText.T("(그대로)", "(keep)");

    /// <summary>바꿀 값 글 칸. 편집 값이 없거나 지금 값과 같으면 빈 글.</summary>
    public string Text
    {
        get => Attribute switch
        {
            StorageProperty.Tablespace => IsTablespaceSet ? Session.Draft.Tablespace!.Trim() : string.Empty,
            StorageProperty.PctFree => Want.PctFree != Now.PctFree ? Number(Want.PctFree) ?? string.Empty : string.Empty,
            StorageProperty.InitTrans => Want.InitTrans != Now.InitTrans ? Number(Want.InitTrans) ?? string.Empty : string.Empty,
            StorageProperty.Parallel => Want.Parallel != Now.Parallel ? ParallelText(Want.Parallel) ?? string.Empty : string.Empty,
            StorageProperty.InitialExtent => ExtentShown(Now.InitialExtent, Want.InitialExtent),
            StorageProperty.NextExtent => ExtentShown(Now.NextExtent, Want.NextExtent),
            _ => string.Empty,
        };
        set
        {
            if (!IsText || !CanEdit || string.Equals(value ?? string.Empty, Text, StringComparison.Ordinal))
            {
                Error = null;
                OnPropertyChanged();
                return;
            }

            var t = (value ?? string.Empty).Trim();
            switch (Attribute)
            {
                case StorageProperty.Tablespace:
                    Error = null;
                    var original = Session.Original.Tablespace;
                    var keep = t.Length == 0 || (!IsNew && string.Equals(TableEditNames.FromInput(t), original, StringComparison.Ordinal));
                    Session.Update(d => d with { Tablespace = keep ? original : t });
                    break;
                case StorageProperty.InitialExtent or StorageProperty.NextExtent:
                    Error = null;
                    var extent = t.Length == 0 ? (Attribute == StorageProperty.InitialExtent ? Now.InitialExtent : Now.NextExtent) : t;
                    Session.Update(d => d with
                    {
                        Storage = Attribute == StorageProperty.InitialExtent ? d.Storage with { InitialExtent = extent } : d.Storage with { NextExtent = extent },
                    });
                    break;
                default:
                    if (!TryParseCount(t, Attribute == StorageProperty.Parallel, out var n))
                    {
                        Error = Attribute == StorageProperty.Parallel
                            ? TableEditText.T("0 이상 정수 또는 DEFAULT 를 쓰세요", "Enter a whole number (0 or more) or DEFAULT")
                            : TableEditText.T("0 이상 정수를 쓰세요", "Enter a whole number (0 or more)");
                        break;
                    }

                    Error = null;
                    Session.Update(d => d with { Storage = WithNumber(d.Storage, n) });
                    break;
            }

            OnPropertyChanged(); // 모델이 그대로여도(지금 값과 같은 글·틀린 글) 칸을 모델 값으로 다시 보인다
        }
    }

    /// <summary>고르는 칸 목록. 첫 줄 = 그대로(새 테이블은 기본).</summary>
    public IReadOnlyList<string> Options
    {
        get
        {
            if (_options is null || _optionsForNew != IsNew)
            {
                _optionsForNew = IsNew;
                var keep = IsNew ? TableEditText.T("(기본)", "(default)") : TableEditText.T("(그대로)", "(keep)");
                _options = Attribute switch
                {
                    StorageProperty.Compression => [keep, CompressionName(TableCompression.None), "BASIC", "ADVANCED"],
                    StorageProperty.Logging => [keep, YesNo(true), YesNo(false)],
                    _ => [],
                };
            }

            return _options;
        }
    }

    /// <summary>고르는 칸 번호(0 = 그대로). 압축: 1 없음 · 2 BASIC · 3 ADVANCED. LOGGING: 1 예 · 2 아니오.</summary>
    public int ChoiceIndex
    {
        get => Attribute switch
        {
            StorageProperty.Compression => Want.Compression is { } c && c != Now.Compression
                ? c switch { TableCompression.None => 1, TableCompression.Basic => 2, TableCompression.Advanced => 3, _ => 0 }
                : 0,
            StorageProperty.Logging => Want.Logging is { } l && l != Now.Logging ? (l ? 1 : 2) : 0,
            _ => 0,
        };
        set
        {
            if (!IsChoice || !CanEdit || value < 0 || value >= Options.Count || value == ChoiceIndex)
            {
                return;
            }

            if (Attribute == StorageProperty.Compression)
            {
                TableCompression? target = value switch { 1 => TableCompression.None, 2 => TableCompression.Basic, 3 => TableCompression.Advanced, _ => null };
                var feature = target switch
                {
                    TableCompression.Basic => TableEditFeature.BasicTableCompression,
                    TableCompression.Advanced => TableEditFeature.AdvancedRowCompression,
                    _ => (TableEditFeature?)null,
                };
                if (feature is { } f && Session.StorageReason(f) is { } reason)
                {
                    // 방언·서버가 못 하는 압축은 모델에 넣지 않고 이유를 보인다(설계 18.8). 칸은 모델 값으로 되돌린다.
                    Error = reason;
                    RevertChoice();
                    return;
                }

                Error = null;
                Session.Update(d => d with { Storage = d.Storage with { Compression = target ?? Now.Compression } });
            }
            else
            {
                Error = null;
                bool? target = value switch { 1 => true, 2 => false, _ => null };
                Session.Update(d => d with { Storage = d.Storage with { Logging = target ?? Now.Logging } });
            }

            OnPropertyChanged();
        }
    }

    /// <summary>바뀐 칸(<c>.changed</c>): 기존 테이블 = 차이에 있는 속성, 새 테이블 = 값을 쓴 칸.</summary>
    public bool IsChanged
    {
        get
        {
            if (IsNew)
            {
                return Attribute switch
                {
                    StorageProperty.Tablespace => !string.IsNullOrWhiteSpace(Session.Draft.Tablespace),
                    StorageProperty.PctFree => Want.PctFree is not null,
                    StorageProperty.InitTrans => Want.InitTrans is not null,
                    StorageProperty.Compression => Want.Compression is not null,
                    StorageProperty.Logging => Want.Logging is not null,
                    StorageProperty.Parallel => Want.Parallel is not null,
                    StorageProperty.InitialExtent => !string.IsNullOrWhiteSpace(Want.InitialExtent),
                    _ => !string.IsNullOrWhiteSpace(Want.NextExtent),
                };
            }

            return Session.Changes.Storage.HasFlag(Flag);
        }
    }

    /// <summary>
    /// 효과 칸: 테이블스페이스·압축 = 「MOVE 필요」, MOVE 없는 PCTFREE·INITRANS = 「새 블록부터」(MOVE 와 함께면 모든 블록), 입력이 들어가지 못했으면 그 이유.
    /// </summary>
    public string EffectText
    {
        get
        {
            if (Error is { } error)
            {
                return error;
            }

            if (IsNew || !IsChanged)
            {
                return string.Empty;
            }

            return Attribute switch
            {
                StorageProperty.Tablespace or StorageProperty.Compression => TableEditText.T("MOVE 필요", "MOVE required"),
                StorageProperty.PctFree or StorageProperty.InitTrans => Session.HasStorageMove
                    ? TableEditText.T("MOVE 와 함께(모든 블록)", "With the MOVE (all blocks)")
                    : TableEditText.T("새 블록부터", "New blocks only"),
                _ => string.Empty,
            };
        }
    }

    /// <summary>효과 칸을 경고 색으로(MOVE·오류).</summary>
    public bool IsWarning => HasError || (!IsNew && IsChanged && Attribute is StorageProperty.Tablespace or StorageProperty.Compression);

    /// <summary>마지막 입력이 모델에 들어가지 못한 이유(틀린 숫자·방언이 못 하는 압축). 없으면 null.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(EffectText));
                OnPropertyChanged(nameof(IsWarning));
                OnPropertyChanged(nameof(StateMark));
                OnPropertyChanged(nameof(Tip));
            }
        }
    }

    public bool HasError => Error is not null;

    /// <summary>줄 머리 표시: ⚠ 오류 · ✎ 바뀜.</summary>
    public string StateMark => HasError ? "⚠" : IsChanged ? "✎" : string.Empty;

    /// <summary>줄 풍선 도움말: 오류, 아니면 이 서버에서 못 고르는 압축의 이유.</summary>
    public string? Tip
    {
        get
        {
            if (Error is { } error)
            {
                return error;
            }

            if (Attribute != StorageProperty.Compression)
            {
                return null;
            }

            var reasons = new[] { TableEditFeature.BasicTableCompression, TableEditFeature.AdvancedRowCompression }
                .Select(f => Session.StorageReason(f) is { } r ? (f == TableEditFeature.BasicTableCompression ? "BASIC: " : "ADVANCED: ") + r : null)
                .OfType<string>().ToList();
            return reasons.Count == 0 ? null : string.Join(Environment.NewLine, reasons);
        }
    }

    internal void ClearError() => Error = null;

    internal void Refresh()
    {
        foreach (var name in RefreshedProperties)
        {
            OnPropertyChanged(name);
        }
    }

    private StorageChange Flag => Attribute switch
    {
        StorageProperty.Tablespace => StorageChange.Tablespace,
        StorageProperty.PctFree => StorageChange.PctFree,
        StorageProperty.InitTrans => StorageChange.InitTrans,
        StorageProperty.Compression => StorageChange.Compression,
        StorageProperty.Logging => StorageChange.Logging,
        StorageProperty.Parallel => StorageChange.Parallel,
        _ => StorageChange.Extents,
    };

    /// <summary>테이블스페이스 바꿀 값이 있다(비지 않고, 기존 테이블은 지금 값과 다름 — Core 차이와 같은 비교).</summary>
    private bool IsTablespaceSet =>
        Session.Draft.Tablespace is { } ts && !string.IsNullOrWhiteSpace(ts)
        && (IsNew || !string.Equals(TableEditNames.FromInput(ts), Session.Original.Tablespace, StringComparison.Ordinal));

    private TableStorage WithNumber(TableStorage s, int? typed) => Attribute switch
    {
        StorageProperty.PctFree => s with { PctFree = typed ?? Now.PctFree },
        StorageProperty.InitTrans => s with { InitTrans = typed ?? Now.InitTrans },
        _ => s with { Parallel = typed ?? Now.Parallel },
    };

    /// <summary>칸이 모델 값으로 돌아가게 알린다. 바인딩이 값을 쓰는 중에는 알림을 놓칠 수 있어 UI 스레드면 한 번 더 미뤄 알린다.</summary>
    private void RevertChoice()
    {
        OnPropertyChanged(nameof(ChoiceIndex));
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ChoiceIndex)));
        }
    }

    private static string ExtentShown(string? now, string? want) =>
        string.IsNullOrWhiteSpace(want) || string.Equals(now?.Trim(), want.Trim(), StringComparison.OrdinalIgnoreCase) ? string.Empty : want.Trim();

    /// <summary>숫자 칸 글 → 값(빈 칸 = null = 그대로). 0 이상 정수, 병렬은 <c>DEFAULT</c>(= 0)도.</summary>
    private static bool TryParseCount(string text, bool allowDefault, out int? value)
    {
        value = null;
        if (text.Length == 0)
        {
            return true;
        }

        if (allowDefault && string.Equals(text, "DEFAULT", StringComparison.OrdinalIgnoreCase))
        {
            value = 0;
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0)
        {
            value = n;
            return true;
        }

        return false;
    }

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>병렬: 0 = DEFAULT(서버 기본), 그 밖 = 수.</summary>
    private static string? ParallelText(int? value) => value switch
    {
        null => null,
        0 => "DEFAULT",
        { } n => n.ToString(CultureInfo.InvariantCulture),
    };

    internal static string YesNo(bool value) => value ? TableEditText.T("예", "Yes") : TableEditText.T("아니오", "No");

    private static string CompressionName(TableCompression compression) => compression switch
    {
        TableCompression.None => TableEditText.T("없음", "None"),
        TableCompression.Basic => "BASIC",
        TableCompression.Advanced => "ADVANCED",
        _ => TableEditText.T("그 밖(바꿀 수 없음)", "Other (read only)"),
    };
}

/// <summary>
/// 「저장」 페이지 LOB 표 한 줄(설계 18.2): 열 / 방식 / 테이블스페이스 / 압축 / 중복 제거 / 행 안에. 고르는 칸 첫 줄 = 그대로(지금 값을 함께 보임).
/// 편집은 <see cref="ColumnDraft.Lob"/> 에 들어가고, 고르지 않은 칸은 지금 값 그대로 둔다(null = 그대로와 같은 규칙).
/// </summary>
public sealed class LobRowViewModel : ObservableObject
{
    private static readonly string[] RefreshedProperties =
    [
        nameof(ColumnName), nameof(Tablespace), nameof(TablespacePlaceholder), nameof(KindIndex), nameof(CompressionIndex), nameof(DeduplicateIndex), nameof(InRowIndex),
        nameof(IsKindChanged), nameof(IsTablespaceChanged), nameof(IsCompressionChanged), nameof(IsDeduplicateChanged), nameof(IsInRowChanged),
        nameof(CanEditKind), nameof(CanEditTablespace), nameof(CanEditCompression), nameof(CanEditDeduplicate), nameof(CanEditInRow),
        nameof(KindTip), nameof(CompressionTip), nameof(KindOptions), nameof(CompressionOptions), nameof(DeduplicateOptions), nameof(InRowOptions),
    ];

    private LobStorage? _optionsFor;
    private bool _optionsForNew;
    private bool _optionsBuilt;
    private IReadOnlyList<string> _kindOptions = [];
    private IReadOnlyList<string> _compressionOptions = [];
    private IReadOnlyList<string> _deduplicateOptions = [];
    private IReadOnlyList<string> _inRowOptions = [];

    internal LobRowViewModel(TableEditSession session, string key)
    {
        Session = session;
        Key = key;
    }

    public TableEditSession Session { get; }

    /// <summary>열 줄 키.</summary>
    public string Key { get; }

    public string ColumnName => Session.Draft.Column(Key)?.Name ?? string.Empty;

    /// <summary>원본에 없는 열(새 테이블·새 열): 지금 값이 없다.</summary>
    private bool IsNewColumn => Session.Original.Column(Key) is null;

    private LobStorage? Now => Session.Original.Column(Key)?.Lob;

    private LobStorage Want => Session.Draft.Column(Key)?.Lob ?? new LobStorage();

    private string? SecureFileReason => Session.StorageReason(TableEditFeature.SecureFileLob);

    public bool CanEditTablespace => Session.CanEditLobStorage;

    public bool CanEditInRow => Session.CanEditLobStorage;

    /// <summary>방식 칸: 방식 낱말을 못 쓰는 방언(Tibero)은 흐림(설계 18.2).</summary>
    public bool CanEditKind => Session.CanEditLobStorage && SecureFileReason is null;

    /// <summary>압축·중복 제거는 SECUREFILE 전용 옵션 — 방식 낱말을 못 쓰면 Core 가 막으므로 흐림.</summary>
    public bool CanEditCompression => CanEditKind;

    public bool CanEditDeduplicate => CanEditKind;

    /// <summary>방식 칸 흐림 이유.</summary>
    public string? KindTip => SecureFileReason;

    public string? CompressionTip => SecureFileReason;

    public IReadOnlyList<string> KindOptions => BuildOptions()._kindOptions;

    public IReadOnlyList<string> CompressionOptions => BuildOptions()._compressionOptions;

    public IReadOnlyList<string> DeduplicateOptions => BuildOptions()._deduplicateOptions;

    public IReadOnlyList<string> InRowOptions => BuildOptions()._inRowOptions;

    /// <summary>테이블스페이스 칸 흐린 글 = 지금 값(새 열은 기본).</summary>
    public string TablespacePlaceholder => IsNewColumn ? TableEditText.T("(기본)", "(default)") : Now?.Tablespace ?? TableEditSession.UnknownText;

    /// <summary>테이블스페이스 바꿀 값(지금과 같으면 빈 글).</summary>
    public string Tablespace
    {
        get => IsTablespaceChanged ? Want.Tablespace!.Trim() : string.Empty;
        set
        {
            if (!CanEditTablespace || string.Equals(value ?? string.Empty, Tablespace, StringComparison.Ordinal))
            {
                OnPropertyChanged();
                return;
            }

            var t = (value ?? string.Empty).Trim();
            var now = Now?.Tablespace;
            var keep = t.Length == 0 || string.Equals(TableEditNames.FromInput(t), now, StringComparison.Ordinal);
            Session.UpdateLob(Key, l => l with { Tablespace = keep ? now : t });
            OnPropertyChanged();
        }
    }

    /// <summary>방식: 0 그대로 · 1 SECUREFILE · 2 BASICFILE.</summary>
    public int KindIndex
    {
        get => Want.Kind is { } k && k != Now?.Kind ? (k == LobStorageKind.SecureFile ? 1 : 2) : 0;
        set
        {
            if (!CanEditKind || value is < 0 or > 2 || value == KindIndex)
            {
                return;
            }

            LobStorageKind? kind = value switch { 1 => LobStorageKind.SecureFile, 2 => LobStorageKind.BasicFile, _ => null };
            Session.UpdateLob(Key, l => l with { Kind = kind ?? Now?.Kind });
            OnPropertyChanged();
        }
    }

    /// <summary>압축: 0 그대로 · 1 없음 · 2 LOW · 3 MEDIUM · 4 HIGH.</summary>
    public int CompressionIndex
    {
        get => Want.Compression is { } c && c != Now?.Compression ? (int)c + 1 : 0;
        set
        {
            if (!CanEditCompression || value is < 0 or > 4 || value == CompressionIndex)
            {
                return;
            }

            LobCompression? compression = value == 0 ? null : (LobCompression)(value - 1);
            Session.UpdateLob(Key, l => l with { Compression = compression ?? Now?.Compression });
            OnPropertyChanged();
        }
    }

    /// <summary>중복 제거: 0 그대로 · 1 예 · 2 아니오.</summary>
    public int DeduplicateIndex
    {
        get => YesNoIndex(Want.Deduplicate, Now?.Deduplicate);
        set
        {
            if (!CanEditDeduplicate || value is < 0 or > 2 || value == DeduplicateIndex)
            {
                return;
            }

            Session.UpdateLob(Key, l => l with { Deduplicate = FromYesNoIndex(value) ?? Now?.Deduplicate });
            OnPropertyChanged();
        }
    }

    /// <summary>행 안에 저장: 0 그대로 · 1 예 · 2 아니오.</summary>
    public int InRowIndex
    {
        get => YesNoIndex(Want.InRow, Now?.InRow);
        set
        {
            if (!CanEditInRow || value is < 0 or > 2 || value == InRowIndex)
            {
                return;
            }

            Session.UpdateLob(Key, l => l with { InRow = FromYesNoIndex(value) ?? Now?.InRow });
            OnPropertyChanged();
        }
    }

    public bool IsKindChanged => KindIndex != 0;

    public bool IsTablespaceChanged =>
        Want.Tablespace is { } ts && !string.IsNullOrWhiteSpace(ts) && !string.Equals(TableEditNames.FromInput(ts), Now?.Tablespace, StringComparison.Ordinal);

    public bool IsCompressionChanged => CompressionIndex != 0;

    public bool IsDeduplicateChanged => DeduplicateIndex != 0;

    public bool IsInRowChanged => InRowIndex != 0;

    internal void Refresh()
    {
        foreach (var name in RefreshedProperties)
        {
            OnPropertyChanged(name);
        }
    }

    private static int YesNoIndex(bool? want, bool? now) => want is { } w && w != now ? (w ? 1 : 2) : 0;

    private static bool? FromYesNoIndex(int index) => index switch { 1 => true, 2 => false, _ => null };

    /// <summary>고르는 칸 목록(첫 줄 = 그대로 · 지금 값). 원본이 바뀔 때만 새로 만든다 — 목록 객체가 바뀌면 칸 고름이 풀린다.</summary>
    private LobRowViewModel BuildOptions()
    {
        var now = Now;
        if (_optionsBuilt && ReferenceEquals(now, _optionsFor) && _optionsForNew == IsNewColumn)
        {
            return this;
        }

        _optionsBuilt = true;
        _optionsFor = now;
        _optionsForNew = IsNewColumn;
        string Keep(string? current) => IsNewColumn
            ? TableEditText.T("(기본)", "(default)")
            : TableEditText.T("그대로 · ", "Keep · ") + (current ?? TableEditSession.UnknownText);
        string? YesNoText(bool? v) => v is { } b ? StorageRowViewModel.YesNo(b) : null;

        var none = TableEditText.T("없음", "None");
        _kindOptions = [Keep(now?.Kind switch { LobStorageKind.SecureFile => "SECUREFILE", LobStorageKind.BasicFile => "BASICFILE", _ => null }), "SECUREFILE", "BASICFILE"];
        _compressionOptions = [Keep(now?.Compression is { } c ? c == LobCompression.None ? none : c.ToString().ToUpperInvariant() : null), none, "LOW", "MEDIUM", "HIGH"];
        _deduplicateOptions = [Keep(YesNoText(now?.Deduplicate)), StorageRowViewModel.YesNo(true), StorageRowViewModel.YesNo(false)];
        _inRowOptions = [Keep(YesNoText(now?.InRow)), StorageRowViewModel.YesNo(true), StorageRowViewModel.YesNo(false)];
        return this;
    }
}
