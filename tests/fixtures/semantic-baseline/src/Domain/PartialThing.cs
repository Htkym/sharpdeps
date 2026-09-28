namespace Domain;

/// <summary>Declared in two files on purpose: partial declarations merge into one type.</summary>
public sealed partial class PartialThing
{
    public string First { get; set; } = string.Empty;
}
