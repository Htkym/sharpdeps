namespace SharpDeps.Query;

/// <summary>One owner for preparation, query and total request deadlines.</summary>
internal sealed class ExplorationExecutionBudget : IDisposable
{
    private readonly TimeProvider time;
    private readonly CancellationToken caller;
    private readonly CancellationToken externalDeadline;
    private readonly CancellationTokenSource totalTimeout;
    private readonly long started;
    private readonly int preparationMilliseconds;
    private readonly int queryMilliseconds;
    private CancellationTokenSource phaseTimeout;
    private CancellationTokenSource phase;
    private long phaseStarted;
    private bool querying;

    internal ExplorationExecutionBudget(ExploreRequest request, CancellationToken cancellationToken,
        CancellationToken deadlineToken = default, TimeProvider? timeProvider = null)
    {
        preparationMilliseconds = request.PrepareMaxMilliseconds;
        queryMilliseconds = request.Budget?.MaxMilliseconds ?? 1000;
        if (!Finite(preparationMilliseconds) || !Finite(queryMilliseconds))
            throw new ArgumentException("Exploration preparation and query deadlines must be finite, between 10 and 30000 ms.", nameof(request));
        time = timeProvider ?? TimeProvider.System;
        caller = cancellationToken;
        externalDeadline = deadlineToken;
        started = phaseStarted = time.GetTimestamp();
        TotalMilliseconds = preparationMilliseconds + queryMilliseconds;
        totalTimeout = new(TimeSpan.FromMilliseconds(TotalMilliseconds), time);
        phaseTimeout = new(TimeSpan.FromMilliseconds(preparationMilliseconds), time);
        phase = Link();
    }

    internal static bool Finite(int milliseconds) => milliseconds is >= 10 and <= 30000;
    internal CancellationToken Token => phase.Token;
    internal CancellationToken CallerToken => caller;
    internal CancellationTokenSource Source => phase;
    internal int TotalMilliseconds { get; }

    internal string Code
    {
        get
        {
            if (caller.IsCancellationRequested) return "CANCELLED";
            if (externalDeadline.IsCancellationRequested || totalTimeout.IsCancellationRequested
                || Elapsed(started) >= TotalMilliseconds) return "REQUEST_TIMEOUT";
            if (phaseTimeout.IsCancellationRequested
                || Elapsed(phaseStarted) >= (querying ? queryMilliseconds : preparationMilliseconds))
                return querying ? "QUERY_TIMEOUT" : "PREPARATION_TIMEOUT";
            return "CANCELLED";
        }
    }

    internal void Check()
    {
        caller.ThrowIfCancellationRequested();
        var limit = querying ? queryMilliseconds : preparationMilliseconds;
        if (externalDeadline.IsCancellationRequested || totalTimeout.IsCancellationRequested
            || Elapsed(started) >= TotalMilliseconds || phaseTimeout.IsCancellationRequested
            || Elapsed(phaseStarted) >= limit)
            throw new OperationCanceledException("The exploration phase or total deadline expired.", Token);
        Token.ThrowIfCancellationRequested();
    }

    internal void StartQuery()
    {
        Check();
        if (querying)
            throw new InvalidOperationException("The query deadline has already started.");
        phaseStarted = time.GetTimestamp();
        querying = true;
        phase.Dispose();
        phaseTimeout.Dispose();
        phaseTimeout = new(TimeSpan.FromMilliseconds(queryMilliseconds), time);
        phase = Link();
        Check();
    }

    private CancellationTokenSource Link()
        => CancellationTokenSource.CreateLinkedTokenSource(caller, externalDeadline, totalTimeout.Token, phaseTimeout.Token);

    private double Elapsed(long timestamp) => time.GetElapsedTime(timestamp).TotalMilliseconds;

    public void Dispose()
    {
        phase.Dispose();
        phaseTimeout.Dispose();
        totalTimeout.Dispose();
    }
}
