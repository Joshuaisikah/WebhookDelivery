using System.Text.Json;
using WebhookDelivery.Api.DTOs;
using WebhookDelivery.Infrastructure.Services;

namespace WebhookDelivery.Api.Endpoints;

public static class ApplicationEndpoints
{
    private const int MaxPayloadChars = 256 * 1024;

    public static IEndpointRouteBuilder MapApplicationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/applications").WithTags("Applications");

        group.MapPost("/", CreateApplication);
        group.MapPost("/{id:guid}/webhooks", CreateWebhook);
        group.MapPost("/{id:guid}/events", CreateEvent);

        return app;
    }

    private static async Task<IResult> CreateApplication(
        CreateApplicationRequest req,
        IApplicationService svc,
        CancellationToken ct
    )
    {
        var a = await svc.CreateAsync(req.Name!, ct);
        return Results.Created(
            $"/api/applications/{a.Id}",
            new ApplicationResponse(a.Id, a.Name, a.CreatedAt)
        );
    }

    private static async Task<IResult> CreateWebhook(
        Guid id,
        CreateWebhookRequest req,
        IWebhookService svc,
        CancellationToken ct
    )
    {
        var w = await svc.CreateAsync(id, req.Url!, ct);
        return w is null
            ? Results.Problem(statusCode: 404, title: "Application not found")
            : Results.Created(
                $"/api/applications/{id}/webhooks/{w.Id}",
                new WebhookResponse(w.Id, w.ApplicationId, w.Url, w.Secret, w.IsActive, w.CreatedAt)
            );
    }

    private static async Task<IResult> CreateEvent(
        Guid id,
        CreateEventRequest req,
        IEventService svc,
        CancellationToken ct
    )
    {
        var payload = req.Payload!.Value;
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["payload"] = ["Payload must be a JSON value."] }
            );

        var raw = payload.GetRawText();
        if (raw.Length > MaxPayloadChars)
            return Results.Problem(statusCode: 413, title: "Payload too large");

        var ev = await svc.CreateAsync(id, req.Type!, raw, ct);
        return ev is null
            ? Results.Problem(statusCode: 404, title: "Application not found")
            : Results.Json(
                new EventAcceptedResponse(ev.Id, ev.ApplicationId, ev.Type, ev.CreatedAt),
                statusCode: StatusCodes.Status202Accepted
            );
    }
}
