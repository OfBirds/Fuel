namespace Api.Models;

public class WeightEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public double Weight { get; set; }
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
}
