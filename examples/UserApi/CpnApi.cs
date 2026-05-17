using CSharPN.Core;

namespace UserApi;

// ── ApiChannel ────────────────────────────────────────────────────────────────

/// <summary>
/// A request/response channel modelled as two CPN places.
/// <para>
/// The HTTP handler injects one <typeparamref name="TReq"/> token into
/// <see cref="In"/>; enabled transitions fire until a <typeparamref name="TRes"/>
/// token appears in <see cref="Out"/>, which becomes the HTTP response body.
/// </para>
/// </summary>
public sealed class ApiChannel<TReq, TRes>
    where TReq : notnull, IEquatable<TReq>
    where TRes : notnull, IEquatable<TRes>
{
    /// <summary>The place that receives the incoming HTTP request token.</summary>
    public Place<TReq> In  { get; }
    /// <summary>The place that holds the outgoing HTTP response token.</summary>
    public Place<TRes> Out { get; }

    internal ApiChannel(Place<TReq> request, Place<TRes> response)
    {
        In  = request;
        Out = response;
    }
}

// ── CpnApiHost ────────────────────────────────────────────────────────────────

/// <summary>
/// Maps CPN model channels and places to ASP.NET Core minimal-API endpoints.
/// <para>
/// A semaphore serialises concurrent HTTP requests so each request sees a
/// consistent model state.  After every successful firing the
/// <see cref="ModelChanged"/> event is raised so Blazor pages can refresh.
/// </para>
/// </summary>
public sealed class CpnApiHost
{
    private readonly CpnModel _model;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Raised (on a thread-pool thread) after every API-driven transition firing.</summary>
    public event Action? ModelChanged;

    public CpnApiHost(CpnModel model) => _model = model;

    // ── POST ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps <c>POST <paramref name="route"/></c> so that:
    /// <list type="number">
    ///   <item>the JSON body is deserialised as <typeparamref name="TReq"/> and injected into
    ///     <c>channel.In</c> as a token;</item>
    ///   <item>enabled transitions are fired until a response token appears in
    ///     <c>channel.Out</c>;</item>
    ///   <item>the response token is returned as JSON (HTTP 200).</item>
    /// </list>
    /// Returns HTTP 500 if no transition fires within 50 iterations (unsatisfied guard).
    /// </summary>
    public void MapPost<TReq, TRes>(
        IEndpointRouteBuilder app, string route, ApiChannel<TReq, TRes> channel)
        where TReq : notnull, IEquatable<TReq>
        where TRes : notnull, IEquatable<TRes>
    {
        app.MapPost(route, async (TReq req) =>
        {
            await _lock.WaitAsync();
            try
            {
                channel.In.Enqueue(req);

                // Iteratively fire enabled transitions until the response place
                // has a token.  The model guarantees progress: for every valid
                // request one (and exactly one) transition family handles it.
                for (int i = 0; i < 50; i++)
                {
                    foreach (var t in _model.Transitions)
                    {
                        var bs = t.GetEnabledBindings();
                        if (bs.Count > 0) { t.Fire(bs[0]); break; }
                    }
                    if (channel.Out.TryDequeue(out var res))
                    {
                        ModelChanged?.Invoke();
                        return Results.Ok(res);
                    }
                }

                return Results.Problem(
                    "No transition handled the request. " +
                    "Guard conditions may be unsatisfied.");
            }
            finally { _lock.Release(); }
        });
    }

    // ── GET ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps <c>GET <paramref name="route"/></c> that returns the current marking
    /// of <paramref name="place"/> as a JSON array (one element per token,
    /// repeated for multiplicity).
    /// </summary>
    public void MapGet<T>(IEndpointRouteBuilder app, string route, Place<T> place)
        where T : notnull, IEquatable<T>
    {
        app.MapGet(route, () => Results.Ok(place.Marking.ToList()));
    }
}
