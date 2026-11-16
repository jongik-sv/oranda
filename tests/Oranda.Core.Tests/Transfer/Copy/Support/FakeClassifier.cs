using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.Core.Tests.Transfer.Copy;

internal sealed class FakeClassifier(Dbms dbms) : IDbErrorClassifier
{
    public FakeClassifier() : this(Dbms.Oracle)
    {
    }

    public Dbms Dbms => dbms;

    public DbErrorKind Classify(int number) => DbErrorKind.Other;

    public DbError? FromException(Exception exception) => null;
}
