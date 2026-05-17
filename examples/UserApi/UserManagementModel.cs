using CSharPN.Core;

namespace UserApi;

/// <summary>
/// A CPN model for a simple user-management REST service.
///
/// <para><b>Domain places</b> (the "server-side database"):</para>
/// <list type="bullet">
///   <item><see cref="Users"/>      – all registered users</item>
///   <item><see cref="ResetTokens"/> – active password-reset tokens</item>
///   <item><see cref="Sessions"/>    – active login sessions</item>
/// </list>
///
/// <para><b>API channels</b> (one pair of places per endpoint):</para>
/// <list type="bullet">
///   <item><see cref="RegisterCh"/>     – POST /api/register</item>
///   <item><see cref="LoginCh"/>        – POST /api/login</item>
///   <item><see cref="ForgotCh"/>       – POST /api/forgot-password</item>
///   <item><see cref="ResetCh"/>        – POST /api/reset-password</item>
/// </list>
///
/// Each channel has an <c>In</c> place (request token) and an <c>Out</c> place
/// (response token).  The <see cref="CpnApiHost"/> injects request tokens and
/// reads response tokens; the CPN transitions consume/produce them.
///
/// <para>
/// Each endpoint has two transitions: a happy-path transition (guard satisfied)
/// and a fail-path transition (guard negated), so exactly one fires for every
/// incoming request.
/// </para>
/// </summary>
public sealed class UserManagementModel : CpnModel
{
    // ── Domain places ──────────────────────────────────────────────────────────
    public readonly Place<User>       Users;
    public readonly Place<ResetToken> ResetTokens;
    public readonly Place<Session>    Sessions;

    // ── API channels ───────────────────────────────────────────────────────────
    public readonly ApiChannel<RegisterRequest,      ApiResult> RegisterCh;
    public readonly ApiChannel<LoginRequest,         ApiResult> LoginCh;
    public readonly ApiChannel<ForgotPasswordRequest, ApiResult> ForgotCh;
    public readonly ApiChannel<ResetPasswordRequest, ApiResult> ResetCh;

    // Ephemeral field used to share a computed token across two output arcs
    // within a single (serialised, locked) transition firing.
    private string _tok = "";

    public UserManagementModel() : base("User Management")
    {
        // ── Places ─────────────────────────────────────────────────────────────
        Users       = AddPlace<User>      ("Users");
        ResetTokens = AddPlace<ResetToken>("ResetTokens");
        Sessions    = AddPlace<Session>   ("Sessions");

        RegisterCh = AddChannel<RegisterRequest,      ApiResult>("Register");
        LoginCh    = AddChannel<LoginRequest,         ApiResult>("Login");
        ForgotCh   = AddChannel<ForgotPasswordRequest, ApiResult>("ForgotPassword");
        ResetCh    = AddChannel<ResetPasswordRequest,  ApiResult>("ResetPassword");

        // ── Variables ──────────────────────────────────────────────────────────
        var regReq   = new Var<RegisterRequest>      ("req");
        var loginReq = new Var<LoginRequest>          ("req");
        var fpReq    = new Var<ForgotPasswordRequest> ("req");
        var rpReq    = new Var<ResetPasswordRequest>  ("req");
        var user     = new Var<User>                  ("user");
        var reset    = new Var<ResetToken>            ("reset");

        // ── Register ───────────────────────────────────────────────────────────

        AddTransition("Register")
            .Input(RegisterCh.In, regReq)
            .Guard(() => !Users.Marking.Any(u => u.Email == regReq.Val.Email),
                   "[email free]")
            .Output(Users,          () => new User(regReq.Val.Email, regReq.Val.Password))
            .Output(RegisterCh.Out, () => new ApiResult(true, $"User {regReq.Val.Email} registered"))
            .Build();

        AddTransition("RegisterFail")
            .Input(RegisterCh.In, regReq)
            .Guard(() => Users.Marking.Any(u => u.Email == regReq.Val.Email),
                   "[email taken]")
            .Output(RegisterCh.Out, () => new ApiResult(false, "Email already registered"))
            .Build();

        // ── Login ──────────────────────────────────────────────────────────────
        // _tok is pre-computed in the guard so both output arcs share the same value.

        AddTransition("Login")
            .Input(LoginCh.In, loginReq)
            .Guard(() =>
            {
                bool ok = Users.Marking.Any(u =>
                    u.Email == loginReq.Val.Email && u.Password == loginReq.Val.Password);
                if (ok) _tok = NewToken();
                return ok;
            }, "[credentials ok]")
            .Output(Sessions,  () => new Session(loginReq.Val.Email, _tok))
            .Output(LoginCh.Out, () => new ApiResult(true, $"Logged in as {loginReq.Val.Email}", _tok))
            .Build();

        AddTransition("LoginFail")
            .Input(LoginCh.In, loginReq)
            .Guard(() => !Users.Marking.Any(u =>
                        u.Email == loginReq.Val.Email && u.Password == loginReq.Val.Password),
                   "[credentials fail]")
            .Output(LoginCh.Out, () => new ApiResult(false, "Invalid email or password"))
            .Build();

        // ── Forgot Password ────────────────────────────────────────────────────
        // _tok is pre-computed in the guard so token is identical in domain place and response.

        AddTransition("ForgotPassword")
            .Input(ForgotCh.In, fpReq)
            .Guard(() =>
            {
                bool ok = Users.Marking.Any(u => u.Email == fpReq.Val.Email);
                if (ok) _tok = NewToken();
                return ok;
            }, "[user exists]")
            .Output(ResetTokens, () => new ResetToken(fpReq.Val.Email, _tok))
            .Output(ForgotCh.Out, () => new ApiResult(true,
                "(In production this token would arrive by email.)", _tok))
            .Build();

        AddTransition("ForgotPasswordFail")
            .Input(ForgotCh.In, fpReq)
            .Guard(() => !Users.Marking.Any(u => u.Email == fpReq.Val.Email),
                   "[no such user]")
            .Output(ForgotCh.Out, () => new ApiResult(false, "No account with that email"))
            .Build();

        // ── Reset Password ─────────────────────────────────────────────────────
        // Consumes the matching ResetToken + User and produces an updated User.

        AddTransition("ResetPassword")
            .Input(ResetCh.In,  rpReq)
            .Input(ResetTokens, reset)
            .Input(Users,       user)
            .Guard(() => reset.Val.Token == rpReq.Val.Token
                      && user.Val.Email  == reset.Val.Email,
                   "[token+user match]")
            .Output(Users,       () => new User(user.Val.Email, rpReq.Val.NewPassword))
            .Output(ResetCh.Out, () => new ApiResult(true, "Password updated"))
            .Build();

        AddTransition("ResetPasswordFail")
            .Input(ResetCh.In, rpReq)
            .Guard(() => !ResetTokens.Marking.Any(rt => rt.Token == rpReq.Val.Token),
                   "[no such token]")
            .Output(ResetCh.Out, () => new ApiResult(false, "Invalid or expired reset token"))
            .Build();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string NewToken() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Creates a pair of places (In/Out) and wraps them in an <see cref="ApiChannel{TReq,TRes}"/>.
    /// </summary>
    private ApiChannel<TReq, TRes> AddChannel<TReq, TRes>(string name)
        where TReq : notnull, IEquatable<TReq>
        where TRes : notnull, IEquatable<TRes>
        => new(AddPlace<TReq>($"{name}.In"), AddPlace<TRes>($"{name}.Out"));
}
