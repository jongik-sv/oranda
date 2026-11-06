using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

public class MaskFunctionsTests
{
    private static readonly string SaltA = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly string SaltB = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray());

    [Fact]
    public void Hash_is_deterministic_per_salt_and_truncated()
    {
        var rule = new MaskRule("EMAIL", MaskKind.Hash);
        var a1 = MaskFunctions.Apply(rule, "a@b.example", SaltA, 64);
        Assert.Equal(a1, MaskFunctions.Apply(rule, "a@b.example", SaltA, 64));
        Assert.NotEqual(a1, MaskFunctions.Apply(rule, "a@b.example", SaltB, 64));
        var cut = MaskFunctions.Apply(rule, "a@b.example", SaltA, 10)!;
        Assert.Equal(10, cut.Length);
        Assert.Matches("^[0-9A-F]+$", cut);
        Assert.Null(MaskFunctions.Apply(rule, null, SaltA, 10));
    }

    [Fact]
    public void Keep_first_and_last_mask_rest()
    {
        Assert.Equal("010********", MaskFunctions.Apply(new MaskRule("P", MaskKind.KeepFirst, null, 3), "01012345678", SaltA, 20));
        Assert.Equal("*******5678", MaskFunctions.Apply(new MaskRule("P", MaskKind.KeepLast, null, 4), "01012345678", SaltA, 20));
        Assert.Equal("X", MaskFunctions.Apply(new MaskRule("P", MaskKind.Fixed, "X"), null, SaltA, 20));
    }

    [Fact]
    public void Non_text_columns_allow_only_fixed_and_null()
    {
        Assert.Equal([MaskKind.Fixed, MaskKind.Null], MaskFunctions.AllowedKinds("NUMBER"));
        Assert.Equal([MaskKind.Fixed, MaskKind.Null], MaskFunctions.AllowedKinds("DATE"));
        Assert.Equal(5, MaskFunctions.AllowedKinds("VARCHAR2").Count);
        Assert.Equal(32, Convert.FromBase64String(MaskFunctions.NewSalt()).Length);
    }
}
