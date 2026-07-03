namespace Api.Models;

/// <summary>
/// A single-use, time-limited token backing the local (backup) "forgot password"
/// flow. Only the SHA-256 <see cref="TokenHash"/> is stored — the raw token lives
/// solely in the emailed reset link, so a database leak can't be replayed to reset
/// anyone's password.
/// </summary>
public class PasswordResetToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Base64 SHA-256 of the raw token. The raw token is never persisted.</summary>
    public required string TokenHash { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Set when the token is redeemed; a consumed token can't be reused.</summary>
    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
