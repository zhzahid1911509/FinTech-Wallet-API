using System.ComponentModel.DataAnnotations;
namespace FinTechWalletAPI.Infrastructure;
public sealed class ValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments.Where(x => x is not null && x.GetType().Namespace == "FinTechWalletAPI.Contracts"))
        {
            var errors = new List<ValidationResult>();
            if (!Validator.TryValidateObject(argument!, new ValidationContext(argument!), errors, true))
                return Results.ValidationProblem(errors.SelectMany(e => e.MemberNames.DefaultIfEmpty("request")
                    .Select(name => new { name, message = e.ErrorMessage ?? "Invalid value." }))
                    .GroupBy(e => e.name).ToDictionary(g => g.Key, g => g.Select(e => e.message).ToArray()));
        }
        return await next(context);
    }
}
