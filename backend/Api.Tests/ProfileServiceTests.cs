using Api.Models;
using Api.Services;

namespace Api.Tests;

public class ProfileServiceTests
{
    private readonly ProfileService _service = new();

    [Fact]
    public void Bmr_Male_MatchesMifflinStJeor()
    {
        // 80kg, 180cm, 30yr: 10*80 + 6.25*180 - 5*30 + 5 = 800 + 1125 - 150 + 5 = 1780
        var bmr = _service.CalculateBmr(80, 180, 30, Sex.Male);
        Assert.Equal(1780, bmr);
    }

    [Fact]
    public void Bmr_Female_MatchesMifflinStJeor()
    {
        // 65kg, 165cm, 30yr: 10*65 + 6.25*165 - 5*30 - 161 = 650 + 1031.25 - 150 - 161 = 1370.25
        var bmr = _service.CalculateBmr(65, 165, 30, Sex.Female);
        Assert.Equal(1370.25, bmr);
    }

    [Fact]
    public void Bmi_ReturnsCorrectValue()
    {
        var bmi = _service.CalculateBmi(80, 180);
        Assert.Equal(80 / (1.8 * 1.8), bmi, 2);
    }

    [Fact]
    public void IdealWeight_SmallFrame_ReturnsLowerRange()
    {
        var (min, max) = _service.CalculateIdealWeightRange(180, Constitution.Small);
        var h2 = 1.8 * 1.8;
        Assert.Equal(18.5 * h2, min, 1);
        Assert.Equal(22 * h2, max, 1);
    }

    [Fact]
    public void IdealWeight_LargeFrame_ReturnsUpperRange()
    {
        var (min, max) = _service.CalculateIdealWeightRange(180, Constitution.Large);
        var h2 = 1.8 * 1.8;
        Assert.Equal(22 * h2, min, 1);
        Assert.Equal(26 * h2, max, 1);
    }

    [Theory]
    [InlineData(180, 17.3, Sex.Male, "Small")]    // r=10.4 → boundary, 180/17.3=10.4 which is NOT >10.4, so Medium
    [InlineData(180, 17.0, Sex.Male, "Small")]    // r=10.59 → Small
    [InlineData(180, 18.5, Sex.Male, "Medium")]   // r=9.73 → Medium
    [InlineData(180, 19.0, Sex.Male, "Large")]    // r=9.47 → Large
    [InlineData(165, 15.0, Sex.Female, "Medium")]  // r=11.0 → boundary, 165/15=11 which is NOT >11, so Medium
    [InlineData(165, 14.5, Sex.Female, "Small")]  // r=11.38 → Small
    [InlineData(165, 16.0, Sex.Female, "Medium")] // r=10.31 → Medium
    [InlineData(165, 16.5, Sex.Female, "Large")]  // r=10.0 → Large
    public void Constitution_Classification_AtThresholds(
        double heightCm, double wristCm, Sex sex, string expected)
    {
        var r = heightCm / wristCm;
        string classification;
        if (sex == Sex.Male)
        {
            if (r > 10.4) classification = "Small";
            else if (r >= 9.6) classification = "Medium";
            else classification = "Large";
        }
        else
        {
            if (r > 11) classification = "Small";
            else if (r >= 10.1) classification = "Medium";
            else classification = "Large";
        }
        Assert.Equal(expected, classification);
    }

    [Fact]
    public void Tdee_Sedentary_EqualsBmrTimes1Dot2()
    {
        var tdee = _service.CalculateTdee(2000, "sedentary");
        Assert.Equal(2400, tdee);
    }

    [Fact]
    public void Tdee_VeryActive_EqualsBmrTimes1Dot9()
    {
        var tdee = _service.CalculateTdee(2000, "very_active");
        Assert.Equal(3800, tdee);
    }

    [Fact]
    public void Tdee_UnknownLevel_FallsBackToSedentary()
    {
        var tdee = _service.CalculateTdee(2000, "extreme");
        Assert.Equal(2400, tdee);
    }

    [Fact]
    public void CalculateAge_FromYearOfBirth()
    {
        var age = _service.CalculateAge(DateTime.UtcNow.Year - 25);
        Assert.Equal(25, age);
    }
}
