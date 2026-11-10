namespace SharpDeps.Cli.Tests;

using Xunit;

public sealed class OracleOwnedStopTests
{
    private const string OwnerRoot = @"D:\gitroot\owned-stop-synthetic";
    private const string FixtureRoot = @"D:\gitroot\fixture-synthetic";
    private const string Request = OwnerRoot + @"\owned-stop.request.json";
    private static readonly string Nonce = new('A', 64);

    [WindowsEnvironmentFact]
    public void BindingIsMandatoryCompleteAndSeparateBeforeAnyFixtureOrChild()
    {
        Assert.Throws<InvalidOperationException>(() => OracleOwnedStop.RequireBinding(null, null, FixtureRoot));
        foreach (var pair in new (string? Path, string? Nonce)[]
        {
            (Request, null), (null, Nonce), ("", Nonce), (Request, ""), (Request, new string('A', 63)),
            (Request, new string('G', 64)), (Request, new string('a', 64)),
            (@"C:\temp\owned-stop.request.json", Nonce), (OwnerRoot + @"\other.json", Nonce),
            (OwnerRoot + @"\nested\owned-stop.request.json", Nonce),
            (OwnerRoot + @"\..\other\owned-stop.request.json", Nonce)
        })
            Assert.Throws<ArgumentException>(() => OracleOwnedStop.RequireBinding(pair.Path, pair.Nonce, FixtureRoot));
        Assert.Throws<ArgumentException>(() => OracleOwnedStop.RequireBinding(Request, Nonce, OwnerRoot));
        Assert.Equal(new OracleOwnedStopBinding(Request, Nonce), OracleOwnedStop.RequireBinding(Request, Nonce, FixtureRoot));
        Assert.Null(new OracleCliIsolationSpec(FixtureRoot, @"C:\Program Files\dotnet\dotnet.exe", @"C:\Program Files\dotnet").OwnedStop);
    }

    [Fact]
    public void OrdinaryNonzeroRejectionRequiresTerminalWaitAndBothCompletedDrains()
    {
        Assert.False(OracleOwnedStop.TerminalAndDrained(false, false));
        Assert.False(OracleOwnedStop.TerminalAndDrained(true, false));
        Assert.False(OracleOwnedStop.TerminalAndDrained(false, true));
        Assert.True(OracleOwnedStop.TerminalAndDrained(true, true));
    }
}
