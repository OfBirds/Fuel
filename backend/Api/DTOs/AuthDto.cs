namespace Api.DTOs;

public class RegisterRequest
{
    public required string Email { get; set; }
    public required string Password { get; set; }
}

public class LoginRequest
{
    public required string Email { get; set; }
    public required string Password { get; set; }
}

public class AuthResponse
{
    public Guid UserId { get; set; }
    public required string Email { get; set; }
    public required string Token { get; set; }

    /// <summary>Current release-email opt-in, so the client has prefs without an extra round-trip.</summary>
    public bool NotifyReleases { get; set; }
}

/// <summary>Step 1 of the local password reset: ask for a reset link by email.</summary>
public class RequestPasswordResetRequest
{
    public required string Email { get; set; }
}

/// <summary>Step 2: redeem the single-use token from the emailed link and set a new password.</summary>
public class ResetPasswordRequest
{
    public required string Token { get; set; }
    public required string NewPassword { get; set; }
}

public class UserResponse
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
}
