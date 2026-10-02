# TIA Portal MCP Server

An [MCP](https://modelcontextprotocol.io) server that lets an AI assistant (Claude Desktop, Claude Code, or any
other MCP client) work inside **Siemens TIA Portal V20** through the official **TIA Portal Openness** API: read and
write PLC blocks in SCL and LAD, manage tags and data types, build **WinCC Unified** HMI screens, and read live
values from the running PLC.

It is built for engineering a real project with a person in the loop, so safety is part of the design rather than
an afterthought: every tool declares whether it reads, writes or destroys, destructive calls must be confirmed,
what is about to be replaced is backed up first, and nothing here can download to a PLC or write to a running
machine.

*Catalan summary: [README.ca.md](README.ca.md).*

> **Not a Siemens product.** This is an independent, unofficial project. SIMATIC, TIA Portal, WinCC and S7 are
> trademarks of Siemens AG. You need your own licensed TIA Portal installation; no Siemens software is included.

---

## What it can do

| Area | Examples |
|---|---|
| **Project** | Connect to the running TIA Portal, open/save/close, project info, archive as a restore point |
| **PLC blocks** | List and read blocks, write blocks from **SCL** (compiled on import, errors returned), build **LAD** blocks from a rung description, block interfaces, instance DBs, folders, cross-references |
| **PLC data** | Tag tables, tags and user constants (create, change, delete, import/export), PLC data types (UDTs), watch tables |
| **Compile** | PLC software/hardware, HMI, or a single block, with the compiler messages |
| **HMI (WinCC Unified)** | Screens and screen groups, any screen item (buttons, IO fields, shapes...), properties, tag bindings, colour-by-tag, event and property scripts with syntax check, HMI tags, alarms, tag usage search, faceplate parameters |
| **Hardware** | Devices, modules with their I/O addresses, IP addresses, hardware catalog search, protection settings |
| **Live values** | Read inputs/outputs/memory/DBs from the running PLC over S7, by address or by PLC tag name |

Around 100 tools in total. The complete, generated list with every argument is in
**[docs/TOOLS.md](docs/TOOLS.md)**.

### What it deliberately does not do

- **No download to the PLC or HMI**, no go-online, no start/stop of the CPU.
- **No writing to the running PLC.** The S7 client is read-only by construction.
- **No changes to fail-safe (F) blocks**, and no handling of passwords or protection levels.
- **No hardware creation** (adding devices or modules). Hardware is read; one tool can change a module's
  firmware version.

Getting changes onto a machine stays a human step, done in TIA Portal.

---

## Safety in one minute

Every tool description starts with its class, and clients that support MCP annotations get the same information as
`readOnlyHint` / `destructiveHint`:

| Class | Meaning |
|---|---|
| `[READ-ONLY]` | Never changes the project. |
| `[WRITE]` | Creates something or changes a property. Nothing existing is removed. |
| `[DESTRUCTIVE]` | Deletes or replaces existing content. **Refused unless called with `confirm: true`**, which the assistant is instructed to set only after you agreed to that specific change. |

On top of that:

- **Nothing is permanent until the project is saved.** Edits live in TIA Portal's memory. Close without saving and
  they never happened. Start the server with `--no-save` and only you can save.
- **Edits run as TIA transactions**: a multi-step edit that fails half-way is rolled back as a whole, and each
  edit is registered with TIA Portal as one undo unit.
- **Automatic backups**: before a block or data type is replaced or deleted, its source is copied to a backup
  folder and the path is returned.
- **Access levels**: `--access readonly` exposes only the read tools; `--access write` adds creating and
  modifying but no deleting or overwriting.
- **Sandboxing**: file arguments can be confined to a folder; live S7 reads only go to PLCs configured in the
  open project.

The full model, including what it cannot protect you from, is in **[docs/SAFETY.md](docs/SAFETY.md)**. Read it
before pointing this at a project that matters.

---

## Requirements

- Windows 10/11 with **TIA Portal V20** installed, including the **TIA Openness** option
  (and WinCC Unified if you want the HMI tools).
- Your Windows user in the local group **`Siemens TIA Openness`**.
- **.NET SDK 8 or newer** to build. The server itself runs on .NET Framework 4.8, which Windows already has
  (Openness is a .NET Framework library, so the server has to be one too).
- For live values: network access to the PLC and *Permit access with PUT/GET communication* enabled on the CPU.

Tested with TIA Portal V20, an S7-1200 (firmware V4.6) and a WinCC Unified Comfort panel. See
[Other TIA Portal versions](#other-tia-portal-versions) for anything else.

## Installation

### 1. Get the server

**Download** the latest zip from the
[Releases page](https://github.com/paurieravila-ship-it/tia-portal-mcp-server/releases) and unpack it anywhere
(no .NET SDK needed), **or build it** yourself:

```powershell
git clone https://github.com/paurieravila-ship-it/tia-portal-mcp-server.git
cd tia-portal-mcp-server
dotnet build -c Release
```

The build result is `bin\Release\TiaMcpServer.exe`. Either way, Siemens' DLLs are not included; they are loaded
from your TIA Portal installation at run time. In the steps below, replace `.\bin\Release\` with the folder you
unpacked if you downloaded the zip.

### 2. Join the Openness group (once)

1. Run `lusrmgr.msc` (or *Computer Management > Local Users and Groups > Groups*).
2. Open **Siemens TIA Openness** and add your user.
3. Sign out of Windows and back in.

### 3. Check the installation

```powershell
.\bin\Release\TiaMcpServer.exe --doctor
```

This reports whether Openness was found, whether your user is in the group, and which TIA Portal instances are
running.

### 4. Register it in your MCP client

**Claude Desktop** - edit `%APPDATA%\Claude\claude_desktop_config.json` (an example is in
[`examples/`](examples/claude_desktop_config.json)) and restart the app:

```json
{
  "mcpServers": {
    "tia-portal": {
      "command": "C:\\path\\to\\tia-portal-mcp-server\\bin\\Release\\TiaMcpServer.exe",
      "args": ["--access", "full", "--no-save"]
    }
  }
}
```

**Claude Code**:

```powershell
claude mcp add tia-portal -- "C:\path\to\tia-portal-mcp-server\bin\Release\TiaMcpServer.exe" --no-save
```

Any other client that speaks MCP over stdio works the same way: run the exe, talk JSON-RPC on stdin/stdout.

### 5. First connection

Open your project in TIA Portal and ask the assistant to connect. TIA Portal shows an **Openness access prompt**
for the server: choose **Yes to all**. It appears once per build of the exe (TIA remembers the file by its hash),
so it comes back after you rebuild.

---

## Using it

Talk to the assistant in plain language; it picks the tools. A typical session:

> Connect to TIA Portal and show me the blocks of the PLC.
>
> Read FB_Conveyor and explain what the state machine does.
>
> Add a 2 second start delay to it. Show me the change before you import it.
>
> Create a screen "Manual" with a Start and a Stop button that set and reset the bit `"DB_HMI".Start`, and colour
> the Start button green while `"DB_HMI".Running` is true.
>
> What are %I0.0 to %I0.7 right now on the PLC?

Good habits:

- **Work on a copy** of the project the first times, or ask for an archive (`tia_archive_project`) before a big
  change. TIA only archives a project with no unsaved changes, so do it first.
- **Review in TIA Portal before saving.** The assistant's changes show up live in the editor.
- **A clean compile is not a correct program.** Test on the machine the way you would test your own code.
- Keep TIA Portal **offline** while editing: it refuses block imports and source generation while online.

## Command-line options

| Option | Effect |
|---|---|
| `--access readonly\|write\|full` | How much the server may do (default `full`). `readonly`: only tools that never change the project. `write`: plus creating and modifying, nothing destructive. `full`: everything; destructive calls still need `confirm: true`. |
| `--groups plc,hmi,...` | Which tool groups to expose: `plc`, `hmi`, `hardware`, `s7`, `diag`, or `all` (default). `core` is always on. Fewer tools means less context spent on tool definitions. |
| `--no-save` | Refuse `tia_save_project`: the assistant edits, only you save. |
| `--allowed-dir <dir>` | Confine every file and directory argument to this folder (repeatable). |
| `--s7-host <ip>` | Also allow live reads from this address (repeatable). By default only addresses configured in the open project are allowed. `--s7-any-host` lifts the restriction. |
| `--audit-log <file>` | Append one JSON line per tool call (tool, arguments, outcome, duration). |
| `--backup-dir <dir>` | Where automatic backups go (default `%LOCALAPPDATA%\TiaMcpServer\backups`). `--no-backups` turns them off. |
| `--no-transactions` | Do not wrap edits in a TIA undo transaction. |
| `--doctor` | Check the installation and exit. |
| `--dump-tools [markdown\|json]` | Print the tool reference and exit. |
| `--help`, `--version` | |

The same settings can come from environment variables (`TIA_MCP_ACCESS`, `TIA_MCP_GROUPS`, `TIA_MCP_ALLOWED_DIRS`,
`TIA_MCP_S7_HOSTS`, `TIA_MCP_AUDIT_LOG`, `TIA_MCP_BACKUP_DIR`); a flag wins over the variable.

Some sensible combinations:

| Goal | Arguments |
|---|---|
| Look around a project, change nothing | `--access readonly` |
| Day-to-day engineering, you keep the save button | `--no-save` |
| PLC programming only, small tool list | `--groups plc --no-save` |
| HMI work only | `--groups hmi --no-save` |
| Unattended or shared use | `--access write --no-save --allowed-dir C:\work\tia-mcp --audit-log C:\work\tia-mcp\audit.jsonl` |

---

## Limitations worth knowing

These are limits of TIA Openness (or of this server), not bugs to wait out:

- **One project per TIA Portal instance.** Connect to the instance that already has your project open.
- **Offline only for edits.** While TIA Portal is online with the PLC it rejects imports and source generation.
- **XML export needs consistent objects.** `tia_export_block` and `tia_get_block_interface` fail on blocks that do
  not compile; `tia_read_block` (source text) still works.
- **Openness compile results are thinner than the TIA Portal UI.** A compile can report success and leave a block
  marked inconsistent when there are warnings the API does not surface; compiling once from TIA Portal itself
  clears it up.
- **Watch table entries cannot be added one by one**; a table is imported as XML.
- **WinCC Unified**: tables/grids and a few other controls cannot be created through Openness; images must
  already be in the project; text lists are not editable entry by entry. Practical notes are in
  [docs/HMI_UNIFIED_NOTES.md](docs/HMI_UNIFIED_NOTES.md).
- **Classic panels (Comfort/Basic)**: only screen export/import as XML. That code path has not been verified on a
  real classic panel.
- **Live reads use the classic S7 protocol**: optimized DBs cannot be read by offset. Read I/Q/M, or a DB with
  optimized access switched off.

What has and has not been verified against a real project is listed in
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md#verification-status).

## Other TIA Portal versions

The server is compiled against one specific Openness version and finds that version's DLLs through the registry.
To target another one:

```powershell
dotnet build -c Release -p:TiaVersion=19
```

Only V20 has been tested. The PLC side of Openness is fairly stable across versions; the WinCC Unified object
model changed between releases, so expect to fix a few things there.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `TIA Portal refused the connection: Security error` | The Openness prompt in TIA Portal was not answered (look for it behind other windows and choose *Yes to all*), or your user is not in the *Siemens TIA Openness* group. Run `--doctor`. |
| The prompt appears again | The exe was rebuilt; TIA identifies it by hash. |
| `No running TIA Portal instance found` | Start TIA Portal and open the project, or connect with mode `new_ui`. |
| `This function is not supported in online mode` | Go offline in TIA Portal. |
| `Inconsistent blocks ... cannot be exported` | Compile first; use `tia_read_block` for the source in the meantime. |
| Tools are missing in the client | The client was not restarted after a rebuild, or `--access` / `--groups` hides them (`tia_state` shows the configuration). |
| Build error `Siemens.Engineering.dll not found` | TIA Portal (with Openness) is not installed in the default folder: pass `-p:TiaPublicApiDir="D:\...\PublicAPI\V20"`. |
| Live read: `connect ... failed` | Wrong IP or no route, or *Permit access with PUT/GET communication* is off on the CPU. |
| Live read: `Refused to contact ...` | The address is not configured in the open project; start the server with `--s7-host <ip>`. |

The server writes a log to stderr; MCP clients keep it (Claude Desktop: *Settings > Developer > Open logs*).

## Repository layout

```
src/
  Program.cs            entry point, command line
  Mcp/                  MCP protocol, tool definitions, safety gates, options
  Tools/                the tool catalogue, one file per area
  Tia/                  everything that talks to TIA Openness
  Lad/                  LAD (ladder) SimaticML builder
  S7/                   read-only S7 client for live values
tests/                  offline protocol tests and live tests
docs/                   tool reference, safety model, HMI notes, development guide
examples/               client configuration
```

How it is built, how to add a tool and how to run the tests: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Credits and licence

MIT licence - see [LICENSE](LICENSE). Third-party components and their licences are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); in particular the LAD builder is adapted from
[hadefuwa/tia-portal-mcp](https://github.com/hadefuwa/tia-portal-mcp), and live values use
[Sharp7](https://github.com/fbarresi/Sharp7).

**Use at your own risk.** This software changes automation projects. It comes with no warranty of any kind; you
are responsible for reviewing, testing and commissioning whatever it produces, and for the safety of the machine
it ends up on.
