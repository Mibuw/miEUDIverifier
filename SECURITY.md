# Security Policy

miEUDIverifier reads identity data out of an EU Digital Identity Wallet. A defect here can mean
that something is accepted as a verified identity when it is not, so security reports are welcome
and taken seriously.

## Reporting a vulnerability

Please use **[GitHub private vulnerability reporting](https://github.com/Mibuw/miEUDIverifier/security/advisories/new)**
— it is enabled on this repository and gives us a private channel for details, patches and
reproduction material.

Please do not open a public issue for a suspected vulnerability.

Helpful in a report, as far as you have it:

- Which version or commit you tested, and whether you ran the library, the demo app, or both
- What you observed, and what you expected instead
- Reproduction material — a request, a response payload, a crafted credential, a patch
- Whether the finding depends on a defect in an upstream component, and whether that one is
  already under coordinated disclosure elsewhere

Synthetic credentials and conditional findings are worth reporting. A check that only matters when
something upstream misbehaves is still a check this project should be doing itself.

## What to expect

| | |
|---|---|
| Acknowledgement | within 3 working days |
| Initial assessment | within 10 working days |
| Fix and release | as soon as a correct fix is ready; we will tell you the intended date |

We follow coordinated disclosure. Details stay private until a fixed release is out, and we are
happy to align the timing with a disclosure already running against an upstream project.

## Credit

Reporters are credited by name in the release notes and the commit, unless they prefer otherwise.
If a CVE is appropriate, we are glad to request one and to name the reporter in it.

## Supported versions

Fixes go into the current minor release line on NuGet. Older versions are not patched separately;
upgrading is the supported path.

## Acknowledgements

- **Vítor Fonte**, Department of Informatics, University of Minho — credential type binding was
  not verified on the consumer side, and the shipped Kestrel configuration bound to all interfaces
  rather than loopback (fixed in 1.4.0)
