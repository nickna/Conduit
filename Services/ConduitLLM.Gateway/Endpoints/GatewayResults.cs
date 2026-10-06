using ConduitLLM.Core.Models;
using System.Text.Json;
using ConduitLLM.Gateway.Serialization;

namespace ConduitLLM.Gateway.Endpoints;

/// <summary>OpenAI-compatible explicit results shared by Gateway endpoint groups.</summary>
public static class GatewayResults
{
    /// <summary>Maps a virtual-key validation status code to the OpenAI error envelope type.</summary>
    public static string OpenAIErrorTypeFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status401Unauthorized => "authentication_error",
        StatusCodes.Status402PaymentRequired => "billing_error",
        StatusCodes.Status403Forbidden => "permission_error",
        _ => "server_error"
    };


    public static IResult OpenAIError(
        int statusCode,
        string message,
        string code,
        string type = "invalid_request_error",
        string? param = null,
        JsonElement? metadata = null) =>
        Results.Json(
            new OpenAIErrorResponse
            {
                Error = new OpenAIError
                {
                    Message = message,
                    Type = type,
                    Code = code,
                    Param = param,
                    Metadata = metadata
                }
            },
            GatewayHttpJsonContext.Default.OpenAIErrorResponse,
            statusCode: statusCode);
}
