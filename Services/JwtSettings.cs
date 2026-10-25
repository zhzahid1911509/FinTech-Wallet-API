namespace FinTechWalletAPI.Services;
public sealed class JwtSettings
{
    public string Issuer { get; set; } = "FinTechWalletAPI";
    public string Audience { get; set; } = "FinTechWalletClients";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 10;
    public int SessionDays { get; set; } = 7;
}
