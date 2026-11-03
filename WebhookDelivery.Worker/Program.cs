using WebhookDelivery.Infrastructure.Extensions;
using WebhookDelivery.Infrastructure.Services;
using WebhookDelivery.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddPersistence(builder.Configuration);
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));

var delivery = builder.Configuration.GetSection("Delivery").Get<DeliveryOptions>() ?? new();
delivery.Validate(); // fail fast on a bad lease/timeout combination
builder.Services.Configure<DeliveryOptions>(builder.Configuration.GetSection("Delivery"));

builder.Services.AddScoped<FanOutService>();
builder.Services.AddScoped<DeliveryStore>();
builder
    .Services.AddHttpClient<DeliverySender>(c =>
        c.Timeout = TimeSpan.FromSeconds(delivery.HttpTimeoutSeconds)
    )
    .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // redirects are not followed
            ConnectTimeout = TimeSpan.FromSeconds(5),
        }
    );
builder.Services.AddHostedService<DeliveryWorker>();

await builder.Build().RunAsync();
