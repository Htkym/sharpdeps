namespace SharpDeps.StandardJobOwner;

// Source-only checks. They were not compiled or run when this candidate was prepared.
internal static class PureChecks
{
    internal static void Run()
    {
        void Equal(string expected, string actual)
        {
            if (expected != actual)
                throw new InvalidOperationException("Pure check mismatch.");
        }
        void Reject(Action action)
        {
            try
            {
                action();
            }
            catch (ArgumentException)
            {
                return;
            }
            throw new InvalidOperationException("Expected argument rejection.");
        }
        Equal("\"\"", LaunchPlan.Quote(""));
        Equal("\"a b\"", LaunchPlan.Quote("a b"));
        Equal("\"a\\\\\"", LaunchPlan.Quote("a\\"));
        Equal("\"a\\\"b\"", LaunchPlan.Quote("a\"b"));
        Equal("\"a\\\\\\\"b\"", LaunchPlan.Quote("a\\\"b"));
        Equal("\"日本語\"", LaunchPlan.Quote("日本語"));
        Equal("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"test\" \"a b\"",
            LaunchPlan.CommandLine(@"C:\Program Files\dotnet\dotnet.exe", ["test", "a b"]));
        Equal("A=1\0b=日本語\0\0", LaunchPlan.EnvironmentBlock([new("b", "日本語"), new("A", "1")]));
        Reject(() => LaunchPlan.EnvironmentBlock([new("X", "1"), new("x", "2")]));
        Reject(() => LaunchPlan.EnvironmentBlock([new("X=Y", "1")]));
        Reject(() => LaunchPlan.EnvironmentBlock([new("", "1")]));
        Reject(() => LaunchPlan.Quote("x\0y"));
        LaunchPlan.HashText(new string('A', 64));
        Reject(() => LaunchPlan.HashText("ABC"));
        foreach (string key in new[] { OwnedStop.PathKey, OwnedStop.NonceKey, OwnedStop.PathKey.ToLowerInvariant(), OwnedStop.NonceKey.ToLowerInvariant() })
            Reject(() => OwnedStop.RejectReserved([new(key, "forbidden")]));
        OwnedStop.RejectReserved([new("ORDINARY", "value")]);
        var oldPlan = new LaunchPlan("unused.exe", new string('A', 64), [], "unused", "unused", [], [], null, 1, 5, 1, 1024);
        if (oldPlan.EnableOwnedStop)
            throw new InvalidOperationException("Owned stop must be opt-in.");
        string nonce = new('A', 64);
        byte[] Request(string text) => System.Text.Encoding.UTF8.GetBytes(text);
        string valid = "{\"schema\":\"sharpdeps-owned-stop-v1\",\"nonce\":\"" + nonce + "\",\"reason\":\"child-uncertain\"}";
        Equal("child-uncertain", OwnedStop.Parse(Request(valid), nonce));
        Equal("cancelled", OwnedStop.Parse(Request(valid.Replace("child-uncertain", "cancelled")), nonce));
        foreach (string invalid in new[]
        {
            "{}", "[]", "not-json", valid.Replace(nonce, new string('B', 64)),
            valid.Replace("child-uncertain", "free text"), valid.Replace("\"reason\":", "\"pid\":7,\"reason\":"),
            valid.Replace("\"reason\":", "\"reason\":\"cancelled\",\"reason\":"),
            valid.Replace("sharpdeps-owned-stop-v1", "other-schema"), new string('x', OwnedStop.MaxRequestBytes + 1)
        })
        {
            try
            {
                OwnedStop.Parse(Request(invalid), nonce);
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
            {
                continue;
            }
            throw new InvalidOperationException("Expected strict owned-stop request rejection.");
        }
        using (var active = new CancellationTokenSource())
        {
            Program.RequestCancellation(active);
            if (!active.IsCancellationRequested)
                throw new InvalidOperationException("Active cancellation was not requested.");
        }
        var disposed = new CancellationTokenSource();
        disposed.Dispose();
        Program.RequestCancellation(disposed);
        Console.WriteLine("Pure quoting/environment/hash/cancellation/owned-stop source checks passed; no child or Job calls.");
    }
}