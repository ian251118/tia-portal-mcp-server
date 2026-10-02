# Safety model

This server lets a language model change an automation project. Automation projects run machines, so this
document spells out exactly what the server can do, what stops it from doing damage, and where those protections
end. Read it once before using the server on a project you care about.

**The short version**

1. The server edits the *project in TIA Portal*. It cannot download to a PLC or HMI, go online, start or stop a
   CPU, or write to a running PLC.
2. Nothing it does is permanent until the project is saved.
3. Anything that deletes or replaces existing content is refused unless the call carries `confirm: true`.
4. What is about to be replaced or deleted is backed up first.
5. None of this replaces your own review and testing.

---

## 1. Safety classes

Every tool has exactly one class. It is set in one place in the code and drives everything else: the tag at the
start of the description, the MCP annotations, the access-level filter, the confirmation gate and the generated
[tool reference](TOOLS.md).

| Class | Tag in the description | What the tool may do | MCP annotations |
|---|---|---|---|
| Read-only | `[READ-ONLY]` | Read the project, TIA Portal, or the running PLC. May write an export file to a path you give it. May change the *session* (attach to TIA, open a project). Never changes project content. | `readOnlyHint: true` |
| Write | `[WRITE]` | Create new objects, or change properties of existing ones (text, colour, position, a binding, a script). The previous value is returned so it can be put back. Nothing is removed. | `readOnlyHint: false`, `destructiveHint: false` |
| Write, conditionally destructive | `[WRITE - DESTRUCTIVE when ...]` | A write tool that, with certain arguments, deletes or replaces something that exists - for example `overwrite: true`. That form needs confirmation. | `destructiveHint: true` |
| Destructive | `[DESTRUCTIVE - needs confirm:true]` | Delete or replace existing content, discard unsaved work, or rewrite hardware configuration. Always needs confirmation. | `destructiveHint: true` |

Two cases that deserve a note:

- **`tia_compile` is a write tool.** It does not change your logic, but it changes the compile state of the
  project and marks it modified, so it is not available in read-only mode.
- **`tia_save_project` is a write tool, and the most consequential one.** See section 3.

### Always destructive

| Tool | What is lost |
|---|---|
| `tia_close_project` | Unsaved changes, unless `save: true` |
| `tia_delete_block` | A program block (backed up first) |
| `tia_delete_type` | A PLC data type (backed up first) |
| `tia_delete_plc_tag` | A PLC tag |
| `tia_delete_tag_table` | A tag table with all its tags (exported first) |
| `tia_delete_block_group` | A block folder - only if it is empty; never deletes blocks |
| `tia_delete_watchtable` | A watch table |
| `tia_hmi_delete_screen` | A screen with everything on it |
| `tia_hmi_delete_screen_item` | A screen item with its bindings and scripts (returned in the result) |
| `tia_hmi_remove_event_script` | An event script (returned in the result) |
| `tia_hmi_remove_property_dynamization` | A tag binding or property script (returned in the result) |
| `tia_hmi_delete_tag` | An HMI tag |
| `tia_hmi_delete_alarm` | An HMI alarm |
| `tia_change_device_item_type` | The module's current type/firmware and possibly its parameters |

### Destructive only in one form

| Tool | Destructive when |
|---|---|
| `tia_import_scl` | the source redefines a block or data type that already exists (the server checks, and names them) |
| `tia_import_block`, `tia_create_lad_block`, `tia_import_tag_table`, `tia_import_watchtable`, `tia_import_screen` | `overwrite: true` |
| `tia_hmi_create_screen`, `tia_hmi_create_button`, `tia_hmi_create_label`, `tia_hmi_create_widget`, `tia_hmi_create_tag`, `tia_hmi_create_internal_tag`, `tia_hmi_create_alarm` | `overwrite: true` (delete and recreate) |
| `tia_hmi_create_items`, `tia_hmi_create_tags` | any item in the batch sets `overwrite: true` |
| `tia_hmi_replace_tag_refs` | `dryRun: false` (the default is a dry run) |

Without `overwrite`, the create tools leave an existing object untouched and say so.

## 2. The confirmation gate

A destructive call without `confirm: true` is refused before anything happens. The refusal tells the model what
would be lost and instructs it to explain that to you and ask. The `confirm` argument is described to the model
as: *set it only after the user has approved this specific deletion or overwrite*.

Be clear about what this is. It is a **speed bump for the model, not an access control**: a model that decides to
pass `confirm: true` without asking you can do so. What makes it effective in practice is the combination with
your MCP client, which normally shows you each tool call and lets you approve or deny it - and which can use the
`destructiveHint` annotation to always ask for those. If you need a hard guarantee, use the access level instead:

## 3. Access levels and the save switch

Set when the server is started; the model cannot change them.

| `--access` | Read-only tools | Write tools | Destructive tools and forms |
|---|---|---|---|
| `readonly` | yes | no | no |
| `write` | yes | yes | no - refused even with `confirm: true` |
| `full` (default) | yes | yes | yes, with `confirm: true` |

Tools outside the level are not listed to the model, and a call by name is refused anyway.

**Saving is the point of no return.** Everything the server changes lives in TIA Portal's memory. If you close the
project without saving, none of it happened. `tia_save_project` makes it all permanent, including changes you may
not have looked at yet. Two ways to keep that decision yours:

- `--no-save`: the server refuses to save. You save in TIA Portal (Ctrl+S) when you are satisfied.
- Without it, the model is instructed to save only when you ask.

`--groups` is not a safety control, only a way to shorten the tool list; but fewer tools is also fewer ways to go
wrong (`--groups plc` if you never want the HMI touched).

## 4. Undo and rollback

Edits are executed inside a TIA Portal *exclusive access* with a *transaction*:

- If an edit fails half-way, the transaction is rolled back and the project is as it was. This matters most for
  the batch tools: `tia_hmi_create_items` either creates every item or none.
- TIA Portal registers each transaction as one undo unit, labelled "MCP: ...".
- While an edit runs, TIA Portal shows an exclusive-access dialog and its UI is locked. That is intended: you and
  the server are not editing at the same moment.

If TIA Portal refuses the exclusive access or the transaction (a modal dialog is open, or the operation is not
allowed inside one), the server falls back to a plain edit and logs it. `--no-transactions` turns the mechanism
off. Compiling, saving and archiving are never inside a transaction.

Do not treat undo as your safety net. Your safety nets are *not saving*, the *backups* below, and an *archive*.

## 5. Backups

- **Per object, automatic.** Before a block or PLC data type is deleted or replaced, the server writes its source
  (or, for LAD/FBD, its XML export) to `%LOCALAPPDATA%\TiaMcpServer\backups\<project>\<date>\` and returns the
  path. Tag tables are exported before `tia_delete_tag_table` and before an overwriting import.
- **HMI objects have no file backup.** Deleting a screen item, a script, a binding or an HMI tag returns what was
  removed in the tool result, so it can be recreated. Deleting a whole screen returns only the number of items it
  held: the way back is not saving.
- **Best effort.** If TIA cannot export the object (a LAD block that does not compile, a know-how protected
  block), the operation still proceeds and the result says `(no backup: ...)`.
- **Whole project, on request.** `tia_archive_project` writes a `.zap20` archive. TIA Portal only archives a
  project without unsaved changes, so take it *before* a session, not in the middle.

`--backup-dir` moves the folder, `--no-backups` disables the per-object backups.

## 6. What the server cannot reach

| Boundary | How it is enforced |
|---|---|
| The PLC program on the machine | No download, upload, go-online or start/stop code exists in the server. |
| Values in the running PLC | The S7 client only implements reads. There is no write function to call. |
| Hosts on your network | Live reads are limited to IP addresses configured in the open project. `--s7-host` adds one, `--s7-any-host` removes the limit. |
| Fail-safe (F) blocks | Deleting or overwriting a block whose language is `F_*` is refused in every access level. |
| Passwords and protection | The server reads the protection level for diagnostics; it has no code to set passwords or change protection. |
| Files on your disk | With `--allowed-dir`, every file and directory argument must be inside that folder (path traversal is resolved first). Without it, the server can read and write wherever your Windows user can. |

## 7. Audit trail

`--audit-log <file>` appends one JSON line per tool call: time, tool, safety class, arguments (long texts cut
short), success or the error, and duration. Refused calls are logged too. Independently, the server logs every
call to stderr, which MCP clients keep.

## 8. What is left to you

The protections above are about the *project file*. They do not make generated automation code safe.

- **A model can be wrong.** It can write logic that compiles and does the wrong thing, bind a button to the
  wrong tag, or misread your intent. A clean compile proves syntax, nothing else.
- **A model can be misled.** Text inside the project - block comments, tag names, alarm texts - is returned to
  the model as tool output. Content from a project you did not write could contain instructions aimed at the
  model. Treat a project from an untrusted source the way you would treat an untrusted document.
- **Compile results through Openness are thinner than in the TIA Portal UI**; some warnings only show there.
- **The step to the machine is yours.** Review the changes in TIA Portal, save, download and commission yourself,
  following the procedures of your plant. Safety functions are outside the scope of this tool entirely.

### Recommended practice

1. Try it first on a **copy** of the project.
2. Start with `--access readonly` to see how the assistant reads your project, then move to `--no-save`.
3. Before a larger change, save and take an **archive**.
4. Keep the MCP client's per-call approval on, at least for destructive tools.
5. Look at every change in TIA Portal before you save.
6. Test on the machine as you would test your own work - with the machine in a safe state.

## Reporting a problem

If you find a way around one of these protections, please report it privately first; see
[SECURITY.md](../SECURITY.md).
