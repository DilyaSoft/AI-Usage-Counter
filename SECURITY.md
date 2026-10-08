# Security Policy

AI Usage Counter reads OAuth tokens that Claude Code, Codex CLI and Grok CLI store on your machine,
so we take token handling seriously.

## What the app does with your tokens

- It reads them from the CLI's own login file (`~/.claude`, `~/.codex`, `~/.grok`).
- It sends each token only to the service it belongs to (Anthropic, OpenAI or xAI) over HTTPS.
- It never writes, logs or copies tokens, and it has no telemetry.
- It never calls an OAuth refresh endpoint. When a Claude, Codex or Grok access token is expired or rejected, it starts that CLI so the CLI can rewrite its own login file, then stops the process it started.

## Reporting a vulnerability

Please **do not open a public issue** for security problems.
Report them privately through GitHub:
**Security → Report a vulnerability** on this repository
([direct link](https://github.com/DilyaSoft/AI-Usage-Counter/security/advisories/new)).

We aim to acknowledge reports within 7 days.

## Supported versions

Only the latest release gets security fixes.
