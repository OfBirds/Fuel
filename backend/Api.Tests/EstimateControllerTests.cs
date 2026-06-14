using Api.Config;
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
/// EstimateController with a fake <see cref="INutritionEstimator"/> — no live provider
/// calls. Covers item→row mapping, catalogue matching, the AI-off / failure fallbacks,
/// user-cancel propagation, and that refine passes the accumulated notes through.
/// </summary>
public class EstimateControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly FakeNutritionEstimator _estimator = new();
    private readonly AiOptions _options = new() { Enabled = true };
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
        => new(_db, _estimator, _options, NullLogger<EstimateController>.Instance);

    private static EstimatedItem Item(string name, double qty, double cal, double conf = 0.8)
        => new() { Name = name, Quantity = qty, Uom = "g", Calories = cal, Confidence = conf };

    [Fact]
    public async Task EstimateText_MapsItems_AndMatchesExistingFood()
    {
        _estimator.Result = new NutritionEstimate
        {
            OverallConfidence = 0.7,
            Items = { Item("Chicken Breast", 200, 330), Item("Broccoli", 100, 34) },
        };

        var result = await NewController().EstimateText(
            _userId, new EstimateTextRequest { Description = "chicken with broccoli" }, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var resp = Assert.IsType<EstimateResponse>(ok.Value);
        Assert.True(resp.Ok);
        Assert.Equal(0.7, resp.OverallConfidence);
        Assert.Equal(2, resp.Items.Count);

        var chicken = resp.Items[0];
        Assert.Equal(_chickenId, chicken.MatchedFoodId);
        Assert.False(chicken.IsNew);
        Assert.Equal("g", chicken.MatchedDefaultUoM);

        var broccoli = resp.Items[1];
        Assert.Null(broccoli.MatchedFoodId);
        Assert.True(broccoli.IsNew);
    }

    [Fact]
    public async Task EstimateText_MatchIsCaseInsensitive()
    {
        _estimator.Result = new NutritionEstimate { Items = { Item("chicken breast", 100, 165) } };

        var result = await NewController().EstimateText(
            _userId, new EstimateTextRequest { Description = "chicken" }, default);

        var resp = Assert.IsType<EstimateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(_chickenId, resp.Items[0].MatchedFoodId);
        Assert.False(resp.Items[0].IsNew);
    }

    [Fact]
    public async Task EstimateText_AiDisabled_ReturnsUnavailable_WithoutCallingProvider()
    {
        _options.Enabled = false;

        var result = await NewController().EstimateText(
            _userId, new EstimateTextRequest { Description = "anything" }, default);

        var resp = Assert.IsType<EstimateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(resp.Ok);
        Assert.NotNull(resp.Error);
        Assert.Empty(resp.Items);
        Assert.False(_estimator.WasCalled);
    }

    [Fact]
    public async Task EstimateText_ProviderFails_DegradesToManualFallback()
    {
        _estimator.ThrowThis = new AiUnavailableException("boom");

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
        _estimator.CancelOnCall = cts; // simulate the request being aborted mid-call

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            NewController().EstimateText(_userId, new EstimateTextRequest { Description = "x" }, cts.Token));
    }

    [Fact]
    public async Task EstimateText_Refine_PassesAccumulatedNotes()
    {
        _estimator.Result = new NutritionEstimate { Items = { Item("Toast", 1, 80) } };
        var notes = new List<string> { "it's wholemeal", "two slices" };

        await NewController().EstimateText(
            _userId, new EstimateTextRequest { Description = "toast", Notes = notes }, default);

        Assert.Equal(notes, _estimator.LastNotes);
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

    // ── Photo path (Phase 3) — same mapping/fallbacks, source = AiPhoto ──

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

    [Fact]
    public async Task EstimateImage_MapsItems_MatchesFood_AndTagsSourceAiPhoto()
    {
        _estimator.SupportsImagesValue = true;
        _estimator.Result = new NutritionEstimate
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
        Assert.True(_estimator.ImageWasCalled);
    }

    [Fact]
    public async Task EstimateImage_AiDisabled_ReturnsUnavailable_WithoutCallingProvider()
    {
        _options.Enabled = false;
        _estimator.SupportsImagesValue = true;

        var result = await NewController().EstimateImage(_userId, ImageReq(), default);

        var resp = Assert.IsType<EstimateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(resp.Ok);
        Assert.False(_estimator.ImageWasCalled);
    }

    [Fact]
    public async Task EstimateImage_ProviderCannotDoImages_ReturnsUnavailable()
    {
        _estimator.SupportsImagesValue = false;

        var result = await NewController().EstimateImage(_userId, ImageReq(), default);

        var resp = Assert.IsType<EstimateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(resp.Ok);
        Assert.NotNull(resp.Error);
        Assert.False(_estimator.ImageWasCalled);
    }

    [Fact]
    public async Task EstimateImage_NoImage_BadRequest()
    {
        _estimator.SupportsImagesValue = true;

        var result = await NewController().EstimateImage(
            _userId, new EstimateImageRequest { Image = null }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task EstimateImage_NonImageContentType_BadRequest()
    {
        _estimator.SupportsImagesValue = true;

        var result = await NewController().EstimateImage(
            _userId, ImageReq(contentType: "application/pdf"), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task EstimateImage_ProviderFails_DegradesToManualFallback()
    {
        _estimator.SupportsImagesValue = true;
        _estimator.ThrowThis = new AiUnavailableException("boom");

        var result = await NewController().EstimateImage(_userId, ImageReq(), default);

        var resp = Assert.IsType<EstimateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(resp.Ok);
        Assert.Empty(resp.Items);
    }

    [Fact]
    public async Task EstimateImage_Refine_PassesAccumulatedNotes()
    {
        _estimator.SupportsImagesValue = true;
        _estimator.Result = new NutritionEstimate { Items = { Item("Toast", 1, 80) } };
        var notes = new List<string> { "the spread is peanut butter", "two slices" };

        await NewController().EstimateImage(_userId, ImageReq(notes: notes), default);

        Assert.Equal(notes, _estimator.LastNotes);
    }

    private sealed class FakeNutritionEstimator : INutritionEstimator
    {
        public NutritionEstimate Result = new();
        public Exception? ThrowThis;
        public CancellationTokenSource? CancelOnCall;
        public bool WasCalled;
        public bool ImageWasCalled;
        public IReadOnlyList<string>? LastNotes;
        public bool SupportsImagesValue;

        public bool SupportsImages => SupportsImagesValue;

        public Task<NutritionEstimate> EstimateFromTextAsync(
            string description, IReadOnlyList<string> notes, CancellationToken ct)
        {
            WasCalled = true;
            LastNotes = notes;
            if (CancelOnCall is not null)
            {
                CancelOnCall.Cancel();
                throw new OperationCanceledException(CancelOnCall.Token);
            }
            if (ThrowThis is not null) throw ThrowThis;
            return Task.FromResult(Result);
        }

        public Task<NutritionEstimate> EstimateFromImageAsync(
            byte[] image, string contentType, IReadOnlyList<string> notes, CancellationToken ct)
        {
            ImageWasCalled = true;
            LastNotes = notes;
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
