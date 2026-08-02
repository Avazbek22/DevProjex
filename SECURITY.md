# Security Policy

## Supported versions

Security fixes are provided for the latest published DevProjex release. The
current development branch may receive a fix before the next release, but it is
not a supported distribution.

## Reporting a vulnerability

Do not disclose a suspected vulnerability, exploit, credential, or sensitive
file content in a public issue, discussion, pull request, or terminal recording.

Report security issues by email to
[avazbekolimov722@gmail.com](mailto:avazbekolimov722@gmail.com). Include:

- the affected DevProjex version, operating system, and architecture;
- the smallest safe reproduction;
- the security impact and required preconditions;
- whether the source project or generated output contains sensitive data;
- any proposed remediation or disclosure deadline.

You should receive an acknowledgement within seven days. Maintainers will
coordinate validation, remediation, release, and disclosure with the reporter.

GitHub private vulnerability reporting is the preferred long-term channel. If
the repository exposes a **Report a vulnerability** button, use it instead of
email.

## Scope

Security-sensitive areas include project path validation, symlink and junction
handling, archive extraction and creation, Git clone/cache operations, Desktop
IPC, profile import, terminal launchers, and accidental disclosure of project
content through diagnostics.

DevProjex is intended to treat the source project as read-only. A reproducible
source mutation, destination escape, credential disclosure, or unintended file
inclusion should be reported as a security issue.
