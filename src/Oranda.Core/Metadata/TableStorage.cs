namespace Oranda.Core.Metadata;

/// <summary>
/// 테이블 저장 속성(테이블 편집기 「저장」 페이지, 설계 18.2). 사전(<c>ALL_TABLES</c>)에서 읽은 값이고, 모르는 값은 null 이다.
/// 편집 모델(<c>TableDraft.Storage</c>)도 같은 형을 쓴다 — 그때 null 은 「바꾸지 않음」(비운 「바꿀 값」 칸).
/// 테이블스페이스는 <see cref="TableInfo.Tablespace"/> 에 따로 있다.
/// 공급자 읽기 규칙: <c>DEGREE</c> 는 앞뒤 공백을 떼고 <c>DEFAULT</c> = 0, <c>COMPRESSION = DISABLED</c> = <see cref="TableCompression.None"/>,
/// <c>COMPRESS_FOR</c> BASIC = Basic, ADVANCED·OLTP = Advanced, 그 밖 = Other. Tibero <c>COMPRESSION YES</c> = Basic, <c>DEGREE</c> 0 = 1·-1 = 0(<see cref="TableStorageRows.TiberoParallel"/>). 사전에 열이 없으면 null.
/// </summary>
public sealed record TableStorage
{
    /// <summary><c>PCT_FREE</c>(0~99).</summary>
    public int? PctFree { get; init; }

    /// <summary><c>INI_TRANS</c>(1~255).</summary>
    public int? InitTrans { get; init; }

    /// <summary>테이블 압축.</summary>
    public TableCompression? Compression { get; init; }

    /// <summary><c>LOGGING</c>(예/아니오).</summary>
    public bool? Logging { get; init; }

    /// <summary>병렬 정도(<c>DEGREE</c>). 1 = <c>NOPARALLEL</c>, 0 = <c>PARALLEL</c>(서버 기본, 사전 <c>DEFAULT</c>), 2 이상 = <c>PARALLEL n</c>.</summary>
    public int? Parallel { get; init; }

    /// <summary><c>STORAGE (INITIAL …)</c> 글(<c>64K</c>, <c>1M</c>, 바이트 수). 새 테이블에서만 문장에 쓴다(TE-39). 공급자는 채우지 않는다(사전 값은 지연 세그먼트·최소 익스텐트라 입력과 달라 거짓 차이만 만든다).</summary>
    public string? InitialExtent { get; init; }

    /// <summary><c>STORAGE (NEXT …)</c> 글. 새 테이블에서만(TE-39), 공급자는 채우지 않는다.</summary>
    public string? NextExtent { get; init; }
}

/// <summary>테이블 압축(설계 18.2). 사전 <c>COMPRESSION</c>·<c>COMPRESS_FOR</c> 를 맞춘다. 하이브리드 컬럼 압축 등 고칠 수 없는 것은 <see cref="Other"/>. 값을 더할 때는 끝에 덧붙인다.</summary>
public enum TableCompression
{
    /// <summary>압축 없음(<c>NOCOMPRESS</c>).</summary>
    None,

    /// <summary>기본 압축(Oracle <c>ROW STORE COMPRESS BASIC</c>·11g <c>COMPRESS BASIC</c>, Tibero <c>COMPRESS</c>).</summary>
    Basic,

    /// <summary>고급 행 압축(Oracle <c>ROW STORE COMPRESS ADVANCED</c>·11g <c>COMPRESS FOR OLTP</c>).</summary>
    Advanced,

    /// <summary>그 밖(읽기만, 바꿀 값으로 고를 수 없음).</summary>
    Other,
}

/// <summary>LOB 저장 방식(<c>ALL_LOBS.SECUREFILE</c>). Tibero 는 읽을 열이 없어 null(모름).</summary>
public enum LobStorageKind
{
    SecureFile,
    BasicFile,
}

/// <summary>SECUREFILE LOB 압축(<c>ALL_LOBS.COMPRESSION</c>: NO·LOW·MEDIUM·HIGH).</summary>
public enum LobCompression
{
    None,
    Low,
    Medium,
    High,
}

/// <summary>
/// LOB 열 하나의 저장 절(설계 18.2 LOB 표). 모르는 값은 null. 편집 모델에서 null 은 「바꾸지 않음」.
/// 공급자 읽기 규칙: <c>SECUREFILE</c> YES/NO = SecureFile/BasicFile(Tibero 는 열이 없어 null), BASICFILE 의 <c>COMPRESSION</c>·<c>DEDUPLICATION</c> = <c>NONE</c> 은 null,
/// <c>COMPRESSION</c> NO(Tibero NOCOMPRESS) = None, <c>DEDUPLICATION</c> NO = false·LOB = true, <c>IN_ROW</c> YES/NO. 규칙 구현 = <see cref="TableStorageRows"/>.
/// </summary>
public sealed record LobStorage
{
    public LobStorageKind? Kind { get; init; }

    public string? Tablespace { get; init; }

    public LobCompression? Compression { get; init; }

    /// <summary>중복 제거(<c>DEDUPLICATE</c>, 거짓 = <c>KEEP_DUPLICATES</c>). SECUREFILE 만.</summary>
    public bool? Deduplicate { get; init; }

    /// <summary>행 안에 저장(<c>ENABLE STORAGE IN ROW</c>).</summary>
    public bool? InRow { get; init; }
}

/// <summary>테이블의 LOB 열과 그 저장 절(<c>ALL_LOBS</c> 한 줄).</summary>
public sealed record LobInfo(string Column, LobStorage Storage);
