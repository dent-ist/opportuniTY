# Security policy

opportuniTY is self-hosted software that law firms and legal departments use to review privileged and confidential
material. We treat vulnerabilities in it, and in what it ships, as high-impact by default.

## Reporting a vulnerability

**Do not open a public issue, discussion or pull request for a security problem.**

Report it privately through GitHub's **private vulnerability reporting**: open the repository's **Security** tab and
choose **Report a vulnerability**. Only the maintainers and you can see the report, and we coordinate the fix and the
advisory (GHSA, plus a CVE where warranted) in that private thread.

Please include:

- the affected version or commit, and the deployment profile (Lite or Full)
- the component (API, a worker, the web app, Compose/deployment files, CI or release artifacts)
- steps to reproduce or a proof of concept, and the impact you expect (for example a cross-workspace read, a
  privilege or redaction bypass, code execution in a worker)

Use synthetic data only. Never send real client documents or matter data.

## Supported versions

| Version | Supported |
|---|---|
| `main` and the latest release | Yes |
| Older pre-1.0 releases | No. Upgrade to the latest release |

After 1.0 we will support the latest minor release and the one before it, and this table will list them.

## Response targets

| Step | Target |
|---|---|
| Acknowledge the report | 3 business days |
| Initial assessment and severity (CVSS v4) | 10 business days |
| Fix released: Critical | 14 days after triage |
| Fix released: High | 30 days after triage |
| Fix released: Medium / Low | Next scheduled release, at most 90 days |

These are targets for a volunteer project, not contractual commitments. We will tell you if we expect to miss one.

## Coordinated disclosure

We ask for up to **90 days** from your report before public disclosure, or less once a fixed release is available.
We publish a GitHub Security Advisory with the fix and credit you unless you prefer otherwise. If a vulnerability is
being exploited in the wild we may release and disclose sooner.

## Scope

In scope: code in this repository, the container images and release artifacts we publish, and the default
configuration of the Compose profiles. The **Lite** profile is for evaluation and must not hold real client data;
weaknesses that only exist because Lite omits Full-profile controls (see the
[threat model](docs/security/threat-model.md)) are documented trade-offs, not vulnerabilities.

Out of scope: an installation operator's own infrastructure, identity provider and backups, and findings that need
root on the host.

## How the project protects its supply chain

Every pull request runs dependency vulnerability, secret, license and SBOM gates, and CodeQL. See
[docs/ci.md](docs/ci.md#security-and-supply-chain-gates).
