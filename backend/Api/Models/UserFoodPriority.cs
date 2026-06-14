namespace Api.Models;

/// <summary>Per-user food priority ("ponder") in the shared catalogue.</summary>
public class UserFoodPriority
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid FoodId { get; set; }
    public Food Food { get; set; } = null!;
    /// <summary>Lower = higher priority. Default 100 (represented by absence of a row). Floor 0.</summary>
    public int Ponder { get; set; }
}
