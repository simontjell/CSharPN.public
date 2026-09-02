using System.Security.Cryptography;
using CSharPN.Core;

namespace UserApi;

/// <summary>
/// A CPN model for a simple user-management REST service.
///
/// <para><b>Domain places</b> (the "server-side database"):</para>
/// <list type="bullet">
///   <item><see cref="Users"/>       – the whole user table, as one <see cref="UserDb"/> token</item>
///   <item><see cref="ResetTokens"/> – all outstanding resets, as one <see cref="ResetDb"/> token</item>
///   <item><see cref="Sessions"/>    – active login sessions, one token each</item>
///   <item><see cref="TokenSeq"/>    – counter; the only source of fresh token identity</item>
/// </list>
///
/// <para><b>API channels</b> (one pair of places per endpoint):</para>
/// <list type="bullet">
///   <item><see cref="RegisterCh"/> – POST /api/register</item>
///   <item><see cref="LoginCh"/>    – POST /api/login</item>
///   <item><see cref="ForgotCh"/>   – POST /api/forgot-password</item>
///   <item><see cref="ResetCh"/>    – POST /api/reset-password</item>
/// </list>
///
/// Each channel has an <c>In</c> place (request token) and an <c>Out</c> place
/// (response token); tokens are wrapped in an <see cref="Envelope{T}"/> carrying the
/// correlation id of the HTTP call, so many requests can be in flight at once.
///
/// <para><b>Guards are expressions over bound variables only.</b>  No guard here
/// reads a place — every value a guard tests arrives through an input arc.  That is
/// why <see cref="Users"/> and <see cref="ResetTokens"/> are single collection
/// tokens: a condition such as "no user has this email" is about the absence of a
/// row, which no individually bound row can witness.  A transition that only needs
/// to *read* a collection consumes the token and puts the same one straight back.
/// The rule is checked when each transition is built, so a regression fails at the
/// call site that wrote it rather than at some later firing.</para>
///
/// <para><b>Guards are also pure.</b>  They assign nothing.  A guard is evaluated
/// once per candidate binding during enumeration, including for candidates that are
/// never fired, so a guard with a side effect leaks values between bindings.
/// Anything a firing must <em>invent</em> — here a fresh token — is derived from the
/// binding instead, via the <see cref="TokenSeq"/> counter.</para>
///
/// <para>Each endpoint has two transitions whose guards are exact complements over
/// the same predicate, so every well-formed request is answered by exactly one of
/// them and no request can get stuck in an <c>In</c> place.</para>
/// </summary>
public sealed class UserManagementModel : CpnModel
{
    // ── Domain places ──────────────────────────────────────────────────────────

    /// <summary>The user table. Exactly one token — see <see cref="UserDb"/> for why.</summary>
    public readonly Place<UserDb>  Users;

    /// <summary>Outstanding password resets. Exactly one token.</summary>
    public readonly Place<ResetDb> ResetTokens;

    /// <summary>One token per session. No guard inspects it, so it needs no collection token.</summary>
    public readonly Place<Session> Sessions;

    /// <summary>
    /// Fresh-identifier counter — the classic CPN idiom for name generation.
    /// A transition that needs a new token consumes the counter, derives the token
    /// from it, and puts back <c>seq + 1</c>.  Holding it as a single token makes
    /// uniqueness structural: two firings cannot draw the same value.
    /// </summary>
    public readonly Place<long> TokenSeq;

    // ── API channels ───────────────────────────────────────────────────────────
    public readonly ApiChannel<RegisterRequest,       ApiResult> RegisterCh;
    public readonly ApiChannel<LoginRequest,          ApiResult> LoginCh;
    public readonly ApiChannel<ForgotPasswordRequest, ApiResult> ForgotCh;
    public readonly ApiChannel<ResetPasswordRequest,  ApiResult> ResetCh;

    /// <summary>
    /// Chosen once at construction and never mutated, so a firing stays a pure
    /// function of its binding.  It only makes <see cref="Mint"/> unguessable from
    /// the outside; the CPN-visible state is the plain counter.  Replace
    /// <see cref="Mint"/> with the identity function for reproducible tokens when
    /// exploring the state space.
    /// </summary>
    private readonly byte[] _tokenSecret = RandomNumberGenerator.GetBytes(32);

    public UserManagementModel() : base("User Management")
    {
        // ── Places ─────────────────────────────────────────────────────────────
        Users       = AddPlace("Users",       UserDb.Empty);
        ResetTokens = AddPlace("ResetTokens", ResetDb.Empty);
        Sessions    = AddPlace<Session>("Sessions");
        TokenSeq    = AddPlace("TokenSeq",    0L);

        RegisterCh = AddChannel<RegisterRequest,       ApiResult>("Register");
        LoginCh    = AddChannel<LoginRequest,          ApiResult>("Login");
        ForgotCh   = AddChannel<ForgotPasswordRequest, ApiResult>("ForgotPassword");
        ResetCh    = AddChannel<ResetPasswordRequest,  ApiResult>("ResetPassword");

        // ── Variables ──────────────────────────────────────────────────────────
        var regReq   = new Var<Envelope<RegisterRequest>>      ("regReq");
        var loginReq = new Var<Envelope<LoginRequest>>         ("loginReq");
        var fpReq    = new Var<Envelope<ForgotPasswordRequest>>("fpReq");
        var rpReq    = new Var<Envelope<ResetPasswordRequest>> ("rpReq");
        var users    = new Var<UserDb>                         ("users");
        var resets   = new Var<ResetDb>                        ("resets");
        var seq      = new Var<long>                           ("seq");

        // ── Register ───────────────────────────────────────────────────────────

        AddTransition("Register")
            .Input(RegisterCh.In, regReq)
            .Input(Users,         users)
            .Guard(() => !users.Val.Contains(regReq.Val.Body.Email), "[email free]")
            .Output(Users, () => users.Val.With(regReq.Val.Body.Email, regReq.Val.Body.Password),
                           "users + req.user")
            .Output(RegisterCh.Out, () => Reply(regReq, true, $"User {regReq.Val.Body.Email} registered"),
                                    "(req.id, ok)")
            .Build();

        // Reads the table without changing it: consume the token, put the same one
        // back.  That is what makes "I depend on Users" visible as an arc.
        AddTransition("RegisterFail")
            .Input(RegisterCh.In, regReq)
            .Input(Users,         users)
            .Guard(() => users.Val.Contains(regReq.Val.Body.Email), "[email taken]")
            .Output(Users, users)
            .Output(RegisterCh.Out, () => Reply(regReq, false, "Email already registered"),
                                    "(req.id, fail)")
            .Build();

        // ── Login ──────────────────────────────────────────────────────────────
        // Mints a session token, so it consumes and advances the counter.  Both
        // output arcs call Mint(seq) and get the same value, because Mint is a pure
        // function of the bound counter — no shared scratch field involved.

        AddTransition("Login")
            .Input(LoginCh.In, loginReq)
            .Input(Users,      users)
            .Input(TokenSeq,   seq)
            .Guard(() => users.Val.CredentialsMatch(loginReq.Val.Body.Email, loginReq.Val.Body.Password),
                   "[credentials ok]")
            .Output(Users,    users)
            .Output(Sessions, () => new Session(loginReq.Val.Body.Email, Mint(seq.Val)),
                              "(req.email, mint seq)")
            .Output(LoginCh.Out, () => Reply(loginReq, true,
                                         $"Logged in as {loginReq.Val.Body.Email}", Mint(seq.Val)),
                                 "(req.id, ok, mint seq)")
            .Output(TokenSeq, () => seq.Val + 1)
            .Build();

        // The failing path mints nothing, so it must not take the counter —
        // otherwise bad credentials would contend for it with real logins.
        AddTransition("LoginFail")
            .Input(LoginCh.In, loginReq)
            .Input(Users,      users)
            .Guard(() => !users.Val.CredentialsMatch(loginReq.Val.Body.Email, loginReq.Val.Body.Password),
                   "[credentials fail]")
            .Output(Users, users)
            .Output(LoginCh.Out, () => Reply(loginReq, false, "Invalid email or password"),
                                 "(req.id, fail)")
            .Build();

        // ── Forgot Password ────────────────────────────────────────────────────

        AddTransition("ForgotPassword")
            .Input(ForgotCh.In, fpReq)
            .Input(Users,       users)
            .Input(ResetTokens, resets)
            .Input(TokenSeq,    seq)
            .Guard(() => users.Val.Contains(fpReq.Val.Body.Email), "[user exists]")
            .Output(Users,       users)
            .Output(ResetTokens, () => resets.Val.Issue(fpReq.Val.Body.Email, Mint(seq.Val)),
                                 "issue(req.email, mint seq)")
            .Output(ForgotCh.Out, () => Reply(fpReq, true,
                                          "(In production this token would arrive by email.)",
                                          Mint(seq.Val)),
                                  "(req.id, ok, mint seq)")
            .Output(TokenSeq,    () => seq.Val + 1)
            .Build();

        AddTransition("ForgotPasswordFail")
            .Input(ForgotCh.In, fpReq)
            .Input(Users,       users)
            .Guard(() => !users.Val.Contains(fpReq.Val.Body.Email), "[no such user]")
            .Output(Users, users)
            .Output(ForgotCh.Out, () => Reply(fpReq, false, "No account with that email"),
                                  "(req.id, fail)")
            .Build();

        // ── Reset Password ─────────────────────────────────────────────────────
        // Both transitions bind the same three tokens; only the guard differs, and
        // the two guards are literally P and !P over the same predicate.

        AddTransition("ResetPassword")
            .Input(ResetCh.In,  rpReq)
            .Input(ResetTokens, resets)
            .Input(Users,       users)
            .Guard(() => CanReset(resets.Val, users.Val, rpReq.Val.Body.Token),
                   "[token matches a user]")
            .Output(ResetTokens, () => resets.Val.Revoke(rpReq.Val.Body.Token),
                                 "resets - req.token")
            .Output(Users, () => users.Val.With(resets.Val.EmailFor(rpReq.Val.Body.Token)!,
                                                rpReq.Val.Body.NewPassword),
                           "users with new password")
            .Output(ResetCh.Out, () => Reply(rpReq, true, "Password updated"), "(req.id, ok)")
            .Build();

        AddTransition("ResetPasswordFail")
            .Input(ResetCh.In,  rpReq)
            .Input(ResetTokens, resets)
            .Input(Users,       users)
            .Guard(() => !CanReset(resets.Val, users.Val, rpReq.Val.Body.Token),
                   "[no matching token]")
            .Output(ResetTokens, resets)
            .Output(Users,       users)
            .Output(ResetCh.Out, () => Reply(rpReq, false, "Invalid or expired reset token"),
                                 "(req.id, fail)")
            .Build();
    }

    // ── Guard predicates ───────────────────────────────────────────────────────
    // Static and taking plain values, so they cannot reach a place even by accident.
    // The happy and failing transition of an endpoint share one predicate, so they
    // cannot drift out of being complements.

    private static bool CanReset(ResetDb resets, UserDb users, string token)
        => resets.EmailFor(token) is { } email && users.Contains(email);

    // ── Output helpers ─────────────────────────────────────────────────────────

    /// <summary>Builds a response token carrying the request's correlation id.</summary>
    private static Envelope<ApiResult> Reply<TReq>(
        Var<Envelope<TReq>> request, bool ok, string message, string? token = null)
        where TReq : notnull
        => new(request.Val.CorrelationId, new ApiResult(ok, message, token));

    /// <summary>
    /// Derives an opaque token from the counter value.  Deterministic given the
    /// binding — the same <paramref name="seq"/> always yields the same token, which
    /// is what lets two output arcs of one transition agree on it.
    /// </summary>
    private string Mint(long seq) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_tokenSecret, BitConverter.GetBytes(seq)))[..24];

    /// <summary>
    /// Creates a pair of places (In/Out) and wraps them in an <see cref="ApiChannel{TReq,TRes}"/>.
    /// </summary>
    private ApiChannel<TReq, TRes> AddChannel<TReq, TRes>(string name)
        where TReq : notnull
        where TRes : notnull
        => new(AddPlace<Envelope<TReq>>($"{name}.In"), AddPlace<Envelope<TRes>>($"{name}.Out"));
}
