using FinTechWalletAPI.Services;
using FinTechWalletAPI.Infrastructure;
using Xunit;
namespace FinTechWalletAPI.Tests;
public sealed class MonitoringTests
{
    [Fact] public void DefaultsAreValid() => Assert.True(new ReconciliationSettings().IsValid());
    [Theory]
    [InlineData(59, 60, 3600, 15)] [InlineData(86401, 60, 172800, 15)]
    [InlineData(900, 4, 3600, 15)] [InlineData(900, 301, 3600, 15)]
    [InlineData(900, 60, 989, 15)] [InlineData(900, 60, 172801, 15)]
    [InlineData(900, 60, 3600, 0)] [InlineData(900, 60, 3600, 1441)]
    public void InvalidOptionsFail(int interval, int timeout, int stale, int backlog) =>
        Assert.False(new ReconciliationSettings { IntervalSeconds = interval, TimeoutSeconds = timeout, StaleAfterSeconds = stale, EmailBacklogMinutes = backlog }.IsValid());
    [Theory] [InlineData(0,20)] [InlineData(1,101)] [InlineData(100001,20)]
    public void PaginationIsBounded(int page, int size) => Assert.Throws<ApiException>(() => OperationsMonitoring.ValidatePage(page,size));
}
