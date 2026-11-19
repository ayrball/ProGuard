# Security policy

## Reporting a vulnerability

Please report security problems privately, through GitHub's private vulnerability reporting:
[Report a vulnerability](https://github.com/ayrball/ProGuard/security/advisories/new) (the **Security** tab, then
**Report a vulnerability**). Don't open a public issue for them.

Useful things to include: the ProGuard version (the exe's file properties, or the MSI name), what an attacker needs
first (for example a standard local account, or administrator rights), the steps to reproduce, and what they gain.

ProGuard's engine runs as LocalSystem and takes commands from the UI over named pipes, so reports about a standard
user reaching the engine's privileges, its pipes, `%ProgramData%\ProGuard` or `Program Files\ProGuard` are
especially welcome.

You'll get a reply in the advisory. A fix is released as a new version on the
[Releases](https://github.com/ayrball/ProGuard/releases) page, with the advisory published once it is out.

## Supported versions

Only the latest release gets security fixes.
