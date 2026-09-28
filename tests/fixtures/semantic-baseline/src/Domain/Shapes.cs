namespace Domain;

/// <summary>Nested, generic, file-local, and non-class kinds for the symbol index.</summary>
public static class Shapes
{
    public sealed class Inner
    {
        public string Name { get; set; } = string.Empty;
    }
}

public sealed class GenericThing<T>
{
    public T? Value { get; set; }
}

public enum Kind
{
    First,
    Second
}

public delegate void Notifier(string message);

public sealed record Recorded(string Name);

file sealed class FileLocalThing
{
    public static string Describe() => "only visible in this file";
}
