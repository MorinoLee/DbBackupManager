using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace DbBackupManager.Web.Components.Shared;

internal static class AuthQuery
{
    public static string? Get(NavigationManager navigation, HttpContext? httpContext, string key)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        if (HttpContextAccess.TryGetActive(httpContext, out var active)
            && active.Request.Query.TryGetValue(key, out var requestValues))
        {
            var fromRequest = requestValues.ToString();
            if (!string.IsNullOrWhiteSpace(fromRequest))
            {
                return fromRequest;
            }
        }

        var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        if (!query.TryGetValue(key, out var values))
        {
            return null;
        }

        var value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
