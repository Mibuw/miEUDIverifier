using miEUDIverifier.Configuration;
using miEUDIverifier.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace miEUDIverifier;

/// <summary>
/// Extension methods for easy integration into any ASP.NET Core
/// or generic-host server.
///
/// Usage in your own server:
/// <code>
/// builder.Services.AddMiEUDIverifier(builder.Configuration);
///
/// // Then resolve via DI:
/// var verifier = app.Services.GetRequiredService&lt;VerifierApiService&gt;();
/// var tx       = await verifier.InitializeTransactionAsync();
/// var deepLink = tx.BuildWalletDeepLink();
/// var qrPng    = QrCodeService.GeneratePng(deepLink);
///
/// var envelope = await verifier.WaitForWalletResponseAsync(tx.TransactionId);
/// var identity = await verifier.ExtractIdentityDataAsync(envelope);
/// // identity.FamilyName, identity.GivenName, identity.BirthDate
/// </code>
///
/// appsettings.json:
/// <code>
/// {
///   "VerifierSettings": {
///     "BackendUrl": "https://verifier-backend.eudiw.dev"
///   }
/// }
/// </code>
/// </summary>
public static class MiEUDIverifierExtensions
{
    /// <summary>
    /// Registers <see cref="VerifierApiService"/> and all dependencies in the DI container.
    /// </summary>
    /// <param name="services">The service container.</param>
    /// <param name="configuration">The application configuration (searched for the <c>VerifierSettings</c> section).</param>
    /// <returns>The service container, for method chaining.</returns>
    public static IServiceCollection AddMiEUDIverifier(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<VerifierSettings>(
            configuration.GetSection(VerifierSettings.SectionName));

        var settings = configuration
            .GetSection(VerifierSettings.SectionName)
            .Get<VerifierSettings>() ?? new VerifierSettings();

        services.AddHttpClient<VerifierApiService>(client =>
        {
            client.BaseAddress = new Uri(settings.BackendUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
