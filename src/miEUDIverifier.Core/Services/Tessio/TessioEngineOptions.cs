using Tessio.Verifier.OpenId4Vp;

namespace miEUDIverifier.Services.Tessio;

/// <summary>
/// The per-request shape of what <see cref="TessioVerifierEngine"/> asks a wallet for, resolved
/// once at startup from configuration.
/// </summary>
/// <remarks>
/// Separate from <c>VerifierSettings</c> because these are resolved values rather than
/// configuration: the response URI is already absolute, the claim list already defaulted. The
/// engine should not have to re-derive any of it per request.
/// </remarks>
public sealed class TessioEngineOptions
{
    /// <summary>The mdoc doctype to ask for, and to require of what comes back.</summary>
    public required string DocType { get; init; }

    /// <summary>
    /// The namespace the requested elements live in. For the EUDI PID this equals the doctype,
    /// which is a coincidence of that credential rather than a rule.
    /// </summary>
    public required string MdocNamespace { get; init; }

    /// <summary>
    /// The attributes to request. Must stay within what the registration certificate grants: the
    /// German wallet's policy engine drops a request asking for more, with no consent screen and
    /// nothing that identifies the overreach as the cause.
    /// </summary>
    public required IReadOnlyList<string> RequestedClaims { get; init; }

    /// <summary>
    /// Where the wallet posts its response. Must be the publicly reachable address of the callback
    /// the library hosts, because the wallet resolves it over the internet.
    /// </summary>
    public required Uri ResponseUri { get; init; }

    /// <summary>
    /// How the wallet answers. <see cref="ResponseMode.DirectPostJwt"/> means encrypted to a
    /// per-request key, which is the HAIP default and what the German wallet uses.
    /// </summary>
    public ResponseMode ResponseMode { get; init; } = ResponseMode.DirectPostJwt;

    /// <summary>How long a request and its session stay valid.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether the credential's revocation status was checked. Carried here so the hosting code can
    /// say so in its startup log; the setting itself is applied to the verifier.
    /// </summary>
    public bool CheckRevocation { get; init; }

    /// <summary>
    /// The host's own page for the holder's phone after a presentation, or null for the underlying
    /// library's. Carried here because the hosting code serves it, while the caller configures it on
    /// <see cref="InProcessVerifierOptions.ReturnPageHtml"/>.
    /// </summary>
    public string? ReturnPageHtml { get; init; }

    /// <summary>How often the engine looks at the session while waiting for the wallet.</summary>
    /// <remarks>
    /// Reading an in-memory session costs nothing, so this is short. It is not a network poll: the
    /// library's callback has already done the work by the time the status changes.
    /// </remarks>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}
