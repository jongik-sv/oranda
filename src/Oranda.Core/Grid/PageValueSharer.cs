using System.Runtime.InteropServices;

namespace Oranda.Core.Grid;

/// <summary>
/// 페이지 1개를 채우는 동안 같은 열의 반복 값(문자열·NUMBER·날짜)이 객체 하나를 가리키게 한다(TSK-06-12, D-023).
/// 값은 바뀌지 않는다: NUMBER 는 값과 소수 자릿수가 모두 같을 때만 같은 상자를 쓴다(1.0 과 1 은 따로).
/// 페이지가 끝나면 버린다. 값이 거의 안 겹치는 열은 처음 <see cref="Probe"/> 개를 보고 공유를 멈춰 조회 비용을 줄인다.
/// </summary>
public sealed class PageValueSharer
{
    private const int Probe = 64, MaxDistinct = 256;

    private readonly Column[] _columns;

    public PageValueSharer(int columnCount)
    {
        _columns = new Column[columnCount];
        for (var i = 0; i < _columns.Length; i++)
        {
            _columns[i] = new Column();
        }
    }

    /// <summary>열 <paramref name="column"/> 의 값 <paramref name="value"/> 와 같은 값을 이미 쥐고 있으면 그 객체를, 아니면 값을 돌려준다.</summary>
    public object? Share(int column, object? value)
    {
        if (value is null || (uint)column >= (uint)_columns.Length)
        {
            return value;
        }

        return _columns[column].Share(value);
    }

    private sealed class Column
    {
        private Dictionary<string, string>? _strings;
        private Dictionary<(decimal Value, byte Scale), object>? _decimals;
        private Dictionary<DateTime, object>? _dates;
        private int _seen, _hits;
        private bool _off;

        public object Share(object value)
        {
            if (_off)
            {
                return value;
            }

            var shared = value switch
            {
                string s => ShareIn(ref _strings, s, s, s),
                decimal d => ShareIn(ref _decimals, (d, (byte)((decimal.GetBits(d)[3] >> 16) & 0xFF)), value, value),
                DateTime t => ShareIn(ref _dates, t, value, value),
                _ => value,
            };

            if (value is string or decimal or DateTime && ++_seen == Probe && _hits == 0)
            {
                _off = true;
                _strings = null;
                _decimals = null;
                _dates = null;
            }

            return shared;
        }

        private object ShareIn<TKey, TValue>(ref Dictionary<TKey, TValue>? map, TKey key, TValue candidate, object original)
            where TKey : notnull
        {
            map ??= new Dictionary<TKey, TValue>();
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out var exists);
            if (exists)
            {
                _hits++;
                return slot!;
            }

            if (map.Count > MaxDistinct)
            {
                map.Remove(key);
                return original;
            }

            slot = candidate;
            return original;
        }
    }
}
