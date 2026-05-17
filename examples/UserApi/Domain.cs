namespace UserApi;

// ── Domain tokens (CPN colour sets) ──────────────────────────────────────────

/// <summary>A registered user.  Password is stored in plain text for this demo.</summary>
public sealed record User(string Email, string Password);

/// <summary>An active password-reset token linked to a user's email.</summary>
public sealed record ResetToken(string Email, string Token);

/// <summary>An authenticated login session.</summary>
public sealed record Session(string Email, string Token);

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
