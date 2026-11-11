#!/usr/bin/env bash
# Fetches the trust anchors of the German PID test issuer into a folder for
# GermanPid__IssuerTrustAnchorDirectory.
#
# Three certificates, because the issuer signs with two different branches under one root:
#
#   Deutschland PID-Provider Root Test CA 1 2026         self-signed, the anchor
#   Deutschland PID-Signer Test CA 1-26-1 2026           signs the PID itself
#   Deutschland PID-Status-List-Signer Test CA 1-26-2 2026   signs the revocation status list
#
# The status list branch is easy to miss and fails late: everything about the presentation
# verifies, and the only error is status_invalid, which reads like a revoked credential rather
# than a missing certificate path.
#
# The URLs are not guesswork: every certificate names the next one up in its own
# authorityInfoAccess "CA Issuers" extension. To rediscover them:
#
#   openssl x509 -inform DER -in signer.der -noout -text | grep -A2 'Authority Information Access'
#
# Served over plain HTTP, which is normal for a CA repository and safe here: the script verifies
# both chains cryptographically afterwards, so a tampered download is caught.
#
# These are TEST anchors. A production verifier takes the production hierarchy instead, and
# should take it from the published trust list rather than from AIA.

set -euo pipefail

target="${1:-./trust-anchors}"
mkdir -p "$target"

base="http://www.d-trust.net/cgi-bin"

# -L matters: www.d-trust.net answers 302 to www1.d-trust.net.
fetch() { curl -sSL --fail -m 60 -o "$target/$2" "$base/$1"; }

fetch Deutschland_PID-Provider_Root_Test_CA_1_2026.crt         pid-provider-root-test-ca-1-2026.crt
fetch Deutschland_PID-Signer_Test_CA_1-26-1_2026.crt           pid-signer-test-ca-1-26-1-2026.crt
fetch Deutschland_PID-Status-List-Signer_Test_CA_1-26-2_2026.crt pid-status-list-signer-test-ca-1-26-2-2026.crt

echo "Fetched into $target:"
for f in "$target"/*.crt; do
    printf '  %s\n' "$(openssl x509 -inform DER -in "$f" -noout -subject | sed 's/^subject=//')"
    printf '    valid to %s\n' "$(openssl x509 -inform DER -in "$f" -noout -enddate | sed 's/^notAfter=//')"
done

# Both branches must chain to the root, or one of the downloads is not what it claims to be.
pem() { openssl x509 -inform DER -in "$target/$1" -out "$target/.$2.pem" 2>/dev/null; }
pem pid-provider-root-test-ca-1-2026.crt                  root
pem pid-signer-test-ca-1-26-1-2026.crt                    signer
pem pid-status-list-signer-test-ca-1-26-2-2026.crt        statuslist

ok=1
for branch in signer statuslist; do
    if openssl verify -CAfile "$target/.root.pem" "$target/.$branch.pem" >/dev/null 2>&1; then
        echo "Chain check: the $branch CA verifies against the root."
    else
        echo "Chain check FAILED for the $branch CA: do not use these files." >&2
        ok=0
    fi
done

rm -f "$target"/.root.pem "$target"/.signer.pem "$target"/.statuslist.pem
[ "$ok" -eq 1 ] || exit 1

echo
echo "Point the sample at it:"
echo "  GermanPid__IssuerTrustAnchorDirectory=$(cd "$target" && pwd)"
