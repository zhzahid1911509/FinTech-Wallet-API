using FinTechWalletAPI.Infrastructure;
namespace FinTechWalletAPI.Services;
public static class HistoryFilters
{
    public static void Validate(DateTimeOffset? from, DateTimeOffset? to, string? kind, string? direction)
    {
        if (from.HasValue && to.HasValue && from >= to)
            throw new ApiException(400, "invalid_date_range", "from must be earlier than to (exclusive).");
        if (kind is not null && kind is not ("Transfer" or "DemoFunding"))
            throw new ApiException(400, "invalid_kind", "kind must be Transfer or DemoFunding.");
        if (direction is not null && direction is not ("incoming" or "outgoing"))
            throw new ApiException(400, "invalid_direction", "direction must be incoming or outgoing.");
    }
}
