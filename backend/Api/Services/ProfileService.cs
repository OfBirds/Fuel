using Api.Models;

namespace Api.Services;

public class ProfileService : IProfileService
{
    public double CalculateBmr(double weightKg, double heightCm, int age, Sex sex)
    {
        var bmr = 10 * weightKg + 6.25 * heightCm - 5 * age;
        return sex == Sex.Male ? bmr + 5 : bmr - 161;
    }

    public double CalculateBmi(double weightKg, double heightCm)
    {
        var heightM = heightCm / 100;
        return weightKg / (heightM * heightM);
    }

    public (double min, double max) CalculateIdealWeightRange(double heightCm, Constitution constitution)
    {
        var h = heightCm / 100;
        var h2 = h * h;

        return constitution switch
        {
            Constitution.Small => (18.5 * h2, 22 * h2),
            Constitution.Medium => (20 * h2, 24 * h2),
            Constitution.Large => (22 * h2, 26 * h2),
            _ => (18.5 * h2, 25 * h2),
        };
    }

    public double CalculateTdee(double bmr, string activityLevel)
    {
        var factor = (activityLevel?.ToLowerInvariant().Trim()) switch
        {
            "sedentary" => 1.2,
            "light" => 1.375,
            "moderate" => 1.55,
            "active" => 1.725,
            "very_active" => 1.9,
            _ => 1.2,
        };
        return bmr * factor;
    }

    public int CalculateAge(int yearOfBirth)
    {
        return DateTime.UtcNow.Year - yearOfBirth;
    }
}
