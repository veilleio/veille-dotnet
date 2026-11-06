# Security Policy

## Supported versions

Security fixes are released for the latest published version of the Veille .NET SDK.

## Reporting a vulnerability

Please do not report security vulnerabilities through public GitHub issues.

Email **contact@veille.io** with a description of the issue, the affected version, and steps to reproduce. We acknowledge reports within three business days and keep you informed until the issue is resolved.

## Handling API keys

- Keep your Veille API key out of source control. Load it from the `VEILLE_API_KEY` environment variable or a secret manager.
- Rotate the key from the [Veille dashboard](https://app.veille.io) if you believe it was exposed.
