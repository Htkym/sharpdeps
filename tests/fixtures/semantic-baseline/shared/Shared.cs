namespace Infrastructure;

/// <summary>Linked into the Infrastructure project from outside its directory.</summary>
public static class SharedLog
{
    public static void Write(string message)
    {
        System.Console.WriteLine(message);
    }
}
