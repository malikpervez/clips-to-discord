# Security policy

## Reporting a vulnerability

Use GitHub's private security-advisory reporting flow. Do not publish exploitable details or credentials in a public issue.

For ordinary bugs, open a public issue with all webhook IDs, tokens, personal paths, usernames, and clip names redacted.

## Exposed webhooks

If a Discord webhook URL is exposed, delete or regenerate it in Discord immediately. Removing it from a Git commit or issue later does not make the old credential safe.

Application logs pass through a centralized Discord-webhook redactor. Treat that as defense in depth: never intentionally place a webhook in a log message, test fixture, issue, or pull request.

## Supported versions

Security fixes are applied to the latest published release.
