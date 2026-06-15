namespace Api.Models;

public class Food
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string DefaultUoM { get; set; }
    public double CaloriesPerUnit { get; set; }
    public double? ProteinPerUnit { get; set; }
    public double? CarbsPerUnit { get; set; }
    public double? FatPerUnit { get; set; }
    public string? IconRef { get; set; }

    /// <summary>Product barcode (EAN-13/UPC/etc.) when resolved from Open Food Facts.
    /// Unique so repeat-scans hit the local catalogue cache instantly. Null for
    /// hand-entered / AI-created foods.</summary>
    public string? Barcode { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Foods that contain this food as an ingredient.</summary>
    public List<FoodIngredient> Parents { get; set; } = [];

    /// <summary>Ingredients that make up this food.</summary>
    public List<FoodIngredient> Ingredients { get; set; } = [];
}
