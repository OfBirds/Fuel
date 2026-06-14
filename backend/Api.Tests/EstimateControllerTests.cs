using Api.Controllers;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Tests;

/// <summary>
/// EstimateController with a fake <see cref="IEstimatorChain"/> — no live provider calls.
/// Covers item→row mapping, catalogue matching, the not-configured / failure fallbacks,
/// user-cancel propagation, and that refine passes accumulated notes through. The chain's
/// own ordering/fallback is tested separately.
/// </summary>
public class EstimateControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly FakeEstimatorChain _chain = new();
    private readonly Guid _userId;
    private readonly Guid _chickenId;

    public EstimateControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"estimate_{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);

        var user = new User { Email = "test@example.com", PasswordHash = "hash" };
        var chicken = new Food { Name = "Chicken Breast", DefaultUoM = "g", CaloriesPerUnit = 1.65 };
        _db.Users.Add(user);
        _db.Foods.Add(chicken);
        _db.SaveChanges();
        _userId = user.Id;
        _chickenId = chicken.Id;
    }

    public void Dispose() => _db.Dispose();

    private EstimateController NewController()
        => new(_db, _chain, NullLogger<EstimateController>.Instance);

    private static EstimatedItem Item(string name, double qty, double cal, double conf = 0.8)
        => new() { Name = name, Quantity = qty, Uom = "g", Calories = cal, Confidence = conf };

    private static EstimateImageRequest ImageReq(byte[]? bytes = null, string contentType = "image/jpeg", List<string>? notes = null)
    {
        bytes ??= [1, 2, 3];
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "image", "meal.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
        return new EstimateImageRequest { Image = file, Notes = notes };
    }

    // ── /api/ai/status ──

    [Fact]
    public void Status_ReportsTextAndVisionIndependently()
    {
        _chain.SupportsText = true;
        _chain.SupportsImages = false;

        var ok = Assert.IsType<OkObjectResult>(NewController().AiStatus());
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"supportsText\":true", json);
        Assert.Contains("\"supportsImages\":false", json);
        Assert.Contains("\"enabled\":true", json); // text alone is enough to be "enabled"
    }

    // ── text ──

    [Fact]
    public async Task EstimateText_MapsItems_AndMatchesExistingFood()
    {
        _chain.Result = new NutritionEstimate
        {
            OverallConfidence = 0.7,
            Items = { Item("Chicken Breast", 200, 330), Item("Broccoli", 100, 34) },
        };

        var result = await NewController().EstimateText(
            _userId, new EstimateTextRequest { Description = "chicken with broccoli" }, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var resp = Assert.IsType<EstimateResponse>(ok.Value);
        Assert.True(resp.Ok);
        Assert.Equal("AiText", resp.Source);
        Assert.Equal(2, resp.Items.Count);
        Assert.Equal(_chickenId, resp.Items[0].MatchedFoodId);
        Assert.False(resp.Items[0].IsNew);
        Assert.True(resp.Items[1].IsNew);
    }

    [Fact]
    public async Task EstimateText_MatchIsCaseInsensitive()
    {
        _chain.Result = new NutritionEstimate { Items = { Item("chicken breast", 100, 165) } };

        var result = await NewController().EstimateText(
            _userId, new EstimateTextRequest { Description = "chicken" }, default);

        var resp = Assert.IsType<EstimateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(_chickenId, resp.Items[0].MatchedFoodId);
    }

    [Fact]
    public async Task EstimateText_NotConfigured_ReturnsUnavailable_WithoutCallingChain()
    {
        _chain.SupportsText = false;

        var result = await NewController().EstimateText(
            _userId, new EstimateTextRequest { Description = "anything" }, default);

        var resp = Assert.IsType<EstimateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(resp.Ok);
        Assert.NotNull(resp.Error);
        Assert.Empty(resp.Items);
        Assert.False(_chain.TextCalled);
    }

    [Fact]
    public async Task EstimateText_AllProvidersFail_DegradesToManualFallback()
    {
        _chain.ThrowThis = new AiUnavailableException("all providers failed");

        var result = await NewController().EstimateText(
            _userId, new EstimateTextRequest { Description = "anything" }, default);

        var resp = Assert.IsType<EstimateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(resp.Ok);
        Assert.Empty(resp.Items);
    }

    [Fact]
    public async Task EstimateText_UserCancel_PropagatesInsteadOfFallback()
    {
        var cts = new CancellationTokenSource();
        _chain.CancelOnCall = cts;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            NewController().EstimateText(_userId, new EstimateTextRequest { Description = "x" }, cts.Token));
    }

    [Fact]
    public async Task EstimateText_Refine_PassesAccumulatedNotes()
    {
        _chain.Result = new NutritionEstimate { Items = { Item("Toast", 1, 80) } };
        var notes = new List<string> { "it's wholemeal", "two slices" };

        await NewController().EstimateText(
            _userId, new EstimateTextRequest { Description = "toast", Notes = notes }, default);

        Assert.Equal(notes, _chain.LastNotes);
    }

    [Fact]
    public async Task EstimateText_EmptyDescription_BadRequest()
    {
        var result = await NewController().EstimateText(
            _userId, new EstimateTextRequest { Description = "   " }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task EstimateText_UnknownUser_NotFound()
    {
        var result = await NewController().EstimateText(
            Guid.NewGuid(), new EstimateTextRequest { Description = "x" }, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── photo ──

    [Fact]
    public async Task EstimateImage_MapsItems_AndTagsSourceAiPhoto()
    {
        _chain.Result = new NutritionEstimate
        {
            OverallConfidence = 0.6,
            Items = { Item("Chicken Breast", 200, 330), Item("Broccoli", 100, 34) },
        };

        var result = await NewController().EstimateImage(_userId, ImageReq(), default);

        var resp = Assert.IsType<EstimateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(resp.Ok);
        Assert.Equal("AiPhoto", resp.Source);
        Assert.Equal(2, resp.Items.Count);
        Assert.Equal(_chickenId, resp.Items[0].MatchedFoodId);
        Assert.True(resp.Items[1].IsNew);
        Assert.True(_chain.ImageCalled);
    }

    [Fact]
    public async Task EstimateImage_NotConfigured_ReturnsUnavailable_WithoutCallingChain()
    {
        _chain.SupportsImages = false;

        var result = await NewController().EstimateImage(_userId, ImageReq(), default);

        var resp = Assert.IsType<EstimateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(resp.Ok);
        Assert.NotNull(resp.Error);
        Assert.False(_chain.ImageCalled);
    }

    [Fact]
    public async Task EstimateImage_NoImage_BadRequest()
    {
        var result = await NewController().EstimateImage(
            _userId, new EstimateImageRequest { Image = null }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task EstimateImage_NonImageContentType_BadRequest()
    {
        var result = await NewController().EstimateImage(
            _userId, ImageReq(contentType: "application/pdf"), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task EstimateImage_AllProvidersFail_DegradesToManualFallback()
    {
        _chain.ThrowThis = new AiUnavailableException("boom");

        var result = await NewController().EstimateImage(_userId, ImageReq(), default);

        var resp = Assert.IsType<EstimateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(resp.Ok);
        Assert.Empty(resp.Items);
    }

    [Fact]
    public async Task EstimateImage_Refine_PassesAccumulatedNotes()
    {
        _chain.Result = new NutritionEstimate { Items = { Item("Toast", 1, 80) } };
        var notes = new List<string> { "the spread is peanut butter" };

        await NewController().EstimateImage(_userId, ImageReq(notes: notes), default);

        Assert.Equal(notes, _chain.LastNotes);
    }

    private sealed class FakeEstimatorChain : IEstimatorChain
    {
        public bool SupportsText { get; set; } = true;
        public bool SupportsImages { get; set; } = true;
        public NutritionEstimate Result = new();
        public Exception? ThrowThis;
        public CancellationTokenSource? CancelOnCall;
        public bool TextCalled;
        public bool ImageCalled;
        public IReadOnlyList<string>? LastNotes;

        public Task<NutritionEstimate> EstimateFromTextAsync(
            string description, IReadOnlyList<string> notes, CancellationToken ct)
        {
            TextCalled = true;
            LastNotes = notes;
            return Run();
        }

        public Task<NutritionEstimate> EstimateFromImageAsync(
            byte[] image, string contentType, IReadOnlyList<string> notes, CancellationToken ct)
        {
            ImageCalled = true;
            LastNotes = notes;
            return Run();
        }

        private Task<NutritionEstimate> Run()
        {
            if (CancelOnCall is not null)
            {
                CancelOnCall.Cancel();
                throw new OperationCanceledException(CancelOnCall.Token);
            }
            if (ThrowThis is not null) throw ThrowThis;
            return Task.FromResult(Result);
        }
    }
}
