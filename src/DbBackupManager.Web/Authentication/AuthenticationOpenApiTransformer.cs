using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace DbBackupManager.Web.Authentication;

internal sealed class AuthenticationOpenApiTransformer :
    IOpenApiDocumentTransformer,
    IOpenApiOperationTransformer
{
    private const string CookieSecuritySchemeName = "AdminCookie";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[CookieSecuritySchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = AdminAuthenticationDefaults.CookieName,
            Description = "管理员登录后由同源 Web 主机签发的安全 Cookie。",
        };
        return Task.CompletedTask;
    }

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var requiresAuthorization = metadata.OfType<IAuthorizeData>().Any()
            && !metadata.OfType<IAllowAnonymous>().Any();

        if (requiresAuthorization)
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(
                    CookieSecuritySchemeName,
                    context.Document,
                    externalResource: null)] = [],
            });
        }

        if (IsUnsafeApiOperation(context))
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = AdminAuthenticationDefaults.CsrfHeaderName,
                In = ParameterLocation.Header,
                Required = true,
                Description = "从 GET /api/v1/auth/csrf 获取，并与当前身份和配套 Antiforgery Cookie 一起使用。身份变化后必须重新获取。",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }

        return Task.CompletedTask;
    }

    private static bool IsUnsafeApiOperation(OpenApiOperationTransformerContext context)
    {
        var method = context.Description.HttpMethod;
        var relativePath = context.Description.RelativePath;

        return relativePath is not null
            && relativePath.StartsWith("api/v1/", StringComparison.OrdinalIgnoreCase)
            && method is not null
            && !HttpMethods.IsGet(method)
            && !HttpMethods.IsHead(method)
            && !HttpMethods.IsOptions(method)
            && !HttpMethods.IsTrace(method);
    }
}
