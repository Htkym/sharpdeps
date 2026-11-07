namespace SharpDeps.Cli.Tests;

using System.Collections;
using System.Text.Json;
using SharpDeps.Query;
using Xunit;

public sealed class ProofDiagnosticTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoreFoundationClassificationDoesNotMutateEnvironmentOrExposeValues(bool anotherUnknown)
    {
        const string marker = "synthetic-private-value-not-for-diagnostic-output";
        var environment = new Hashtable(StringComparer.OrdinalIgnoreCase) { ["PATH"] = marker, ["__CF_USER_TEXT_ENCODING"] = marker };
        if (anotherUnknown)
            environment.Add("UNRECOGNIZED_ORACLE_TEST_PROPERTY", marker);
        var before = environment.Count;
        var result = Describe(environment);
        Assert.True(result.GetProperty("coreFoundationPresent").GetBoolean());
        Assert.Equal(anotherUnknown ? 2 : 1, result.GetProperty("unknownEnvironmentCount").GetInt32());
        Assert.False(result.GetProperty("normalized").GetBoolean());
        Assert.Equal(!anotherUnknown, result.GetProperty("normalizedWithoutCoreFoundation").GetBoolean());
        Assert.Equal(before, environment.Count);
        Assert.Equal(marker, environment["__CF_USER_TEXT_ENCODING"]);
        Assert.DoesNotContain(marker, result.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MSBUILD_EXE_PATH")]
    [InlineData("MSBuildSDKsPath")]
    [InlineData("MSBuildExtensionsPath")]
    public void RegistrationMismatchIsSeparateFromUnknownEnvironment(string name)
    {
        var environment = new Hashtable(StringComparer.OrdinalIgnoreCase) { [name] = Path.GetFullPath(Path.GetTempPath()) };
        var result = Describe(environment);
        Assert.Equal(0, result.GetProperty("unknownEnvironmentCount").GetInt32());
        Assert.Equal("mismatched", result.GetProperty("registeredEnvironment").GetProperty(name).GetString());
        Assert.False(result.GetProperty("normalized").GetBoolean());
        Assert.False(result.GetProperty("normalizedWithoutCoreFoundation").GetBoolean());
    }

    [Theory]
    [InlineData("MSBUILDDISABLENODEREUSE")]
    [InlineData("MSBuildEnableWorkloadResolver")]
    public void UnexpectedControlsAreReportedWithoutTheirValues(string name)
    {
        const string marker = "unexpected-test-control";
        var result = Describe(new Hashtable(StringComparer.OrdinalIgnoreCase) { [name] = marker });
        Assert.Equal(0, result.GetProperty("unknownEnvironmentCount").GetInt32());
        Assert.Equal("mismatched", result.GetProperty("controls").GetProperty(name).GetString());
        Assert.False(result.GetProperty("normalized").GetBoolean());
        Assert.DoesNotContain(marker, result.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticCopyRetainsDistinctCaseSensitiveEnvironmentKeys()
    {
        var environment = new Hashtable(StringComparer.Ordinal) { ["PATH"] = "first-private-value", ["Path"] = "second-private-value" };
        var result = Describe(environment);
        Assert.Equal(2, environment.Count);
        Assert.Equal(0, result.GetProperty("unknownEnvironmentCount").GetInt32());
        Assert.True(result.GetProperty("normalized").GetBoolean());
        Assert.True(result.GetProperty("normalizedWithoutCoreFoundation").GetBoolean());
        Assert.DoesNotContain("private-value", result.GetRawText(), StringComparison.Ordinal);
    }

    private static JsonElement Describe(IDictionary environment)
        => JsonSerializer.SerializeToElement(OracleProcess.DescribeProofEnvironment(environment,
            Path.Combine(Path.GetTempPath(), "diagnostic-only-sdk", "10.0.403")), QueryJson.Options);
}
