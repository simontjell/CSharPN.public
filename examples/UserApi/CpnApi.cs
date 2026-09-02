using System.Collections.Concurrent;
using CSharPN.Core;

namespace UserApi;

// ── ApiChannel ────────────────────────────────────────────────────────────────

/// <summary>
/// A request/response channel modelled as two CPN places.
/// <para>
/// The HTTP handler injects one <see cref="Envelope{T}"/> of
/// <typeparamref name="TReq"/> into <see cref="In"/>; transitions eventually
/// produce an envelope with the same correlation id in <see cref="Out"/>, which
/// becomes that call's HTTP response body.
/// </para>
/// </summary>
public sealed class ApiChannel<TReq, TRes>
    where TReq : notnull
    where TRes : notnull
{
    /// <summary>The place that receives incoming HTTP request tokens.</summary>
    public Place<Envelope<TReq>> In  { get; }
    /// <summary>The place that holds outgoing HTTP response tokens.</summary>
    public Place<Envelope<TRes>> Out { get; }

    internal ApiChannel(Place<Envelope<TReq>> request, Place<Envelope<TRes>> response)
    {
        In  = request;
        Out = response;
    }
}

// ── CpnApiHost ────────────────────────────────────────────────────────────────

/// <summary>
/// Maps CPN model channels and places to ASP.NET Core minimal-API endpoints.
///
/// <para><b>Execution model.</b>  HTTP threads never touch the net.  A request
/// handler only:</para>
/// <list type="number">
///   <item>allocates a correlation id and queues "inject this token" onto the engine;</item>
///   <item>awaits a <see cref="TaskCompletionSource{T}"/> registered under that id;</item>
///   <item>returns when the engine hands it the matching response token.</item>
/// </list>
///
/// <para>A single engine thread owns all firing.  It drains the inbox, fires
/// enabled transitions until the net is quiescent, and completes every waiter
/// whose response has appeared.  Any number of requests can be in the net at the
/// same time and are processed in the same sweep, so throughput is bounded by the
/// firing rate rather than by HTTP round-trip latency.</para>
///
/// <para><b>Why one thread and not N?</b>  Firing is a read-modify-write over
/// shared place markings, so it needs a serialisation point regardless; the
/// engine simply makes that point explicit and microsecond-short instead of
/// holding a lock for a whole request.  True parallel firing would also buy
/// little here: every request transition touches the <c>Users</c> place, so in
/// CPN terms they are in conflict and could not fire concurrently anyway.</para>
/// </summary>
public sealed class CpnApiHost : IAsyncDisposable
{
    private readonly CpnModel _model;
    private readonly ILogger<CpnApiHost> _log;

    // Work queued from HTTP threads, executed on the engine thread.
    private readonly ConcurrentQueue<Action> _inbox  = new();
    private readonly SemaphoreSlim           _wakeup = new(0);

    // In-flight HTTP calls, keyed by correlation id.
    private readonly ConcurrentDictionary<long, TaskCompletionSource<object>> _pending = new();
    private long _nextCorrelationId;

    // One drain action per registered response place. Populated by MapPost before
    // Start(), and only read by the engine thread afterwards.
    private readonly List<Action> _drains = [];

    private readonly CancellationTokenSource _stopping = new();
    private Task? _engine;

    /// <summary>How long a call waits for the net to produce its response before giving up.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Safety bound on firings per engine sweep, in case a transition is permanently enabled.</summary>
    private const int MaxFiringsPerSweep = 100_000;

    /// <summary>
    /// Raised on the engine thread after each sweep that fired at least once,
    /// with the number of firings in that sweep.
    /// </summary>
    public event Action<int>? ModelChanged;

    public CpnApiHost(CpnModel model, ILogger<CpnApiHost> log)
    {
        _model = model;
        _log   = log;
    }

    /// <summary>Starts the engine thread. Call after all endpoints have been mapped.</summary>
    public void Start()
    {
        if (_engine is not null) throw new InvalidOperationException("Engine already started.");
        _engine = Task.Run(EngineLoopAsync);
    }

    // ── Engine ────────────────────────────────────────────────────────────────

    private async Task EngineLoopAsync()
    {
        var ct = _stopping.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await _wakeup.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }

            // Coalesce: everything queued so far is handled by this one sweep.
            while (_wakeup.Wait(0)) { }

            try { Sweep(); }
            catch (Exception ex)
            {
                // Never let the engine die — in-flight calls fall back to their timeout.
                _log.LogError(ex, "CPN engine sweep failed");
            }
        }
    }

    /// <summary>
    /// Injects queued request tokens, then fires enabled transitions until the net
    /// is quiescent, handing each response token to the call that is waiting for it.
    /// </summary>
    private void Sweep()
    {
        int fired = 0;
        bool progress = true;

        while (progress && fired < MaxFiringsPerSweep)
        {
            progress = false;

            lock (_model.SyncRoot)
                while (_inbox.TryDequeue(out var work)) work();

            foreach (var t in _model.Transitions)
            {
                // Drain this transition before moving on, so a batch of pending
                // requests costs one pass rather than one pass each.
                while (fired < MaxFiringsPerSweep)
                {
                    // Enumerate and fire under one lock: the binding is only valid for
                    // the marking it was computed against.  The lock is released between
                    // firings so an interactive visualizer step can interleave.
                    //
                    // Ask for exactly one binding: the guard runs once per candidate, so
                    // enumerating all of them and using the first would make each firing
                    // cost proportional to the number of requests queued behind it.
                    lock (_model.SyncRoot)
                    {
                        var bindings = t.GetEnabledBindings(max: 1);
                        if (bindings.Count == 0) break;
                        t.Fire(bindings[0]);
                    }
                    fired++;
                    progress = true;
                }
            }

            foreach (var drain in _drains) drain();
        }

        if (fired >= MaxFiringsPerSweep)
            _log.LogWarning(
                "CPN engine stopped at the {Max}-firing safety bound — a transition is " +
                "probably permanently enabled.", MaxFiringsPerSweep);

        if (fired > 0) ModelChanged?.Invoke(fired);
    }

    // ── POST ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps <c>POST <paramref name="route"/></c> so that the JSON body enters
    /// <c>channel.In</c> as a correlated token and the matching token from
    /// <c>channel.Out</c> is returned as JSON (HTTP 200).
    /// <para>
    /// Returns HTTP 500 only if the net produced no answer within
    /// <see cref="RequestTimeout"/>, which means the endpoint's guards do not
    /// cover every request — a modelling bug, not a load condition.
    /// </para>
    /// </summary>
    public void MapPost<TReq, TRes>(
        IEndpointRouteBuilder app, string route, ApiChannel<TReq, TRes> channel)
        where TReq : notnull
        where TRes : notnull
    {
        if (_engine is not null)
            throw new InvalidOperationException("Map all endpoints before calling Start().");

        _drains.Add(() =>
        {
            lock (_model.SyncRoot)
                while (channel.Out.TryDequeue(out var envelope))
                    Complete(envelope.CorrelationId, envelope.Body);
        });

        app.MapPost(route, async (TReq body, CancellationToken ct) =>
        {
            var (ok, response) = await SubmitAsync(channel, body, ct);
            return ok
                ? Results.Ok(response)
                : Results.Problem(
                    $"The model produced no response for {route} within " +
                    $"{RequestTimeout.TotalSeconds:0.#}s. Guard conditions may not cover this request.");
        });
    }

    private async Task<(bool Ok, TRes? Response)> SubmitAsync<TReq, TRes>(
        ApiChannel<TReq, TRes> channel, TReq body, CancellationToken ct)
        where TReq : notnull
        where TRes : notnull
    {
        if (_engine is null) throw new InvalidOperationException("Engine not started.");

        var id  = Interlocked.Increment(ref _nextCorrelationId);
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        _inbox.Enqueue(() => channel.In.Enqueue(new Envelope<TReq>(id, body)));
        _wakeup.Release();

        try
        {
            return (true, (TRes)await tcs.Task.WaitAsync(RequestTimeout, ct));
        }
        catch (TimeoutException)
        {
            _log.LogWarning("Request #{Id} on {Place} timed out with no response token.",
                            id, channel.In.Name);
            return (false, default);
        }
        finally
        {
            // No-op on the happy path (the drain already removed it); this covers
            // timeout and client disconnect so the dictionary cannot grow unbounded.
            _pending.TryRemove(id, out _);
        }
    }

    private void Complete(long correlationId, object response)
    {
        if (_pending.TryRemove(correlationId, out var tcs))
            tcs.TrySetResult(response);
        // Otherwise the caller already gave up — the response token is simply dropped.
    }

    // ── GET ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps <c>GET <paramref name="route"/></c> returning the current marking of
    /// <paramref name="place"/> as a JSON array (one element per token).
    /// </summary>
    public void MapGet<T>(IEndpointRouteBuilder app, string route, Place<T> place)
        where T : notnull, IEquatable<T>
    {
        app.MapGet(route, () => Results.Ok(Read(place)));
    }

    /// <summary>
    /// Maps <c>GET <paramref name="route"/></c> for a place whose tokens are
    /// collections, flattening each token through <paramref name="expand"/> so the
    /// endpoint still returns a flat JSON array of rows.
    /// </summary>
    public void MapGet<T, TRow>(
        IEndpointRouteBuilder app, string route, Place<T> place, Func<T, IEnumerable<TRow>> expand)
        where T : notnull, IEquatable<T>
    {
        app.MapGet(route, () => Results.Ok(Read(place, expand)));
    }

    /// <summary>
    /// Takes a snapshot of a place's marking that cannot tear against a concurrent
    /// firing.  Use this instead of reading <c>place.Marking</c> directly.
    /// </summary>
    public List<T> Read<T>(Place<T> place) where T : notnull, IEquatable<T>
    {
        lock (_model.SyncRoot) return place.Marking.ToList();
    }

    /// <summary>Snapshot of a collection-token place, flattened to its rows.</summary>
    public List<TRow> Read<T, TRow>(Place<T> place, Func<T, IEnumerable<TRow>> expand)
        where T : notnull, IEquatable<T>
    {
        lock (_model.SyncRoot) return place.Marking.SelectMany(expand).ToList();
    }

    // ── Shutdown ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_engine is not null)
        {
            try { await _engine; } catch (OperationCanceledException) { }
        }
        _stopping.Dispose();
        _wakeup.Dispose();
    }
}
