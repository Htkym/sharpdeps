// Shared paths for tests that read repository fixtures.

namespace SharpDeps.Analysis.Tests;

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string Fixture(params string[] segments)
        => Path.Combine([RepositoryRoot, "tests", "fixtures", .. segments]);

    private static string FindRepositoryRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("SHARPDEPS_TEST_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            if (!Path.IsPathFullyQualified(explicitRoot) ||
                !Directory.Exists(Path.Combine(explicitRoot, "tests", "fixtures")) ||
                !Directory.Exists(Path.Combine(explicitRoot, "analyzer", "src")))
            {
                throw new InvalidOperationException("SHARPDEPS_TEST_REPOSITORY_ROOT must identify the repository root.");
            }

            return Path.GetFullPath(explicitRoot);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located from the test output directory.");
    }
}
