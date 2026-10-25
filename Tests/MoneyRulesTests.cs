using FinTechWalletAPI.Infrastructure;
using FinTechWalletAPI.Services;
using Xunit;
namespace FinTechWalletAPI.Tests;
public sealed class MoneyRulesTests
{
    [Theory]
    [InlineData("0")][InlineData("-1")][InlineData("0.001")][InlineData("1000000.01")]
    public void RejectsInvalidMoney(string value) => Assert.Throws<ApiException>(() =>
        MoneyRules.Validate(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), "request-1"));
    [Theory]
    [InlineData("0.01")][InlineData("1.10")][InlineData("1000000")]
    public void AcceptsSupportedMoney(string value) => MoneyRules.Validate(decimal.Parse(value,
        System.Globalization.CultureInfo.InvariantCulture), "request-1");
    [Theory]
    [InlineData("")][InlineData("white space")][InlineData("é")][InlineData("a/b")]
    public void RejectsInvalidKeys(string key) => Assert.Throws<ApiException>(() => MoneyRules.Validate(1, key));
    [Fact]
    public void RejectsOversizedKey() => Assert.Throws<ApiException>(() => MoneyRules.Validate(1, new string('a',129)));
    [Fact]
    public void FingerprintIsStableAcrossAmountScaleAndCulture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try {
            var recipient = Guid.NewGuid();
            var first = MoneyRules.Fingerprint(recipient, 1.0m);
            System.Globalization.CultureInfo.CurrentCulture = new("fr-FR");
            Assert.Equal(first, MoneyRules.Fingerprint(recipient, 1.00m));
            Assert.NotEqual(first, MoneyRules.Fingerprint(recipient, 1.01m));
            Assert.NotEqual(first, MoneyRules.Fingerprint(Guid.NewGuid(), 1m));
        } finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    }
}
