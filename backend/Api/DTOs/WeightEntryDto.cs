namespace Api.DTOs;

public class WeightEntryResponse
{
    public Guid Id { get; set; }
    public double Weight { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public double? DeltaPercent { get; set; }
}

public class CreateWeightEntryRequest
{
    public double Weight { get; set; }
    public DateTime? RecordedAtUtc { get; set; }
}
