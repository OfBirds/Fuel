namespace Api.DTOs;

/// <summary>Report produced by a duplicate-food cleanup run.</summary>
public class DedupResult
{
    public string? SnapshotPath { get; set; }
    public int GroupsFound { get; set; }
    public int TotalDuplicates { get; set; }
    public int FoodsDeleted { get; set; }
    public int FoodEntriesRepointed { get; set; }
    public int FoodIngredientsRepointed { get; set; }
    public int UserFoodPrioritiesMerged { get; set; }
    public int SelfReferentialLinksDropped { get; set; }
    public int DuplicateLinksDropped { get; set; }
    public List<DedupGroupDetail> Details { get; set; } = [];
}

public class DedupGroupDetail
{
    public required string NormalizedName { get; set; }
    public Guid SurvivorId { get; set; }
    public required string SurvivorName { get; set; }
    public List<Guid> MergedIds { get; set; } = [];
    public int FoodEntriesRepointed { get; set; }
    public int FoodIngredientsRepointed { get; set; }
    public int UserFoodPrioritiesMerged { get; set; }
    public int SelfReferentialLinksDropped { get; set; }
    public int DuplicateLinksDropped { get; set; }
}
