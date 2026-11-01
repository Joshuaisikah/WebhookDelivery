namespace WebhookDelivery.Api.DTOs;

public sealed record ApplicationResponse(Guid Id, string Name, DateTimeOffset CreatedAt);

public sealed record WebhookResponse(
    Guid Id,
    Guid ApplicationId,
    string Url,
    string? Secret,
    bool IsActive,
    DateTimeOffset CreatedAt
);

public sealed record EventAcceptedResponse(
    Guid Id,
    Guid ApplicationId,
    string Type,
    DateTimeOffset CreatedAt
);
