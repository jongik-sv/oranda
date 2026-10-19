using BenchmarkDotNet.Attributes;
using EditorSpike.L1;

namespace EditorSpike.Bench;

/// <summary>e3-L1: 1만 줄 문서에서 전체 렉싱과 편집 1회당 증분 렉싱 시간(편집 → 되돌림 2회를 1 연산 2개로 셈).</summary>
[MemoryDiagnoser]
[Config(typeof(SpikeConfig))]
public class LexerBenchmarks
{
    ListLineSource _src = null!;
    IncrementalLexer _inc = null!;
    int _mid;
    string _orig = "";

    [GlobalSetup]
    public void Setup()
    {
        _src = ListLineSource.FromText(PlSqlGen.Package());
        _inc = new IncrementalLexer(_src);
        _inc.LexAll();
        _mid = 5000;
        _orig = _src.Lines[_mid];
    }

    [Benchmark(Description = "전체 렉싱 1만 줄")]
    public int FullLex()
    {
        _inc.LexAll();
        return _inc.LineCount;
    }

    [Benchmark(Description = "증분: 문자 1개 입력", OperationsPerInvoke = 2)]
    public int CharEdit()
    {
        _src.Lines[_mid] = _orig + "x";
        var a = _inc.ApplyEdit(_mid, 1, 1).relexed;
        _src.Lines[_mid] = _orig;
        return a + _inc.ApplyEdit(_mid, 1, 1).relexed;
    }

    [Benchmark(Description = "증분: Enter(줄 분할·합치기)", OperationsPerInvoke = 2)]
    public int EnterEdit()
    {
        _src.Lines[_mid] = _orig[..4];
        _src.Lines.Insert(_mid + 1, _orig[4..]);
        var a = _inc.ApplyEdit(_mid, 1, 2).relexed;
        _src.Lines[_mid] = _orig;
        _src.Lines.RemoveAt(_mid + 1);
        return a + _inc.ApplyEdit(_mid, 2, 1).relexed;
    }

    [Benchmark(Description = "증분: '/*' 열기(다음 */ 까지 전파)", OperationsPerInvoke = 2)]
    public int OpenComment()
    {
        _src.Lines[_mid] = "/*" + _orig;
        var a = _inc.ApplyEdit(_mid, 1, 1).relexed;
        _src.Lines[_mid] = _orig;
        return a + _inc.ApplyEdit(_mid, 1, 1).relexed;
    }

    [Benchmark(Description = "최악: 1번 줄 '/*' 열고 닫는 */ 가 없음(끝까지 전파)", OperationsPerInvoke = 2)]
    public int OpenCommentNoClose()
    {
        // 문서에 */ 가 없도록 만든 별도 원본에서 측정
        _noCloseSrc.Lines[0] = "/*" + _noCloseOrig;
        var a = _noClose.ApplyEdit(0, 1, 1).relexed;
        _noCloseSrc.Lines[0] = _noCloseOrig;
        return a + _noClose.ApplyEdit(0, 1, 1).relexed;
    }

    ListLineSource _noCloseSrc = null!;
    IncrementalLexer _noClose = null!;
    string _noCloseOrig = "";

    [GlobalSetup(Target = nameof(OpenCommentNoClose))]
    public void SetupNoClose()
    {
        Setup();
        _noCloseSrc = ListLineSource.FromText(PlSqlGen.Package().Replace("/*", "/ *").Replace("*/", "* /"));
        _noClose = new IncrementalLexer(_noCloseSrc);
        _noClose.LexAll();
        _noCloseOrig = _noCloseSrc.Lines[0];
    }
}
