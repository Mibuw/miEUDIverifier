using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tessio.Verifier.AspNetCore;
using Tessio.Verifier.Core;
using Tessio.Verifier.Core.Mdoc;
using Tessio.Verifier.OpenId4Vp;
using Tessio.Verifier.Trust;

namespace miEUDIverifier.Services.Tessio;

/// <summary>
/// Registers and hosts the in-process verification path.
/// </summary>
public static class TessioVerifierExtensions
{
    /// <summary>The media type a JAR is served as. SPEC: RFC 9101.</summary>
    private const string RequestObjectMediaType = "application/oauth-authz-req+jwt";

    /// <summary>
    /// Registers everything the in-process engine needs. Call before building the application, and
    /// host it afterwards with <see cref="MapInProcessVerifier"/>.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="options">Where the certificates, the trust anchors and the public URL are.</param>
    /// <remarks>
    /// <para>
    /// Order matters inside this method, not to the caller: the library registers its own services
    /// with <c>TryAdd</c>, so the request builder and trust resolver registered here win because
    /// they are in the container before <c>AddTessioVerifier</c> runs.
    /// </para>
    /// <para>
    /// Everything is read and validated here, at startup, so a wrong path or password fails the
    /// process rather than the first wallet to scan a code.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddInProcessVerifier(
        this IServiceCollection services, InProcessVerifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var publicBaseUrl = options.PublicBaseUrl.TrimEnd('/');
        var routePrefix = NormalizePrefix(options.RoutePrefix);

        // Resolved once, here, rather than defaulted on the property: see the remarks on
        // InProcessVerifierOptions.RequestedClaims for why a pre-filled collection property is a
        // trap when a host binds these options from configuration.
        var requestedClaims = options.RequestedClaims is { Count: > 0 } configured
            ? configured
            : InProcessVerifierOptions.DefaultRequestedClaims;

        // The whole chain, not only the leaf. SPEC: HAIP 1.0 section 5 and ETSI TS 119 472-2 clause
        // 6.4.2 want the access certificate AND its path up to, but excluding, the trust anchor in
        // the JAR's x5c; a wallet handed only the leaf may not be able to build a path to its trust
        // list. The constructor also refuses a self-signed leaf, which HAIP forbids.
        var accessCertificateChain = AccessCertificateChain.LoadFromPkcs12(
            options.AccessCertificatePath, options.AccessCertificatePassword);

        var registrationCertificate = options.RegistrationCertificate is { Length: > 0 } inline
            ? inline
            : File.ReadAllText(options.RegistrationCertificatePath!).Trim();

        var anchors = LoadTrustAnchors(options.IssuerTrustAnchorDirectory);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new HostedRequestObjects(sp.GetRequiredService<TimeProvider>()));

        // Registered twice on purpose, as one instance: the library takes the interface, and the
        // engine needs the concrete type for the client_id the certificate produces.
        services.AddSingleton(_ => new RegistrationCertificateRequestBuilder(
            accessCertificateChain,
            registrationCertificate,
            // Our own endpoint, not the library's: see HostedRequestObjects for why.
            new Uri($"{publicBaseUrl}{routePrefix}/jar"),
            options.SessionLifetime,
            options.AllowSelfSignedAccessCertificate));
        services.AddSingleton<IPresentationRequestBuilder>(
            sp => sp.GetRequiredService<RegistrationCertificateRequestBuilder>());

        // The library's default resolver trusts its own mock issuers and nothing else.
        services.AddSingleton<ITrustListResolver>(sp => new StaticTrustListResolver(
            trustedIssuers: [],
            source: options.IssuerTrustAnchorDirectory,
            trustAnchors: anchors,
            clock: sp.GetRequiredService<TimeProvider>()));

        // Revocation checking. Off for the German PID, and that is a gap rather than a preference:
        // see InProcessVerifierOptions.CheckRevocation, which explains why it cannot currently be
        // on and what the consequence is.
        services.AddSingleton(sp => new MdocVerifier(
            sp.GetRequiredService<ITrustListResolver>(),
            new MdocVerifierOptions { CheckStatus = options.CheckRevocation },
            sp.GetRequiredService<TimeProvider>()));

        services.AddTessioVerifier(verifier =>
        {
            verifier.Mode = VerifierMode.Live;
            verifier.RoutePrefix = routePrefix;
            verifier.CredentialFormat = MdocVerifier.Format;
            verifier.ExpectedDocType = options.DocType;
            verifier.MdocNamespace = options.MdocNamespace;
            verifier.RequestedClaims = requestedClaims.ToList();
            verifier.ResponseMode = options.ResponseMode;
            verifier.SessionLifetime = options.SessionLifetime;

            // Only a presentation that actually verified completes the session, so a failed attempt
            // leaves the session open for the holder to try again.
            verifier.CompleteOnlyOnValidResponse = true;
        });

        services.AddSingleton(new TessioEngineOptions
        {
            DocType = options.DocType,
            MdocNamespace = options.MdocNamespace,
            RequestedClaims = requestedClaims,
            // Where the library hosts the wallet callback under the prefix set above.
            ResponseUri = new Uri($"{publicBaseUrl}{routePrefix}/callback"),
            ResponseMode = options.ResponseMode,
            SessionLifetime = options.SessionLifetime,
            CheckRevocation = options.CheckRevocation,
        });

        services.AddSingleton<TessioVerifierEngine>();
        return services;
    }

    /// <summary>
    /// Hosts the endpoints a wallet calls: the library's own, plus this host's request object
    /// endpoint.
    /// </summary>
    /// <remarks>
    /// Call after <c>UseForwardedHeaders</c> where the application sits behind a proxy or a tunnel.
    /// The library builds the response URI from the scheme and host it sees, and a wallet refuses to
    /// post a presentation to an <c>http</c> address -- as it should.
    /// </remarks>
    public static WebApplication MapInProcessVerifier(this WebApplication app, string? routePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<TessioEngineOptions>();
        var prefix = NormalizePrefix(routePrefix ?? app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<VerifierOptions>>().Value.RoutePrefix);

        // The library's hosted start page cannot work in this application, and leaving it reachable
        // would put a page that looks like a verifier on a public site and then fail: it creates a
        // session whose request object it files in its own internal store, while this host's request
        // URIs point at the endpoint below. Unmapping a route the library maps is not possible, so
        // it is shadowed here, ahead of routing, and sent to the application's own page.
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method)
                && context.Request.Path.Equals($"{prefix}/start", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Redirect("/");
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        // A wallet cannot sign in, so none of the endpoints below may require it. Without this, an
        // application with a fallback authorization policy -- "every endpoint needs an
        // authenticated user", which is a common and otherwise sensible default -- answers the
        // wallet's fetch of the request object with a 302 to its login page, and the presentation
        // never happens. Confirmed against such an application before this was added.
        //
        // A group with an empty prefix is used because the library's own endpoints are mapped by
        // MapTessioVerifier, which returns no convention builder to mark them through. Anything
        // mapped into this group carries the metadata.
        //
        // This widens nothing that was protected: these endpoints are the wallet-facing protocol
        // surface and are anonymous by design. The session status endpoint among them discloses a
        // result to whoever holds a session id, which is the library's documented contract.
        var wallet = app.MapGroup(string.Empty).AllowAnonymous();

        wallet.MapTessioVerifier(prefix);

        // The signed request object, which the wallet fetches from the request_uri in the deep link.
        wallet.MapGet($"{prefix}/jar/{{requestId}}", (string requestId, HostedRequestObjects store, HttpContext http) =>
        {
            var jar = store.Get(requestId);
            if (jar is null)
            {
                // Expired or never existed, and the two are not told apart on purpose: whoever holds
                // a request id is not necessarily the wallet the request was built for.
                return Results.NotFound();
            }

            // No store: a request object is for one presentation, and a cache between here and the
            // wallet serving it to someone else is not wanted.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Text(jar, RequestObjectMediaType);
        });

        app.Logger.LogInformation(
            "In-process verifier hosted under {Prefix}: request objects at {Prefix}/jar/{{id}}, "
            + "wallet callback at {ResponseUri}, revocation checking {Revocation}.",
            prefix, prefix, options.ResponseUri, options.CheckRevocation ? "ON" : "OFF");

        if (!options.CheckRevocation)
        {
            // Said out loud at every start, because it is the one thing about this path that a
            // reader of the logs would otherwise have to know to ask about.
            app.Logger.LogWarning(
                "Revocation is NOT checked on the in-process path: a revoked PID is accepted. "
                + "See InProcessVerifierOptions.CheckRevocation.");
        }

        return app;
    }

    private static List<X509Certificate2> LoadTrustAnchors(string directory)
    {
        var anchors = Directory.EnumerateFiles(directory)
            .Where(f => Path.GetExtension(f) is ".pem" or ".crt" or ".cer" or ".der")
            .Select(X509CertificateLoader.LoadCertificateFromFile)
            .ToList();

        if (anchors.Count == 0)
        {
            throw new InvalidOperationException(
                $"No issuer trust anchor found in '{directory}'. Without the PID issuer's CA "
                + "certificate every presentation verifies cryptographically and is still refused "
                + "as untrusted, which reads like a broken credential rather than a missing file.");
        }

        return anchors;
    }

    private static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return string.Empty;
        }

        var trimmed = prefix.Trim().TrimEnd('/');
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }
}
