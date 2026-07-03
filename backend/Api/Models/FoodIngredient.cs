namespace Api.Models;

public class FoodIngredient
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ParentFoodId { get; set; }
    public Food ParentFood { get; set; } = null!;

    public Guid ChildFoodId { get; set; }
    public Food ChildFood { get; set; } = null!;

    public double Quantity { get; set; }
    public required string UoM { get; set; }
}
