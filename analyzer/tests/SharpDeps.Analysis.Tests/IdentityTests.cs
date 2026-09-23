using SharpDeps.Analysis.Contracts;
using SharpDeps.Analysis.Core.Identity;
using SharpDeps.Analysis.Quick;
using Xunit;

namespace SharpDeps.Analysis.Tests;

/// <summary>
/// Pins the hash rule shared with the TypeScript implementation
/// (src/analyzer/identity.ts). The same values are asserted in
/// tests/contract/identityAgreement.test.ts; changing either side requires updating
/// both, on purpose.
/// </summary>
public sealed class IdentityTests
{
    [Fact]
    public void MatchesTheDocumentedHashRule()
    {
        var rootId = Identity.WorkspaceRootId("C:/repo");
        var logicalId = Identity.ProjectLogicalId(rootId, "src/App/App.csproj");
        var variantId = Identity.ProjectVariantId(logicalId, "net10.0", "Debug", null);

        Assert.Equal("wrk_65aa6de15d22cae5", rootId);
        Assert.Equal("prj_e69fd820b24f1b1f", logicalId);
        Assert.Equal("var_7b4b9ff45d2adef1", variantId);
        Assert.Equal("ns_f9fa382aaca132c4", Identity.NamespaceId(variantId, "Core"));
        Assert.Equal(
            "rel_3d6733779d039822",
            Identity.RelationId("projectDeclared", "prj_0123456789abcdef", "prj_fedcba9876543210", "0123456789abcdef"));
        Assert.Equal(
            "doc_ca362217930aa10a",
            Identity.DocumentId(rootId, "src/App/Program.cs", "userSource"));
    }

    [Fact]
    public void NormalizesProfileInputsAndPaths()
    {
        var upper = Identity.ProfileHash("Debug", null, [("prj_1", "NET10.0")]);
        var lower = Identity.ProfileHash("debug", null, [("prj_1", "net10.0")]);

        Assert.Equal("e909108ec79fc382", upper);
        Assert.Equal(upper, lower);
        Assert.Equal(
            Identity.ProjectLogicalId("wrk_x", "src/App/App.csproj"),
            Identity.ProjectLogicalId("wrk_x", @"src\App\App.csproj"));
    }

    [Fact]
    public void KeepsIdsFreeOfHostPathsAndSymbolNames()
    {
        var id = Identity.ProjectLogicalId(Identity.WorkspaceRootId(@"D:\build\agent\repo"), "src/App/App.csproj");

        Assert.Matches("^prj_[0-9a-f]{16}$", id);
        Assert.DoesNotContain("App", id, StringComparison.Ordinal);
        Assert.DoesNotContain("build", id, StringComparison.Ordinal);
    }

    [Fact]
    public void DerivesDomIdsFromTheOpaqueId()
    {
        Assert.Matches("^node-[0-9a-f]{16}$", Identity.DomId("node", "ty_0123456789abcdef"));
        Assert.Matches("^node-[0-9a-f]{16}$", Identity.DomId("node", "<img src=x onerror=alert(1)>"));
    }
}
