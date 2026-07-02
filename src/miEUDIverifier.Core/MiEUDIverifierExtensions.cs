using miEUDIverifier.Configuration;
using miEUDIverifier.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace miEUDIverifier;

/// <summary>
/// Extension-Methoden für eine einfache Integration in jeden ASP.NET Core
/// oder Generic-Host-Server.
///
/// Verwendung im eigenen Server:
/// <code>
/// builder.Services.AddMiEUDIverifier(builder.Configuration);
///
/// // Dann per DI beziehen:
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
    /// Registriert <see cref="VerifierApiService"/> und alle Abhängigkeiten im DI-Container.
    /// </summary>
    /// <param name="services">Der Service-Container.</param>
    /// <param name="configuration">Die Applikations-Konfiguration (wird nach <c>VerifierSettings</c> durchsucht).</param>
    /// <returns>Den Service-Container für Method-Chaining.</returns>
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
