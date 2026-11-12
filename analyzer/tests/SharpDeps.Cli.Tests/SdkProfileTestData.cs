namespace SharpDeps.Cli.Tests;

using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class SdkProfileTestData
{
    internal const string Id = "Proof.Package";
    internal const string Version = "1.0.0";
    internal const string Key = Id + "/" + Version;
    internal const string Dll = "lib/net10.0/Proof.Package.dll";
    internal const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
        "<TargetFramework>net10.0</TargetFramework><EnableNETAnalyzers>false</EnableNETAnalyzers>" +
        "<GitRepositoryConfigurationScope>local</GitRepositoryConfigurationScope>" +
        "</PropertyGroup><ItemGroup><PackageReference Include=\"Proof.Package\" Version=\"1.0.0\" /></ItemGroup></Project>";
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static object? Invoke(string method, params object?[] arguments)
        => typeof(CliReuseProof).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, arguments);

    internal static bool Boolean(string method, params object?[] arguments)
    {
        try
        {
            return (bool)Invoke(method, arguments)!;
        }
        catch (TargetInvocationException error) when (error.InnerException is IOException or JsonException
            or FormatException or InvalidOperationException or KeyNotFoundException or System.Xml.XmlException or UnauthorizedAccessException or CliFailure)
        {
            return false;
        }
    }

    internal static object Package(string project) => Activator.CreateInstance(
        typeof(CliReuseProof).GetNestedType("PackageSpec", BindingFlags.NonPublic)!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null, args: [project, Id, Version], culture: null)!;

    internal static string NewRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    internal static string InstalledSdk(string version) => Path.GetFullPath(Path.Combine(
        RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "sdk", version));

    internal static void Write(string root, string relative, string text)
    {
        var path = CliPaths.InRoot(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Utf8);
    }

    internal static void Git(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, ".git", "objects"));
        Directory.CreateDirectory(Path.Combine(root, ".git", "refs", "heads"));
        Write(root, ".git/config", "[core]\nrepositoryformatversion = 0\nbare = false\n");
        Write(root, ".git/HEAD", "ref: refs/heads/main\n");
        Write(root, ".git/refs/heads/main", new string('1', 40) + "\n");
    }

    internal static Dictionary<string, byte[]> PackageFiles(int value = 7)
    {
        var compilation = CSharpCompilation.Create("Proof.Package",
            [CSharpSyntaxTree.ParseText($"namespace ProofPackage; public static class PackageApi {{ public static int Value => {value}; }}", encoding: Utf8)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, deterministic: true));
        using var dll = new MemoryStream();
        var emitted = compilation.Emit(dll);
        if (!emitted.Success)
            throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["proof.package.nuspec"] = Utf8.GetBytes("<?xml version=\"1.0\"?><package><metadata><id>Proof.Package</id>" +
                "<version>1.0.0</version><authors>SharpDeps fixture</authors><description>Closed DLL fixture</description></metadata></package>"),
            [Dll] = dll.ToArray()
        };
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in files)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        var nupkg = archive.ToArray();
        var contentHash = Convert.ToBase64String(SHA512.HashData(nupkg));
        files["proof.package.1.0.0.nupkg"] = nupkg;
        files["proof.package.1.0.0.nupkg.sha512"] = Utf8.GetBytes(contentHash);
        files[".nupkg.metadata"] = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, contentHash, source = "synthetic" });
        return files;
    }

    internal static string ContentHash(Dictionary<string, byte[]> files)
    {
        using var document = JsonDocument.Parse(files[".nupkg.metadata"]);
        return document.RootElement.GetProperty("contentHash").GetString()!;
    }

    internal static byte[] Assets(string project, string cache, Dictionary<string, byte[]> files)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 3,
            targets = new Dictionary<string, object>
            {
                ["net10.0"] = new Dictionary<string, object>
                {
                    [Key] = new
                    {
                        type = "package",
                        compile = new Dictionary<string, object> { [Dll] = new { } },
                        runtime = new Dictionary<string, object> { [Dll] = new { } }
                    }
                }
            },
            libraries = new Dictionary<string, object>
            {
                [Key] = new
                {
                    type = "package",
                    path = "proof.package/1.0.0",
                    sha512 = ContentHash(files),
                    files = files.Keys.Where(p => !p.EndsWith(".nupkg", StringComparison.Ordinal)).ToArray()
                }
            },
            packageFolders = new Dictionary<string, object> { [cache + Path.DirectorySeparatorChar] = new { } },
            project = new
            {
                restore = new { packagesPath = cache, projectPath = project },
                frameworks = new Dictionary<string, object>
                {
                    ["net10.0"] = new
                    {
                        framework = "net10.0",
                        frameworkReferences = new Dictionary<string, object> { ["Microsoft.NETCore.App"] = new { } },
                        dependencies = new Dictionary<string, object> { [Id] = new { target = "Package", version = "[1.0.0, )" } }
                    }
                }
            }
        });

    internal static byte[] Lock(Dictionary<string, byte[]> files) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        dependencies = new Dictionary<string, object>
        {
            ["net10.0"] = new Dictionary<string, object>
            { [Id] = new { type = "Direct", requested = "[1.0.0, )", resolved = Version, contentHash = ContentHash(files) } }
        }
    });

    internal static byte[] Cache(int group, string item, params (string Name, string Value)[] metadata)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        var strings = metadata.SelectMany(m => new[] { m.Name, m.Value }).Distinct(StringComparer.Ordinal).ToList();
        writer.Write(0x41474b50);
        writer.Write(12);
        writer.Write(new byte[32]);
        writer.Write(0);
        for (var current = 0; current < 17; current++)
        {
            writer.Write(current == group ? 1 : 0);
            if (current != group)
                continue;
            writer.Write(item);
            writer.Write(metadata.Length);
            foreach (var (name, value) in metadata)
            {
                writer.Write(strings.IndexOf(name));
                writer.Write(strings.IndexOf(value));
            }
        }
        var table = checked((int)stream.Position);
        writer.Write(strings.Count);
        foreach (var value in strings)
            writer.Write(value);
        writer.Seek(40, SeekOrigin.Begin);
        writer.Write(table);
        writer.Flush();
        return stream.ToArray();
    }

    internal static byte[] ChangeJson(byte[] bytes, Action<JsonObject> change)
    {
        var value = JsonNode.Parse(bytes)!.AsObject();
        change(value);
        return Utf8.GetBytes(value.ToJsonString());
    }
}
