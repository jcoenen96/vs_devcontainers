# Security policy

## Supported versions

Security fixes go into the latest release. Until 1.0 there are no maintained older versions: update to the latest
release from the Visual Studio Marketplace or the [GitHub releases](https://github.com/jcoenen96/vs_devcontainers/releases).

## Reporting a vulnerability

Please do **not** open a public issue. Report it privately through GitHub:
**[Report a vulnerability](https://github.com/jcoenen96/vs_devcontainers/security/advisories/new)** (the
repository's *Security* tab, *Report a vulnerability*). Include what an attacker can do, the steps to reproduce it,
and the extension and Visual Studio versions.

You get an answer within 7 days. A confirmed issue is fixed in a release as soon as possible, and credited in the
advisory unless you prefer otherwise.

## What the extension does with your data

The extension runs on your computer and talks to no service of its own. It has no telemetry. These are the parts
that matter for security, so you can judge them and report what looks wrong:

- **It runs what devcontainer.json says.** Opening a dev container builds its image and runs its lifecycle commands
  (`postCreateCommand` and the others), as VS Code does. Open dev containers only from repositories you trust.
- **It copies credentials into the container** on every run, so the app works as on Windows: the project's user
  secrets, your NuGet.Config with its feed credentials (passwords Windows encrypted are left out), your `.npmrc`
  with its registry tokens, and the HTTPS development certificate with its private key. They are sent through the
  container engine's `exec` on standard input, never on a command line, and written readable by the container user
  only (`chmod 600`). The certificate's file is deleted once it is imported; it is protected by a one-time random password,
  which is passed to `dotnet dev-certs` inside the container. Anyone who can run commands in the
  container as that user can read them: do not share the container, or its image after committing it, with people
  who should not have them.
- **Port forwarding** listens on `127.0.0.1` and `::1` only, never on other network interfaces, so other computers
  on your network cannot reach the forwarded ports.
- **Debugging** runs the .NET debugger (vsdbg, downloaded from Microsoft's `aka.ms/getvsdbgsh` into the container)
  and Node.js with its inspector on port 9229 of the container, forwarded to `localhost` only. The inspector lets
  whoever reaches it run code in the app; it is on only while you debug.
- **Installing prerequisites** runs `winget` (Node.js LTS from the winget source) and `npm install -g
  @devcontainers/cli`, only after you agree in a prompt.
