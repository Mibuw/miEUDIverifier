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
    /// When true, only the SD-JWT VC PID credential is requested (no mso_mdoc alternative).
    /// The mirror image of <see cref="MdocOnly"/>, for backends scoped to a <c>dc+sd-jwt</c>
    /// Registration Certificate. Setting both at once is a configuration error and throws.
    /// </summary>
    public bool SdJwtOnly { get; set; }

    /// <summary>
    /// Optional PID attributes to request, in mso_mdoc spelling. Falls back to the configured
    /// values when null.
    /// </summary>
    public List<string>? PidClaims { get; set; }

    /// <summary>
    /// Optional accepted vct values for the generic SD-JWT VC PID option. Falls back to the
    /// configured values when null; an empty list drops the option from the request.
    /// </summary>
    public List<string>? SdJwtVctValues { get; set; }

    /// <summary>
    /// Optional accepted vct values for the German PID option. Falls back to the configured
    /// values when null; an empty list drops the option from the request. A backend fronting a Registration Certificate must request exactly the
    /// vct values that certificate covers.
    /// </summary>
    public List<string>? GermanPidVctValues { get; set; }

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
