namespace SharpDeps.Analysis.Contracts.Harness;

public enum HarnessOperation { ReadSaved, Index, Update, Watch, Restore, WriteStore }

/// <summary>A permission gate, not an OS sandbox. Existing Quick/Semantic trust gates remain in force.</summary>
public static class HarnessTrustPolicy
{
    public static bool Allows(HarnessOperation operation, bool isTrusted)
        => Enum.IsDefined(operation) && (operation == HarnessOperation.ReadSaved || isTrusted);
}
