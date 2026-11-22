# Security Policy — FreeNotch

## Supported Versions

Security fixes are provided for the latest release of FreeNotch (`1.0.x`).

| Version | Supported |
|---|---|
| `1.0.x` | Yes |
| `< 1.0.0` | No |

## Reporting a Vulnerability

Please **do not** open a public GitHub issue for security vulnerabilities.

Instead, report potential vulnerabilities privately via **[GitHub Security Advisories](https://github.com/Foxtrott7/FreeNotch/security/advisories/new)** on this repository. Include:

- Affected component or version
- Steps to reproduce or proof of concept
- Potential impact

You can expect an initial response within a few days, and fixes will be coordinated before public disclosure.

## Security Design Highlights

- **Unprivileged Execution**: FreeNotch runs as a standard user (`asInvoker` manifest) and installs per-user into `%LocalAppData%\Programs\WindowsNotch`.
- **Hardened Local Phone Transfer**: Uploaded filenames are sanitized against path traversal and reserved Windows device names, verified to stay inside the target directory, rate-limited with concurrent connection caps and header timeouts, and protected by constant-time (`FixedTimeEquals`) token validation.
- **Strict URI & SQLite Safety**: External links opened from OCR or Clipboard History enforce `http`/`https` scheme allowlisting; all SQLite queries use parameterized commands.
- **Reproducible Release Integrity**: Release archives ship with `SHA256SUMS.txt` and SignPath.io Authenticode signing integration.
