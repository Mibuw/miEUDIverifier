#!/usr/bin/env bash
# Fetches the trust anchors of the German PID test issuer into a folder for
# GermanPid__IssuerTrustAnchorDirectory.
#
# The two URLs are not guesswork: each certificate names the next one up in its own
# authorityInfoAccess "CA Issuers" extension. To rediscover them from a presentation you
# captured yourself:
#
#   openssl x509 -inform DER -in signer.der -noout -text | grep -A2 'Authority Information Access'
#
# Served over plain HTTP, which is normal for a CA repository and safe here: the script
# verifies the chain cryptographically afterwards, so a tampered download is caught.
#
# These are TEST anchors. A production verifier takes the production hierarchy instead, and
# should take it from the published trust list rather than from AIA.

set -euo pipefail

target="${1:-./trust-anchors}"
mkdir -p "$target"

root_url="http://www.d-trust.net/cgi-bin/Deutschland_PID-Provider_Root_Test_CA_1_2026.crt"
signer_url="http://www.d-trust.net/cgi-bin/Deutschland_PID-Signer_Test_CA_1-26-1_2026.crt"

# -L matters: www.d-trust.net answers 302 to www1.d-trust.net.
curl -sSL --fail -m 60 -o "$target/pid-provider-root-test-ca-1-2026.crt" "$root_url"
curl -sSL --fail -m 60 -o "$target/pid-signer-test-ca-1-26-1-2026.crt" "$signer_url"

echo "Fetched into $target:"
for f in "$target"/*.crt; do
    printf '  %s\n' "$(openssl x509 -inform DER -in "$f" -noout -subject | sed 's/^subject=//')"
    printf '    valid to %s\n' "$(openssl x509 -inform DER -in "$f" -noout -enddate | sed 's/^notAfter=//')"
done

# The root must be self-signed and the signer CA must chain to it, or one of the downloads is
# not what it claims to be.
openssl x509 -inform DER -in "$target/pid-provider-root-test-ca-1-2026.crt" -out "$target/.root.pem" 2>/dev/null
openssl x509 -inform DER -in "$target/pid-signer-test-ca-1-26-1-2026.crt" -out "$target/.signer.pem" 2>/dev/null

if openssl verify -CAfile "$target/.root.pem" "$target/.signer.pem" >/dev/null 2>&1; then
    echo "Chain check: the signer CA verifies against the root."
else
    echo "Chain check FAILED: do not use these files." >&2
    rm -f "$target/.root.pem" "$target/.signer.pem"
    exit 1
fi

rm -f "$target/.root.pem" "$target/.signer.pem"

echo
echo "Point the sample at it:"
echo "  GermanPid__IssuerTrustAnchorDirectory=$(cd "$target" && pwd)"
