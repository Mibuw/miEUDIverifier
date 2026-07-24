namespace miEUDIverifier.Models;

/// <summary>
/// Per-transaction options that shape the DCQL query and the authorization request.
/// Used to serve different trust ecosystems (e.g. the German EUDI Wallet) from one app.
/// </summary>
public class TransactionOptions
{
    /// <summary>
    /// When true, only the <c>mso_mdoc</c> PID credential is requested (no SD-JWT VC
    /// alternatives). Required for backends scoped to an mso_mdoc-only Registration
    /// Certificate — requesting more than registered makes the wallet abort.
    /// </summary>
    public bool MdocOnly { get; set; }

    /// <summary>
    /// Optional Wallet Relying Party Intended Use id configured on the backend. Passed as
    /// <c>intended_use_id</c> so the backend attaches the matching Registration Certificate.
    /// </summary>
    public string? IntendedUseId { get; set; }

    /// <summary>
    /// Optional OpenID4VP <c>response_mode</c> override (<c>direct_post</c> or
    /// <c>direct_post.jwt</c>). High-assurance wallets (e.g. the German EUDI Wallet) require an
    /// encrypted response (<c>direct_post.jwt</c>). Falls back to the configured default when null.
    /// </summary>
    public string? ResponseMode { get; set; }
}
