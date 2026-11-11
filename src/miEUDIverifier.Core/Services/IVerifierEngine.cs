using miEUDIverifier.Models;

namespace miEUDIverifier.Services;

/// <summary>
/// One way of running a verification, from the request the wallet is handed to the identity data
/// that comes back. Two implementations exist, and which one a deployment uses is per ecosystem:
/// <list type="bullet">
/// <item><see cref="VerifierApiService"/> talks to an EUDI reference verifier backend over HTTP.
/// That is how the EU ecosystem works here, because its verifier identity comes from the hosted
/// backend at verifier-backend.eudiw.dev rather than from a certificate we hold.</item>
/// <item><see cref="Tessio.TessioVerifierEngine"/> does the whole thing in process. That needs an
/// access certificate of our own, which is why it is the German path: the SPRIND-issued
/// certificate is what lets this application be the verifier instead of delegating.</item>
/// </list>
/// </summary>
/// <remarks>
/// The three members are the whole surface the application needs, which is why the split is worth
/// having: the UI, the polling and the session handling are written once and do not know which
/// engine answered.
/// </remarks>
public interface IVerifierEngine
{
    /// <summary>
    /// Starts a verification and returns what the wallet must be handed: the transaction to follow
    /// and the request URI to put in the QR code or deep link.
    /// </summary>
    Task<InitTransactionResponse> InitializeTransactionAsync(
        TransactionOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Waits for the wallet to answer, until it does, the transaction times out, or
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="transactionId">The transaction returned by <see cref="InitializeTransactionAsync"/>.</param>
    /// <param name="progress">Optional running commentary, for a console host.</param>
    /// <param name="onRawResponse">
    /// Optional access to the engine's own view of the response, for diagnostics. What it carries
    /// differs per engine and is not a contract.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<WalletResponseEnvelope> WaitForWalletResponseAsync(
        string transactionId,
        IProgress<string>? progress = null,
        Action<string>? onRawResponse = null,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the identity data out of a wallet response, taking it only from a credential whose
    /// type matches what the transaction asked for.
    /// </summary>
    /// <param name="envelope">The response as <see cref="WaitForWalletResponseAsync"/> returned it.</param>
    /// <param name="options">
    /// The options the transaction was started with, so the expected types match what was asked
    /// for. When null, the configured defaults apply.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The identity data. A null <see cref="IdentityData.CredentialType"/> means nothing was taken,
    /// because the response carried no credential of the requested type -- which is a different
    /// outcome from incomplete data, and the caller is expected to tell the two apart.
    /// </returns>
    Task<IdentityData> ExtractIdentityDataAsync(
        WalletResponseEnvelope envelope,
        TransactionOptions? options,
        CancellationToken ct = default);
}
