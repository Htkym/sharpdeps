namespace F4Fixture;
public static class NestedCleanup
{
    public static void Run(bool operationFails, bool firstCleanupFails, bool secondCleanupFails, List<string> trace)
    {
        try
        {
            trace.Add("operation");
            if (operationFails) throw new InvalidOperationException("operation");
        }
        finally
        {
            try
            {
                trace.Add("first-cleanup");
                if (firstCleanupFails) throw new InvalidOperationException("first-cleanup");
            }
            finally
            {
                trace.Add("second-cleanup");
                if (secondCleanupFails) throw new InvalidOperationException("second-cleanup");
            }
        }
    }
}
