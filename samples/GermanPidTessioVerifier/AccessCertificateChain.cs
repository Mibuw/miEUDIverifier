using System.Security.Cryptography.X509Certificates;

namespace GermanPidTessioVerifier;

/// <summary>
/// Reads the relying-party access certificate and its issuers out of a PKCS#12 file, in the order
/// the JAR's <c>x5c</c> header wants them.
/// </summary>
/// <remarks>
/// SPEC: OpenID4VC HAIP 1.0 section 5 and ETSI TS 119 472-2 V1.2.1 clause 6.4.2 -- the access
/// certificate "in its first element, and its certificate path up to, but excluding, the trust
/// anchor". So: leaf first, each certificate issued by the next, and the self-signed root left
/// out. The EUDI reference implementation sends exactly this, two certificates for the German
/// registrar's hierarchy.
/// </remarks>
internal static class AccessCertificateChain
{
    /// <summary>
    /// Loads the chain. The file must hold the access certificate with its private key, and should
    /// hold the issuing CA certificates too; a file with only the leaf yields a chain of one, which
    /// works only where the wallet already holds the issuer.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The file holds no certificate with a private key, or two of them.
    /// </exception>
    public static IReadOnlyList<X509Certificate2> LoadFromPkcs12(string path, string? password)
    {
        var bag = new X509Certificate2Collection();
        bag.Import(path, password, X509KeyStorageFlags.EphemeralKeySet);

        var withKey = bag.Where(c => c.HasPrivateKey).ToList();
        var leaf = withKey.Count switch
        {
            1 => withKey[0],
            0 => throw new InvalidOperationException(
                $"'{path}' holds no certificate with a private key, so nothing in it can sign a request object."),
            _ => throw new InvalidOperationException(
                $"'{path}' holds {withKey.Count} certificates with a private key. Which one is the access " +
                "certificate cannot be guessed; export a file that holds exactly one."),
        };

        var chain = new List<X509Certificate2> { leaf };
        var remaining = bag.Where(c => !ReferenceEquals(c, leaf)).ToList();

        // Follow the issuer links by their encoded names, which is how the library compares them.
        // Stop before a self-signed certificate: that is the trust anchor and must not be sent.
        while (true)
        {
            var current = chain[^1];
            if (IsSelfIssued(current))
            {
                // The leaf itself is self-signed (a smoke-test key), or a root slipped in.
                break;
            }

            var issuer = remaining.FirstOrDefault(
                c => c.SubjectName.RawData.AsSpan().SequenceEqual(current.IssuerName.RawData));

            if (issuer is null)
            {
                break;
            }

            remaining.Remove(issuer);

            if (IsSelfIssued(issuer))
            {
                // The trust anchor. Found, and deliberately not added.
                break;
            }

            chain.Add(issuer);
        }

        return chain;
    }

    private static bool IsSelfIssued(X509Certificate2 certificate) =>
        certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
}
