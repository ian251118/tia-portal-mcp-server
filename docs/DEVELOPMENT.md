# Development guide

## Why it is built this way

- **.NET Framework 4.8, one process.** `Siemens.Engineering.dll` is a .NET Framework assembly built on .NET
  Remoting, which modern .NET does not have. Loading it in-process means the whole server runs on net48.
- **A hand-written MCP layer.** The official MCP SDK for .NET does not target net48, so the protocol (JSON-RPC
  2.0, one message per line on stdin/stdout) is implemented in `src/Mcp/McpServer.cs`. It is small: `initialize`,
  `ping`, `tools/list`, `tools/call`.
- **Siemens' DLLs are never shipped.** The project references them at build time with `Private=false`; at run
  time `OpennessResolver` finds the installed TIA Portal through the registry and loads them from there. They are
  version-locked to the installed TIA Portal and are not ours to redistribute.
- **stdout is the protocol.** Anything else written there corrupts the stream, so all logging goes to stderr
  (`Log.cs`).
- **One thread.** Requests are handled one at a time on the main thread; every Openness call comes from it.

## Layout

| Path | Contents |
|---|---|
| `src/Program.cs` | Command line, one-shot commands, start-up order (the assembly resolver is installed before any Siemens type is touched). |
| `src/Mcp/McpServer.cs` | Protocol loop, dispatch, the safety gate, audit log. Knows nothing about TIA. |
| `src/Mcp/ToolDef.cs` | `ToolDef` (name, group, safety class, schema, handler), the fluent schema builder, `ToolRegistry`. |
| `src/Mcp/ServerOptions.cs` | Options, access levels, the path sandbox, `SafetyPolicy.RequireDestructive`. |
| `src/Mcp/Args.cs` | Forgiving accessors for tool arguments (models send `"5"` for `5` and JSON strings for arrays). |
| `src/Mcp/ToolDocs.cs` | Renders the registry as `docs/TOOLS.md`. |
| `src/Tools/*.cs` | The tool catalogue: names, descriptions, schemas, safety classes. One file per group. |
| `src/Tia/TiaService*.cs` | One partial class holding the TIA session; all Openness code lives here, split by area. |
| `src/Lad/LadXmlBuilder.cs` | Pure function from a rung description to LAD SimaticML, with a pre-flight validator. |
| `src/S7/S7Live.cs` | Read-only S7 client (Sharp7) for live values. |

## Building

```powershell
dotnet build -c Release                       # bin\Release\TiaMcpServer.exe
dotnet build -c Release -o bin\Dev            # a second copy, while a client holds the first one open
dotnet build -c Release -p:TiaVersion=19      # against another TIA Portal version
```

An MCP client that has the server running keeps `bin\Release\TiaMcpServer.exe` locked. Build into another folder
(`-o bin\Dev`) to keep working, and test that copy with the scripts below.

Every build produces a new exe hash, so TIA Portal asks for Openness access again on the next connection.

## Adding a tool

1. Write the method in the `TiaService.*.cs` file for its area. It returns an anonymous object (serialised to
   JSON; nulls are dropped) and throws `InvalidOperationException` / `ArgumentException` with a message that
   tells the model what to do next.
2. Register it in the matching `src/Tools/*.cs` file:

   ```csharp
   r.Add("tia_delete_block", Group, Safety.Destructive, "Delete a block",
           "Delete a program block. Its source is backed up first ...")
       .Str("device", Device)
       .Str("block", "Name of the block to delete", required: true)
       .Run(a => tia.DeleteBlock(a.Str("device"), a.ReqStr("block")));
   ```

3. Regenerate the reference: `.\bin\Dev\TiaMcpServer.exe --dump-tools markdown > docs\TOOLS.md`
   (write it as UTF-8).
4. Add a check to `tests/live-tests.ps1`.

### Choosing the safety class

The class is the one decision that matters. It drives the description tag, the annotations, which access levels
see the tool, and whether `confirm` is required.

- `Safety.ReadOnly` - cannot change project content under any arguments.
- `Safety.Write` - creates, or changes a property. If some arguments make it replace or delete something, add
  `.DestructiveIf(a => a.Bool("overwrite"), "overwrite:true")`; the dispatcher then demands `confirm` for exactly
  those calls. If only the handler can tell (it has to look at the project), use `.ConfirmsInHandler("...")` and
  call `SafetyPolicy.RequireDestructive(confirm, "what will be lost")` there - `tia_import_scl` does this.
- `Safety.Destructive` - always deletes or replaces. `confirm` is added to the schema and enforced for you.

When in doubt, pick the stricter class.

### Conventions inside `TiaService`

- Wrap every modification in `Edit("description", () => { ... })`. That gives it exclusive access, a transaction
  (rollback on exception, one undo unit) and a fallback when TIA refuses either. Do not compile, save or archive
  inside it.
- Back up before destroying: `Backup(plc, obj, name, "before-delete")`.
- Return the previous value when changing one.
- Resolve things with the `Require*` helpers; their error messages name the tool that lists the valid names.
- Use `Brief(ex.Message)` when quoting an Openness exception: the first line is generic, the reason is below it.

### Finding your way around Openness

The API is large and thinly documented. Reflection is the practical way in:

- `tia_debug_search_types` and `tia_debug_reflect_type` (group `diag`) inspect any type from inside the server.
- The same without a server, straight from the DLL's metadata:

  ```powershell
  $a = [Reflection.Assembly]::LoadFrom('C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20\Siemens.Engineering.dll')
  $a.GetTypes() | Where-Object FullName -match 'CrossReference' | ForEach-Object FullName
  ```

- Export an object that was made by hand in TIA Portal and read the XML; it is the reference for what an import
  must look like.

## Tests

Both suites are PowerShell scripts that start the exe and talk MCP to it through `tests/McpClient.psm1`.

```powershell
# Offline: protocol, catalogue, safety gates, LAD builder. TIA Portal installed, not running.
.\tests\protocol-tests.ps1 -Exe .\bin\Dev\TiaMcpServer.exe

# Live: needs TIA Portal open with a project.
.\tests\live-tests.ps1 -Exe .\bin\Dev\TiaMcpServer.exe            # read-only
.\tests\live-tests.ps1 -Exe .\bin\Dev\TiaMcpServer.exe -Write     # + temporary objects
.\tests\live-tests.ps1 -Exe .\bin\Dev\TiaMcpServer.exe -Write -S7 # + live reads from the PLC
```

The `-Write` pass creates, modifies and deletes objects named `MCPTEST_*` and nothing else, removes them again
(also after a crash, at the start of the next run), and never saves. Afterwards TIA Portal shows the project as
modified; close without saving to be exactly where you were. Use a copy of a project you care about anyway.

To poke at a single tool:

```powershell
Import-Module .\tests\McpClient.psm1
$s = Start-McpServer -Exe .\bin\Dev\TiaMcpServer.exe
(Invoke-McpTool $s tia_connect).Data
(Invoke-McpTool $s tia_list_blocks).Data.blocks | Format-Table
Stop-McpServer $s
```

## Verification status

Checked against a real project in TIA Portal V20 (S7-1200 CPU 1212C FW 4.6, WinCC Unified Comfort panel) with
`tests/live-tests.ps1 -Write -S7`:

| Area | Verified |
|---|---|
| Connection, state, project info, doctor | yes |
| Devices, modules and I/O addresses, IP addresses, catalog search, protection, online state | yes |
| Blocks: list, read (SCL, DB, UDT as source; LAD as XML), interface, export, cross-references | yes |
| Blocks: SCL import (create, replace with backup, compile errors), XML import, instance DB, folders, delete | yes |
| LAD builder: contacts, coils, branch, TON, compare, move, network titles | yes |
| LAD builder: counters, SR/RS, arithmetic, NORM_X/SCALE_X, calls, generic parts | builds valid XML offline; not imported live here |
| PLC data types: create, list, read, delete | yes |
| PLC tags and constants: create, change, delete, table export/import | yes |
| Watch tables: create, export, import, list, delete | yes |
| Compile: single block, whole PLC software, Unified HMI - with messages | yes |
| Compile: PLC hardware (`scope: hardware`) | not covered by the test script |
| Unified HMI: screens and groups, items, properties, bounds, bindings, colour mapping, event and property scripts, validation | yes |
| Unified HMI: internal and PLC-linked tags, discrete and analog alarms, tag usage, replace | yes |
| Unified HMI: faceplate interface tools | **no** - the test project has no faceplates |
| Transactions: rollback of a failing batch | yes |
| Live S7: CPU state, raw bytes, typed values, tags by name | yes (S7-1200) |
| Project archive | refusal with unsaved changes yes; successful archive not yet |
| `tia_open_project`, `tia_save_project`, `tia_close_project` | used in practice, not part of the test script (they would disturb the open project) |
| `tia_change_device_item_type`, `tia_inspect_other_project` | used in practice earlier, not re-verified in this release |
| Classic (Comfort/Basic) panel screen export/import | **no** - no classic panel available |
| TIA Portal versions other than V20 | **no** |

If you verify one of the open rows, or find one of the verified ones broken on your setup, an issue or a pull
request updating this table is welcome.
