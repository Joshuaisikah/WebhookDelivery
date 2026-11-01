using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace WebhookDelivery.Api.DTOs;

public sealed record CreateApplicationRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string? Name { get; init; }
}

public sealed record CreateWebhookRequest
{
    [Required, StringLength(2048), HttpUrl]
    public string? Url { get; init; }
}

public sealed record CreateEventRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string? Type { get; init; }

    [Required]
    public JsonElement? Payload { get; init; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class HttpUrlAttribute : ValidationAttribute
{
    public HttpUrlAttribute()
        : base("The field {0} must be an absolute http or https URL.") { }

    public override bool IsValid(object? value) =>
        value is null
        || (
            value is string s
            && Uri.TryCreate(s, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host)
        );
}
