using FinTechWalletAPI.Services;
using Xunit;
namespace FinTechWalletAPI.Tests;
public sealed class TransferRiskTests
{
    [Theory]
    [InlineData(4999, 0, true, false, "")]
    [InlineData(5000, 0, true, false, "large_amount")]
    [InlineData(100, 14900, true, false, "cumulative_spending")]
    [InlineData(1000, 0, false, false, "new_recipient")]
    [InlineData(999, 0, false, false, "")]
    [InlineData(1, 0, true, true, "recovery_session")]
    public void PolicyUsesInclusiveBoundaries(decimal amount, decimal outgoing, bool known, bool recovery, string expected)
    { Assert.Equal(expected, string.Join(",", new TransferRiskSettings().Reasons(amount, outgoing, known, recovery))); }
    [Theory]
    [InlineData(0, 120)] [InlineData(-1, 120)] [InlineData(0.001, 120)]
    [InlineData(1000001, 120)] [InlineData(5000, 29)] [InlineData(5000, 301)]
    public void InvalidConfigurationIsRejected(decimal threshold, int seconds)
    { Assert.False(new TransferRiskSettings { SingleTransferThreshold = threshold, AuthorizationSeconds = seconds }.IsValid()); }
    [Fact] public void DefaultsAreValid() => Assert.True(new TransferRiskSettings().IsValid());
}
