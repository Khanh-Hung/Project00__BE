using Microsoft.AspNetCore.Mvc;

namespace Application.Abstractions.Responses;

public static class ResultExtensions
{
    public static IActionResult ToActionResult<T>(this Result<T> result)
    {
        var response = new
        {
            Success = result.IsSuccess,
            Data = result.Value,
            Errors = result.Errors
        };

        if (result.IsSuccess)
        {
            if (result.StatusCode.HasValue)
            {
                return new ObjectResult(response)
                {
                    StatusCode = result.StatusCode.Value
                };
            }
            return new OkObjectResult(response);
        }

        if (result.StatusCode.HasValue)
            return new ObjectResult(response)
            {
                StatusCode = result.StatusCode.Value
            };

        if (result.Errors != null && result.Errors.Any(e =>
                e.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)))
            return new UnauthorizedObjectResult(response);

        return new BadRequestObjectResult(response);
    }
}
