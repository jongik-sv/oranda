using Oranda.Core.Connection;
using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

public class ConnectionRelinkerTests
{
    private static readonly Guid IdA = Guid.NewGuid();

    private static ConnectionProfile P(string name, Guid id, ConnectionEnvironment env = ConnectionEnvironment.Development)
        => new(name, "U", "db.example.com/ORCL", Id: id, Environment: env);

    [Fact]
    public void Relink_prefers_same_id_then_same_name()
    {
        var byId = P("Dev", IdA);
        var byName = P("dev", Guid.NewGuid());
        Assert.Equal(RelinkKind.ById, ConnectionRelinker.Resolve(new ConnectionRef("DEV", "U", IdA), [byName, byId], true).Kind);
        var r = ConnectionRelinker.Resolve(new ConnectionRef("DEV", "U", Guid.NewGuid()), [byName, byId], true);
        Assert.Equal(RelinkKind.ByName, r.Kind);
        Assert.Same(byName, r.Profile);
    }

    [Fact]
    public void Relink_not_found_when_no_name_matches()
    {
        var r = ConnectionRelinker.Resolve(new ConnectionRef("Other", "U", IdA), [P("Dev", Guid.NewGuid())], false);
        Assert.Equal(RelinkKind.NotFound, r.Kind);
        Assert.Null(r.Profile);
    }

    [Fact]
    public void Target_relink_to_production_profile_is_blocked()
    {
        var prod = P("Dev", IdA, ConnectionEnvironment.Production);
        var target = ConnectionRelinker.Resolve(new ConnectionRef("Dev", "U", IdA), [prod], true);
        Assert.Equal(RelinkKind.BlockedProduction, target.Kind);
        Assert.Null(target.Profile);
        var source = ConnectionRelinker.Resolve(new ConnectionRef("Dev", "U"), [prod], false);
        Assert.Equal(RelinkKind.ByName, source.Kind);
        Assert.Same(prod, source.Profile);
    }

    [Fact]
    public void Guard_rejects_production_and_recreate_needs_development()
    {
        var dev = P("D", Guid.NewGuid());
        var prod = P("P", Guid.NewGuid(), ConnectionEnvironment.Production);
        var test = P("T", Guid.NewGuid(), ConnectionEnvironment.Test);
        Assert.True(CopyTargetGuard.IsAllowedTarget(dev));
        Assert.True(CopyTargetGuard.IsAllowedTarget(test));
        Assert.False(CopyTargetGuard.IsAllowedTarget(prod));
        Assert.True(CopyTargetGuard.CanRecreate(dev));
        Assert.False(CopyTargetGuard.CanRecreate(test));
        Assert.False(CopyTargetGuard.CanRecreate(prod));
        Assert.Throws<InvalidOperationException>(() => CopyTargetGuard.EnsureAllowedTarget(prod));
        CopyTargetGuard.EnsureAllowedTarget(dev);
    }

    [Fact]
    public void Target_prefers_allowed_profile_among_same_names()
    {
        var prod = P("Dev", Guid.NewGuid(), ConnectionEnvironment.Production);
        var dev = P("dev", Guid.NewGuid());
        var r = ConnectionRelinker.Resolve(new ConnectionRef("Dev", "U"), [prod, dev], true);
        Assert.Same(dev, r.Profile);
    }
}
