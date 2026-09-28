namespace Application;

/// <summary>
/// Same simple name as Domain.Info on purpose: types with the same display name in
/// different projects must stay distinct nodes (SD-008).
/// </summary>
public sealed class Info
{
    public int Number { get; set; }
}
