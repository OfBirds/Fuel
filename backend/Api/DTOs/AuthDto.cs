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

public class ResetPasswordRequest
{
    public required string Email { get; set; }
    public required string NewPassword { get; set; }
}

public class UserResponse
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
}
