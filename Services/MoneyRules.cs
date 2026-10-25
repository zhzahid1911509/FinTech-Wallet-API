using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FinTechWalletAPI.Infrastructure;
namespace FinTechWalletAPI.Services;
public static class MoneyRules
{
    public const decimal MaximumTransfer = 1_000_000m;
    public const decimal MaximumBalance = 99_999_999_999_999_999.99m;
    public static void Validate(decimal amount, string key)
    {
        if (amount <= 0 || amount > MaximumTransfer || decimal.Round(amount, 2) != amount)
            throw new ApiException(400, "invalid_amount", "Amount must be between 0.01 and 1,000,000 BDT with at most two decimal places.");
        if (string.IsNullOrEmpty(key) || key.Length > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ApiException(400, "invalid_idempotency_key", "Supply an Idempotency-Key of 1–128 letters, digits, hyphens or underscores.");
    }
    public static string Fingerprint(Guid recipient, decimal amount) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"transfer:v1|{recipient:D}|BDT|{amount.ToString("F2", CultureInfo.InvariantCulture)}")));
}
