using System.Collections.Immutable;

namespace UserApi;

// ── Domain tokens (CPN colour sets) ──────────────────────────────────────────

/// <summary>A registered user.  Password is stored in plain text for this demo.</summary>
public sealed record User(string Email, string Password);

/// <summary>An active password-reset token linked to a user's email.</summary>
public sealed record ResetToken(string Email, string Token);

/// <summary>An authenticated login session.</summary>
public sealed record Session(string Email, string Token);

// ── Collection tokens ─────────────────────────────────────────────────────────
//
// Users and reset tokens are held as ONE token each rather than one token per row.
//
// The reason is the rule that a guard may only be expressed over variables bound by
// its own input arcs.  "[email free]" and "[no such user]" are statements about the
// *absence* of a row, which no single bound row can witness — the transition has to
// bind the whole collection.  Holding it as one token is what puts that dependency
// on an input arc, where the net can see it.
//
// It also makes the mutual exclusion structural: there is exactly one Users token,
// so two registrations are in genuine conflict and only one can fire in a step.
// With one token per row they had disjoint pre-sets and were formally concurrent,
// and uniqueness of the email rested on the host firing serially — not on the net.

/// <summary>Immutable snapshot of the whole user table, carried as a single CPN token.</summary>
public sealed class UserDb : IEquatable<UserDb>
{
    public static readonly UserDb Empty = new(ImmutableSortedDictionary<string, string>.Empty, 0);

    private readonly ImmutableSortedDictionary<string, string> _passwordByEmail;

    /// <summary>
    /// Order-independent hash, carried forward incrementally instead of recomputed.
    /// <para>
    /// A place's marking is a <c>Multiset</c> keyed by the token, so every consume and
    /// produce hashes this object. Recomputing over the whole table would make each
    /// firing cost O(rows) — the price of holding the table as one token would then
    /// scale with the table.
    /// </para>
    /// </summary>
    private readonly int _hash;

    private UserDb(ImmutableSortedDictionary<string, string> passwordByEmail, int hash)
    {
        _passwordByEmail = passwordByEmail;
        _hash            = hash;
    }

    private static int EntryHash(string email, string password) => HashCode.Combine(email, password);

    public bool Contains(string email) => _passwordByEmail.ContainsKey(email);

    public bool CredentialsMatch(string email, string password)
        => _passwordByEmail.TryGetValue(email, out var p) && p == password;

    /// <summary>Adds the user, or replaces the password when the email is already present.</summary>
    public UserDb With(string email, string password)
    {
        unchecked
        {
            int hash = _hash;
            if (_passwordByEmail.TryGetValue(email, out var existing))
            {
                if (existing == password) return this;
                hash -= EntryHash(email, existing);
            }
            hash += EntryHash(email, password);
            return new UserDb(_passwordByEmail.SetItem(email, password), hash);
        }
    }

    public IEnumerable<User> All => _passwordByEmail.Select(kv => new User(kv.Key, kv.Value));
    public int Count => _passwordByEmail.Count;

    // Structural equality, so two runs that reach the same table reach the same
    // marking — without it, state-space exploration could not fold equivalent states.
    // The dictionary is sorted, so SequenceEqual is a valid content comparison.
    public bool Equals(UserDb? other)
        => other is not null
        && (ReferenceEquals(this, other)
            || (_hash == other._hash
             && _passwordByEmail.Count == other._passwordByEmail.Count
             && _passwordByEmail.SequenceEqual(other._passwordByEmail)));

    public override bool Equals(object? obj) => Equals(obj as UserDb);

    public override int GetHashCode() => _hash;

    public override string ToString() => Render(_passwordByEmail.Keys, Count, "users");

    /// <summary>Compact marking text for the visualizer, e.g. <c>{a@x, b@x, +7}</c>.</summary>
    internal static string Render(IEnumerable<string> labels, int count, string noun)
    {
        if (count == 0) return $"no {noun}";
        var shown = string.Join(", ", labels.Take(3));
        return count <= 3 ? $"{{{shown}}}" : $"{{{shown}, +{count - 3}}}";
    }
}

/// <summary>Immutable snapshot of all outstanding reset tokens, carried as a single CPN token.</summary>
public sealed class ResetDb : IEquatable<ResetDb>
{
    public static readonly ResetDb Empty = new(ImmutableSortedDictionary<string, string>.Empty, 0);

    private readonly ImmutableSortedDictionary<string, string> _emailByToken;

    /// <summary>Order-independent hash, maintained incrementally — see <see cref="UserDb"/>.</summary>
    private readonly int _hash;

    private ResetDb(ImmutableSortedDictionary<string, string> emailByToken, int hash)
    {
        _emailByToken = emailByToken;
        _hash         = hash;
    }

    private static int EntryHash(string token, string email) => HashCode.Combine(token, email);

    /// <summary>The email a reset token was issued for, or null when the token is unknown.</summary>
    public string? EmailFor(string token)
        => _emailByToken.TryGetValue(token, out var email) ? email : null;

    public ResetDb Issue(string email, string token)
    {
        unchecked
        {
            int hash = _hash;
            if (_emailByToken.TryGetValue(token, out var existing))
            {
                if (existing == email) return this;
                hash -= EntryHash(token, existing);
            }
            hash += EntryHash(token, email);
            return new ResetDb(_emailByToken.SetItem(token, email), hash);
        }
    }

    public ResetDb Revoke(string token)
    {
        unchecked
        {
            if (!_emailByToken.TryGetValue(token, out var existing)) return this;
            return new ResetDb(_emailByToken.Remove(token), _hash - EntryHash(token, existing));
        }
    }

    public IEnumerable<ResetToken> All => _emailByToken.Select(kv => new ResetToken(kv.Value, kv.Key));
    public int Count => _emailByToken.Count;

    public bool Equals(ResetDb? other)
        => other is not null
        && (ReferenceEquals(this, other)
            || (_hash == other._hash
             && _emailByToken.Count == other._emailByToken.Count
             && _emailByToken.SequenceEqual(other._emailByToken)));

    public override bool Equals(object? obj) => Equals(obj as ResetDb);

    public override int GetHashCode() => _hash;

    public override string ToString() => UserDb.Render(_emailByToken.Values, Count, "pending resets");
}

// ── HTTP request types ────────────────────────────────────────────────────────

public sealed record RegisterRequest(string Email, string Password);

public sealed record LoginRequest(string Email, string Password);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

// ── HTTP response type ────────────────────────────────────────────────────────

/// <summary>
/// Standard API response envelope.
/// <para>
/// <see cref="Token"/> carries a session or reset token when relevant;
/// it is null for simple success/failure responses.
/// </para>
/// </summary>
public sealed record ApiResult(bool Ok, string Message, string? Token = null);

// ── Correlation ───────────────────────────────────────────────────────────────

/// <summary>
/// Wraps an API request or response token with the id of the HTTP call it belongs to.
/// <para>
/// This is what makes concurrency possible.  With several requests in the net at
/// once, response tokens arrive in whatever order the transitions happen to fire,
/// so the host cannot simply take "the next" token out of an Out place — it has to
/// take <em>its own</em>.  Making that identity part of the token colour is also the
/// honest CPN modelling: the net really does carry several distinct requests.
/// </para>
/// </summary>
public sealed record Envelope<T>(long CorrelationId, T Body) where T : notnull
{
    /// <summary>Compact rendering for place markings in the visualizer.</summary>
    public override string ToString() => $"#{CorrelationId} {Body}";
}
