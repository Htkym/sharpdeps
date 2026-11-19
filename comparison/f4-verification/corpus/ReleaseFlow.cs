namespace F4Fixture;
public static class ReleaseFlow
{
    public static void Run(bool operationFails, bool releaseFails, bool cleanupFails, List<string> trace)
    {
        try
        {
            trace.Add("operation");
            if (operationFails) throw new InvalidOperationException("operation");
        }
        catch
        {
            trace.Add("rollback");
            throw;
        }
        finally
        {
            try
            {
                trace.Add("release");
                if (releaseFails) throw new InvalidOperationException("release");
            }
            catch { trace.Add("release-failed"); }
            trace.Add("cleanup");
            if (cleanupFails) throw new InvalidOperationException("cleanup");
        }
    }
}
