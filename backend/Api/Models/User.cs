namespace Api.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether this user receives "new version released" emails.</summary>
    public bool NotifyReleases { get; set; } = true;

    /// <summary>Opaque token for one-click unsubscribe links.</summary>
    public Guid UnsubscribeToken { get; set; } = Guid.NewGuid();
}
