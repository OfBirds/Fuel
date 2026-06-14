using Api.Models;

namespace Api.Services;

public interface IProfileService
{
    double CalculateBmr(double weightKg, double heightCm, int age, Sex sex);
    double CalculateBmi(double weightKg, double heightCm);
    (double min, double max) CalculateIdealWeightRange(double heightCm, Constitution constitution);
    double CalculateTdee(double bmr, string activityLevel);
    int CalculateAge(int yearOfBirth);
}
