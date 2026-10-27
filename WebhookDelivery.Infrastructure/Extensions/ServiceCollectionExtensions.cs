using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebhookDelivery.Infrastructure.Data;

namespace WebhookDelivery.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddPersistence(IConfiguration config)
        {
            var cs =
                config.GetConnectionString("Default")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:Default is not configured."
                );

            services.AddDbContext<AppDbContext>(o =>
                o.UseNpgsql(
                    cs,
                    npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)
                )
            );

            return services;
        }
    }
}
