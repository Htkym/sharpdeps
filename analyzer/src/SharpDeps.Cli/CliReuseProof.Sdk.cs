namespace SharpDeps.Cli;

internal static partial class CliReuseProof
{
    // The admitted package and Git read closures were compared at dotnet/dotnet
    // a8dd0577 (10.0.402) and 5a2162bea6845eabc1fc721624a43568c65db3b1 (10.0.403).
    // Matching source does not admit another SDK: each installed payload is pinned below.
    private static (string Path, string Hash)[]? PackageSdkPayloads(string version) => version switch
    {
        "10.0.402" =>
        [
            ("Sdks/Microsoft.NET.Sdk/tools/net10.0/Microsoft.NET.Build.Tasks.dll", "a673d48e1aa2ede63547cf63cc94d59b90b361512e13210943dc70171e1c1ed1"),
            ("Sdks/Microsoft.NET.Sdk/targets/Microsoft.PackageDependencyResolution.targets", "cb0a2cf6ea590fbd3dfbf333d32fc7ee1db3344908ab04dc31288c5591c5136c")
        ],
        "10.0.403" =>
        [
            ("Sdks/Microsoft.NET.Sdk/tools/net10.0/Microsoft.NET.Build.Tasks.dll", "cbb0248c51684837e1d76ba3b9984420fc35817290d0289ff2d9fd48711142f5"),
            ("Sdks/Microsoft.NET.Sdk/targets/Microsoft.PackageDependencyResolution.targets", "cb0a2cf6ea590fbd3dfbf333d32fc7ee1db3344908ab04dc31288c5591c5136c")
        ],
        _ => null
    };

    private static (string Path, string Hash)[]? GitSdkPayloads(string version) => version switch
    {
        "10.0.402" =>
        [
            ("tools/net/Microsoft.Build.Tasks.Git.dll", "a68655c3c976a8837897f9bae8308069691e3477922d60cc92636a1483fb8ecb"),
            ("build/Microsoft.Build.Tasks.Git.targets", "a3499b2ed15095be71667a690d86e4a18abb7889f224f52c7bc5459c8479ad34"),
            ("build/Microsoft.Build.Tasks.Git.props", "19a1134fa4c2825c016df1b8c42e375f0c744e9b1a2262222c2ac101e74f0544")
        ],
        "10.0.403" =>
        [
            ("tools/net/Microsoft.Build.Tasks.Git.dll", "34e7a0fe3858c8a723122e8d128af9b1f33247b6c42aa3dabc55bbff86ecf8f9"),
            ("build/Microsoft.Build.Tasks.Git.targets", "a3499b2ed15095be71667a690d86e4a18abb7889f224f52c7bc5459c8479ad34"),
            ("build/Microsoft.Build.Tasks.Git.props", "19a1134fa4c2825c016df1b8c42e375f0c744e9b1a2262222c2ac101e74f0544")
        ],
        _ => null
    };
}
