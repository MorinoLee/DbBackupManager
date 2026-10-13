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
        DescribeRecovery(document);
        return Task.CompletedTask;
    }

    private static void DescribeRecovery(OpenApiDocument document)
    {
        if (document.Components?.Schemas is null)
        {
            return;
        }

        if (document.Components.Schemas.TryGetValue("BackupLocationRecoveryResponse", out var location)
            && location is OpenApiSchema locationSchema)
        {
            Describe(locationSchema, "unknown",
                "为真表示该位置必须按不健康显示。可以与 remoteUnrecoverable 同时为真；即使 remoteUnrecoverable 为假，或结论看起来可恢复，也不能显示为健康。");
            Describe(locationSchema, "remoteUnrecoverable",
                "远端存在已分类缺口，或在没有未知项时判定为不可恢复。为假不表示健康；unknown 为真时仍必须按不健康显示。");
        }

        if (document.Components.Schemas.TryGetValue("BackupRecoverySummaryResponse", out var summary)
            && summary is OpenApiSchema summarySchema)
        {
            Describe(summarySchema, "hasCurrentPlan",
                "为假时 locations 为空。没有当前计划的数据库仍由旧策略模型负责，不合成计划恢复窗口。");
            Describe(summarySchema, "locations",
                "仅当 hasCurrentPlan 为真时返回各存储位置的恢复投影。");
        }
    }

    private static void Describe(OpenApiSchema schema, string name, string description)
    {
        if (schema.Properties is not null
            && schema.Properties.TryGetValue(name, out var property)
            && property is OpenApiSchema propertySchema)
        {
            propertySchema.Description = description;
        }
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
