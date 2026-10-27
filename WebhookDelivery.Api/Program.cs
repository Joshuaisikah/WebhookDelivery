using Scalar.AspNetCore;
using WebhookDelivery.Infrastructure.Extensions;

var builder = WebApplication.CreateBuilder(args);

// OpenAPI document generation (built-in since .NET 9, works on .NET 8+ with the package)
builder.Services.AddOpenApi();
builder.Services.AddPersistence(builder.Configuration);
var app = builder.Build();

// Development-only: serve the OpenAPI document + Scalar UI
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // → /openapi/v1.json
    app.MapScalarApiReference(); // → /scalar
}

app.UseHttpsRedirection();

app.Run();
