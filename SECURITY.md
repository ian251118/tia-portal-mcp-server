# Security

## What this software is trusted with

The server runs with your Windows user's rights, drives TIA Portal through Openness, and can open an S7 connection
to PLCs configured in the open project. How it limits what a language model can do with that is described in
[docs/SAFETY.md](docs/SAFETY.md).

## Reporting a vulnerability

Please report privately, not in a public issue, anything that lets a tool call:

- change project content from a tool classed read-only, or bypass `--access readonly` / `--access write`;
- delete or overwrite without `confirm: true`;
- read or write files outside `--allowed-dir`;
- contact a host that the S7 restriction should have refused;
- save the project despite `--no-save`.

Use GitHub's *Report a vulnerability* button on the repository's **Security** tab (private vulnerability
reporting). Include the server version (`TiaMcpServer.exe --version`), the options it was started with and the
tool call that shows the problem.

## Out of scope

- A model passing `confirm: true` without asking the user. The confirmation is a prompt-level safeguard, not an
  access control; use `--access` for a hard limit.
- Wrong or unsafe automation logic produced by a model. Reviewing and testing it is the user's responsibility.
- Vulnerabilities in TIA Portal, in Openness or in the PLC firmware: report those to Siemens ProductCERT.

## Supported versions

Only the latest release is maintained.
