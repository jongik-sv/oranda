using System.Collections.ObjectModel;
using Oranda.App.Features.Localization;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 「저장」 페이지 화면 상태(설계 18.2, 시안 ⑦): 테이블 저장 속성 표(<see cref="StorageRows"/>), LOB 표(<see cref="LobRows"/>, LOB 열이 없으면 빔),
/// 아래 ⚠ MOVE 줄(<see cref="StorageMoveText"/>). 편집은 <see cref="Update"/>·<see cref="UpdateColumn"/> 로 편집 모델만 바꾸고 문장은 Core 가 만든다.
/// 줄 객체는 처음 쓸 때 만들고 <see cref="Recomputed"/> 마다 다시 맞춘다(같은 객체 유지).
/// </summary>
public sealed partial class TableEditSession
{
    private static readonly StorageProperty[] ExistingStorageAttributes =
    [
        StorageProperty.Tablespace, StorageProperty.PctFree, StorageProperty.InitTrans, StorageProperty.Compression, StorageProperty.Logging, StorageProperty.Parallel,
    ];

    /// <summary>새 테이블은 STORAGE(INITIAL/NEXT)도(TE-39).</summary>
    private static readonly StorageProperty[] NewTableStorageAttributes =
        [.. ExistingStorageAttributes, StorageProperty.InitialExtent, StorageProperty.NextExtent];

    private static readonly string[] StoragePageProperties =
    [
        nameof(LobRows), nameof(HasLobRows), nameof(CanEditStorage), nameof(CanEditStorageAttributes), nameof(CanEditLobStorage), nameof(StorageLockedReason),
        nameof(IsStorageLocked), nameof(HasStorageMove), nameof(StorageMoveText),
    ];

    private Dictionary<StorageProperty, StorageRowViewModel>? _storageRowsByAttribute;
    private IReadOnlyList<StorageRowViewModel> _storageRows = [];
    private bool? _storageRowsForNewTable;
    private readonly ObservableCollection<LobRowViewModel> _lobRows = [];

    /// <summary>모르는 지금 값(사전에 없음·못 읽음).</summary>
    internal static string UnknownText => TableEditText.T("알 수 없음", "Unknown");

    /// <summary>테이블 저장 속성 표 줄(기존 테이블 6줄, 새 테이블은 익스텐트 2줄 더). 같은 속성은 늘 같은 객체.</summary>
    public IReadOnlyList<StorageRowViewModel> StorageRows
    {
        get
        {
            EnsureStorageRows();
            return _storageRows;
        }
    }

    /// <summary>LOB 표 줄(지우지 않은 LOB 열마다, 열 줄 키가 같은 동안 같은 객체).</summary>
    public IReadOnlyList<LobRowViewModel> LobRows
    {
        get
        {
            EnsureStorageRows();
            return _lobRows;
        }
    }

    /// <summary>LOB 표를 보인다(LOB 열이 있음).</summary>
    public bool HasLobRows => LobRows.Count > 0;

    /// <summary>
    /// 저장 페이지를 고칠 수 있다. 기존 특수 테이블(임시·외부·IOT·클러스터)은 저장 속성·LOB 변경을 Core 가 모두 ✖ 하므로 흐린다(검사 계획 SpecialTable).
    /// </summary>
    public bool CanEditStorage => !IsRestricted;

    /// <summary>테이블 저장 속성 칸을 고칠 수 있다(방언이 저장 속성을 함).</summary>
    public bool CanEditStorageAttributes => CanEditStorage && StorageReason(TableEditFeature.StorageAttributes) is null;

    /// <summary>LOB 칸을 고칠 수 있다(방언이 LOB 저장 절을 함).</summary>
    public bool CanEditLobStorage => CanEditStorage && StorageReason(TableEditFeature.LobStorage) is null;

    /// <summary>못 고치는 까닭(특수 테이블 제한 글 · 방언 이유). 고칠 수 있으면 null.</summary>
    public string? StorageLockedReason => IsRestricted ? RestrictionText
        : StorageReason(TableEditFeature.StorageAttributes) ?? (HasLobRows ? StorageReason(TableEditFeature.LobStorage) : null);

    public bool IsStorageLocked => StorageLockedReason is not null;

    /// <summary>
    /// 이 편집이 테이블 MOVE 를 만든다(기존 테이블의 테이블스페이스·압축·LOB 저장 변경 — Core 검사 계획과 같은 기준).
    /// </summary>
    public bool HasStorageMove =>
        !IsNewTable
        && ((Changes.Storage & (StorageChange.Tablespace | StorageChange.Compression)) != 0
            || Changes.Columns.Any(d => d.State == DraftRowState.Modified && d.Changes.HasFlag(ColumnChange.Lob) && !TableDraftDiff.IsRecreated(d, Dialect)));

    /// <summary>
    /// ⚠ MOVE 줄(설계 18.2): 테이블 전체를 다시 쓴다, ONLINE 이 아니면 그동안 DML 잠금, UPDATE INDEXES 가 안 되면 인덱스 REBUILD. MOVE 가 없으면 빈 글.
    /// ONLINE·REBUILD 는 만들어진 문장을 따른다(문장이 없으면 방언·서버 판정).
    /// </summary>
    public string StorageMoveText
    {
        get
        {
            if (!HasStorageMove)
            {
                return string.Empty;
            }

            var move = Script.Statements.FirstOrDefault(s => s.Effect.Kind == TableEditEffectKind.Move);
            var online = move is not null
                ? move.Statement.Text.Contains(" MOVE ONLINE", StringComparison.OrdinalIgnoreCase)
                : StorageReason(TableEditFeature.OnlineMove) is null;
            var rebuild = move is not null
                ? Script.Statements.Any(s => s.Effect.Kind == TableEditEffectKind.Rebuild)
                : StorageReason(TableEditFeature.MoveUpdateIndexes) is not null;
            var parts = new List<string> { TableEditText.T("⚠ MOVE: 테이블 전체를 다시 씁니다", "⚠ MOVE: the whole table is rewritten") };
            if (!online)
            {
                parts.Add(TableEditText.T("ONLINE 이 안 되는 서버는 그동안 DML 이 잠깁니다", "on servers without ONLINE, DML is locked meanwhile"));
            }

            if (rebuild)
            {
                parts.Add(TableEditText.T("MOVE 뒤 인덱스를 다시 만듭니다(REBUILD)", "indexes are rebuilt after the MOVE"));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// 저장 페이지 기능을 못 하는 이유(지역화). 방언 판정, 없으면 Core 보수 판정(<see cref="TableEditDefaults.StorageReasonKey"/>) — 문장 빌더·검사 계획과 같은 기준. 되면 null.
    /// </summary>
    public string? StorageReason(TableEditFeature feature)
    {
        if (Dialect.UnsupportedReasonKey(feature, Context.Server) is { } key)
        {
            return UiText.Get(key, key);
        }

        return TableEditDefaults.StorageReasonKey(feature, Context.Server) is { } core ? UiText.Get(core, CoreStorageReasonText(feature)) : null;
    }

    /// <summary>LOB 열 저장 절을 바꾼다. 원본에 LOB 절이 없고 모든 칸이 비면 null 로 둔다(편집 모델이 원본과 같게).</summary>
    internal void UpdateLob(string rowKey, Func<Oranda.Core.Metadata.LobStorage, Oranda.Core.Metadata.LobStorage> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var original = Original.Column(rowKey)?.Lob;
        UpdateColumn(rowKey, c =>
        {
            var next = change(c.Lob ?? new Oranda.Core.Metadata.LobStorage());
            return c with { Lob = original is null && next == new Oranda.Core.Metadata.LobStorage() ? null : next };
        });
    }

    private string CoreStorageReasonText(TableEditFeature feature) => Context.Server is null
        ? TableEditText.T("서버 버전을 알 수 없어 끕니다", "Turned off because the server version is unknown")
        : feature switch
        {
            TableEditFeature.OnlineMove => TableEditText.T("이 서버는 MOVE ONLINE 을 못 합니다(Oracle 12.2 이상 Enterprise)", "This server cannot MOVE ONLINE (Oracle 12.2+ Enterprise)"),
            TableEditFeature.MoveUpdateIndexes => TableEditText.T("이 서버는 MOVE … UPDATE INDEXES 를 못 합니다(Oracle 12.2 이상)", "This server cannot MOVE … UPDATE INDEXES (Oracle 12.2+)"),
            TableEditFeature.SecureFileLob => TableEditText.T("이 서버에서는 LOB 저장 방식(SECUREFILE·BASICFILE)을 고를 수 없습니다", "The LOB storage kind (SECUREFILE/BASICFILE) cannot be chosen on this server"),
            TableEditFeature.BasicTableCompression => TableEditText.T("기본 압축(BASIC)은 Oracle Enterprise·Tibero 에서만 됩니다", "Basic compression needs Oracle Enterprise or Tibero"),
            _ => TableEditText.T("고급 행 압축(ADVANCED)은 Oracle Enterprise 에서만 됩니다", "Advanced row compression needs Oracle Enterprise"),
        };

    private void EnsureStorageRows()
    {
        if (_storageRowsByAttribute is not null)
        {
            return;
        }

        _storageRowsByAttribute = NewTableStorageAttributes.ToDictionary(a => a, a => new StorageRowViewModel(this, a));
        Recomputed += (_, _) => SyncStorageRows();
        SyncStorageRows();
    }

    private void SyncStorageRows()
    {
        var rows = _storageRowsByAttribute!;
        var forNew = IsNewTable;
        if (_storageRowsForNewTable != forNew)
        {
            _storageRowsForNewTable = forNew;
            _storageRows = [.. (forNew ? NewTableStorageAttributes : ExistingStorageAttributes).Select(a => rows[a])];
            OnPropertyChanged(nameof(StorageRows));
        }

        SyncLobRows();

        var reverted = Draft == Original; // 모두 되돌림(↺ 전체·원본 다시 읽기): 남은 입력 오류도 지운다
        foreach (var row in rows.Values)
        {
            if (reverted)
            {
                row.ClearError();
            }

            row.Refresh();
        }

        foreach (var lob in _lobRows)
        {
            lob.Refresh();
        }

        foreach (var name in StoragePageProperties)
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>LOB 줄: 지우지 않은 열 가운데 원본에 LOB 절이 있거나 형식이 CLOB·NCLOB·BLOB 인 것(편집 모델 순서).</summary>
    private void SyncLobRows()
    {
        var keys = Draft.Columns
            .Where(c => !c.IsDropped && (Original.Column(c.RowKey)?.Lob is not null || TableEditDefaults.IsLobType(c.DataType)))
            .Select(c => c.RowKey).ToList();
        var wanted = keys.ToHashSet(StringComparer.Ordinal);
        for (var i = _lobRows.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(_lobRows[i].Key))
            {
                _lobRows.RemoveAt(i);
            }
        }

        for (var i = 0; i < keys.Count; i++)
        {
            var at = -1;
            for (var j = i; j < _lobRows.Count; j++)
            {
                if (_lobRows[j].Key == keys[i])
                {
                    at = j;
                    break;
                }
            }

            if (at < 0)
            {
                _lobRows.Insert(i, new LobRowViewModel(this, keys[i]));
            }
            else if (at != i)
            {
                _lobRows.Move(at, i);
            }
        }
    }
}
