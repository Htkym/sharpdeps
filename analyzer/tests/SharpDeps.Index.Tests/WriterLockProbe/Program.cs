using SharpDeps.Index;

using var writer = IndexWriter.Open(args[0], args[1], Guid.Parse(args[2]), isTrusted: true);
Console.WriteLine("writer-ready");
Console.Out.Flush();
// The test owns this process and terminates it to exercise OS lock release after a crash.
_ = Console.ReadLine();
