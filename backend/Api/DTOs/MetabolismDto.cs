namespace Api.DTOs;

public class MetabolismResponse
{
    public double Bmr { get; set; }
    public double Tdee { get; set; }
    public double Bmi { get; set; }
    public double? IdealWeightMin { get; set; }
    public double? IdealWeightMax { get; set; }
    public required string ActivityLevel { get; set; }
}
