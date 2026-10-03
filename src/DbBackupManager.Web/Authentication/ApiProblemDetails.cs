using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace DbBackupManager.Web.Authentication;

internal static class ApiProblemDetails
{
    public static readonly IReadOnlyList<string> InvalidModelValue = ["invalid"];

    public static ProblemDetails Create(
        HttpContext httpContext,
        int status,
        string code,
        string title)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        return problem;
    }
}
