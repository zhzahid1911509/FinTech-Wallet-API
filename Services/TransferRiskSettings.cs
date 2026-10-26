namespace FinTechWalletAPI.Services;
public sealed class TransferRiskSettings
{
    public decimal SingleTransferThreshold { get; set; } = 5000m;
    public decimal Rolling24HoursThreshold { get; set; } = 15000m;
    public decimal NewRecipientThreshold { get; set; } = 1000m;
    public int AuthorizationSeconds { get; set; } = 120;
    public bool IsValid() => ValidAmount(SingleTransferThreshold) && ValidAmount(Rolling24HoursThreshold) &&
        ValidAmount(NewRecipientThreshold) && AuthorizationSeconds is >= 30 and <= 300;
    private static bool ValidAmount(decimal amount) => amount >= 0.01m && amount <= MoneyRules.MaximumTransfer && decimal.Round(amount, 2) == amount;
    public string[] Reasons(decimal amount, decimal outgoing, bool knownRecipient, bool recoverySession)
    {
        var reasons = new List<string>();
        if (amount >= SingleTransferThreshold) reasons.Add("large_amount");
        if (outgoing + amount >= Rolling24HoursThreshold) reasons.Add("cumulative_spending");
        if (!knownRecipient && amount >= NewRecipientThreshold) reasons.Add("new_recipient");
        if (recoverySession) reasons.Add("recovery_session");
        return reasons.ToArray();
    }
}
