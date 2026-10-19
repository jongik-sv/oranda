namespace GridSpike;

/// <summary>열 종류. 실제 결과 집합의 Oracle 타입을 흉내 낸다.</summary>
public enum ColKind { Int64, Text, Date, Decimal, Code }

/// <summary>500행 한 페이지. 열 지향 버퍼에 원시 값을 담는다(문자열 변환은 그릴 때).</summary>
public sealed class Page
{
    public readonly int FirstRow;
    public readonly int Count;
    public readonly Array[] Columns;

    public Page(int firstRow, int count, Array[] columns) { FirstRow = firstRow; Count = count; Columns = columns; }
}

/// <summary>합성 결과 집합: 행 수·열 정의와 페이지 생성(가짜 fetch).</summary>
public sealed class SyntheticResultSet
{
    public const int PageSize = 500;
    public readonly int RowCount;
    public readonly ColKind[] Kinds;
    public readonly string[] Names;
    static readonly string[] Words = ["ORDER", "LINE", "STEEL", "COIL", "PLATE", "HEAT", "SLAB", "GRADE", "MILL", "YARD", "SHIP", "LOT", "QA", "PASS", "HOLD"];

    public SyntheticResultSet(int rows, int cols)
    {
        RowCount = rows;
        Kinds = new ColKind[cols];
        Names = new string[cols];
        for (int c = 0; c < cols; c++)
        {
            Kinds[c] = (ColKind)(c % 5);
            Names[c] = $"{Kinds[c].ToString().ToUpperInvariant()}_{c:00}";
        }
    }

    public Page Fetch(int pageIndex)
    {
        int first = pageIndex * PageSize;
        int count = Math.Min(PageSize, RowCount - first);
        var cols = new Array[Kinds.Length];
        for (int c = 0; c < Kinds.Length; c++)
        {
            switch (Kinds[c])
            {
                case ColKind.Int64:
                {
                    var a = new long[count];
                    for (int i = 0; i < count; i++) a[i] = (long)(first + i) * 31 + c;
                    cols[c] = a; break;
                }
                case ColKind.Decimal:
                {
                    var a = new double[count];
                    for (int i = 0; i < count; i++) a[i] = ((first + i) * 7919L % 1_000_000) / 100.0 + c;
                    cols[c] = a; break;
                }
                case ColKind.Date:
                {
                    var a = new DateTime[count];
                    var b = new DateTime(2020, 1, 1);
                    for (int i = 0; i < count; i++) a[i] = b.AddSeconds((first + i) * 97L + c * 3600);
                    cols[c] = a; break;
                }
                case ColKind.Text:
                {
                    // 8~40자 문자열: 실제 fetch 처럼 셀마다 string 인스턴스가 생긴다.
                    var a = new string[count];
                    for (int i = 0; i < count; i++)
                    {
                        int r = first + i;
                        int n = 1 + (r + c) % 4;
                        a[i] = n switch
                        {
                            1 => $"{Words[r % 15]}-{r}",
                            2 => $"{Words[r % 15]} {Words[(r + c) % 15]}-{r}",
                            3 => $"{Words[r % 15]} {Words[(r + c) % 15]} {Words[(r * 3) % 15]}-{r}",
                            _ => $"{Words[r % 15]} {Words[(r + c) % 15]} {Words[(r * 3) % 15]} {Words[(r * 7) % 15]}-{r}",
                        };
                    }
                    cols[c] = a; break;
                }
                default:
                {
                    var a = new string[count];
                    for (int i = 0; i < count; i++) a[i] = Words[(first + i + c) % 15];   // 짧은 코드(중복 인스턴스 공유)
                    cols[c] = a; break;
                }
            }
        }
        return new Page(first, count, cols);
    }
}

/// <summary>LRU 페이지 캐시(페이지 수 상한). 실제 설계는 메모리 상한 + 임시 파일 강등이지만 시제품은 개수 상한만.</summary>
public sealed class PageCache
{
    readonly SyntheticResultSet _rs;
    readonly int _maxPages;
    readonly Dictionary<int, LinkedListNode<Page>> _map = new();
    readonly LinkedList<Page> _lru = new();
    public int Fetches;

    public PageCache(SyntheticResultSet rs, int maxPages) { _rs = rs; _maxPages = maxPages; }
    public int Count => _map.Count;

    public Page Get(int row)
    {
        int pi = row / SyntheticResultSet.PageSize;
        if (_map.TryGetValue(pi, out var node))
        {
            if (node != _lru.First) { _lru.Remove(node); _lru.AddFirst(node); }
            return node.Value;
        }
        var p = _rs.Fetch(pi);
        Fetches++;
        node = _lru.AddFirst(p);
        _map[pi] = node;
        while (_map.Count > _maxPages)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(last.Value.FirstRow / SyntheticResultSet.PageSize);
        }
        return p;
    }

    public void Clear() { _map.Clear(); _lru.Clear(); }
}
