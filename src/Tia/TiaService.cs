using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcpServer.Mcp;

namespace TiaMcpServer
{
    /// <summary>
    /// Thin, stateful wrapper around the TIA Portal Openness API. One instance per
    /// server process; holds the live TiaPortal / Project handles.
    /// All Siemens.Engineering usage is confined to the TiaService.*.cs files; this
    /// one holds the session handling and the helpers the other parts share.
    /// </summary>
    internal sealed partial class TiaService : IDisposable
    {
        private TiaPortal _tia;
        private Project _project;
        private bool _inTransaction;

        // ===== Session ==========================================================

        /// <summary>
        /// Validates the cached TiaPortal / Project handles and repairs them.
        ///
        /// Openness objects become invalid when the user closes the project or TIA
        /// Portal itself, and touching a disposed one throws. There is no "IsAlive"
        /// property, so the only reliable probe is to touch it inside a try/catch:
        ///  - project disposed, portal alive  -> re-read the open project;
        ///  - portal disposed                 -> drop everything so the next
        ///                                       tia_connect can attach again.
        /// </summary>
        private void RefreshSession()
        {
            if (_tia == null) return;

            try
            {
                var projects = _tia.Projects;   // throws if the portal is gone

                if (_project == null)
                {
                    _project = projects.FirstOrDefault();
                    return;
                }

                try
                {
                    var probe = _project.Name;   // throws if the project was closed
                }
                catch
                {
                    Log.Warn("Open project handle was stale; re-reading it from TIA Portal.");
                    _project = projects.FirstOrDefault();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"TIA Portal session is no longer usable, dropping it: {ex.Message}");
                try { _tia.Dispose(); } catch { /* it is already gone */ }
                _tia = null;
                _project = null;
            }
        }

        private void EnsureTia()
        {
            RefreshSession();

            if (_tia == null)
                throw new InvalidOperationException(
                    "Not connected (or the previous session was closed). Call tia_connect first.");
        }

        private void EnsureProject()
        {
            EnsureTia();
            if (_project == null)
                throw new InvalidOperationException("No project open. Call tia_open_project first.");
        }

        // ===== Edits: undo transaction + exclusive access ======================

        /// <summary>
        /// Runs a modifying operation inside a TIA exclusive-access transaction, so
        /// that it becomes one undo step in TIA Portal and is rolled back as a whole
        /// if it throws half-way. Falls back to a plain call when TIA refuses the
        /// exclusive access or the transaction (e.g. a modal dialog is open).
        /// </summary>
        private T Edit<T>(string description, Func<T> body)
        {
            EnsureProject();
            if (!ServerOptions.Current.Transactions || _inTransaction)
                return body();

            ExclusiveAccess access;
            try
            {
                access = _tia.ExclusiveAccess("TIA MCP: " + description);
            }
            catch (Exception ex)
            {
                Log.Warn($"No exclusive access for '{description}' ({ex.Message}); running without a transaction.");
                return body();
            }

            try
            {
                Transaction tx;
                try
                {
                    tx = access.Transaction((ITransactionSupport)_project, "MCP: " + description);
                }
                catch (Exception ex)
                {
                    Log.Warn($"No transaction for '{description}' ({ex.Message}); running without one.");
                    return body();
                }

                _inTransaction = true;
                var rolledBackForRetry = false;
                try
                {
                    var result = body();
                    if (tx.CanCommit) tx.CommitOnDispose();
                    return result;
                }
                catch (Exception ex) when (LooksLikeTransactionRestriction(ex))
                {
                    // Some Openness operations are not allowed inside a transaction. The
                    // rollback below undoes whatever ran, then the body runs again plainly.
                    Log.Warn($"'{description}' is not possible inside a transaction ({Brief(Unwrap(ex).Message)}); retrying without one.");
                    rolledBackForRetry = true;
                }
                finally
                {
                    _inTransaction = false;
                    tx.Dispose();   // without CommitOnDispose this rolls everything back
                }

                return rolledBackForRetry ? body() : default(T);
            }
            finally
            {
                access.Dispose();
            }
        }

        private static Exception Unwrap(Exception ex) =>
            ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;

        // "transaction" in the UI languages TIA Portal ships in (en/de/es/fr/it/pt).
        private static bool LooksLikeTransactionRestriction(Exception ex) =>
            System.Text.RegularExpressions.Regex.IsMatch(
                Unwrap(ex).Message ?? "", "transa[ckzç]|exclusive access", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // ===== Software lookup =================================================

        private sealed class SoftwareEntry<T>
        {
            public Device Device;
            public DeviceItem Item;
            public T Software;
        }

        private IEnumerable<SoftwareEntry<T>> SoftwareEntries<T>(Project project = null) where T : class
        {
            foreach (var device in AllDevices(project ?? _project))
            foreach (var item in DeviceItemsRecursive(device))
            {
                if (item.GetService<SoftwareContainer>()?.Software is T sw)
                    yield return new SoftwareEntry<T> { Device = device, Item = item, Software = sw };
            }
        }

        /// <summary>
        /// Resolves a software by the name of the software itself, its device item or
        /// its device (all three are accepted wherever a tool takes "device").
        /// A null name selects the first one found.
        /// </summary>
        private SoftwareEntry<T> FindSoftware<T>(string name, Func<T, string> softwareName) where T : class
        {
            EnsureProject();
            return SoftwareEntries<T>().FirstOrDefault(e =>
                name == null ||
                softwareName(e.Software).Equals(name, StringComparison.OrdinalIgnoreCase) ||
                e.Item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                e.Device.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private PlcSoftware RequirePlc(string deviceName)
        {
            var entry = FindSoftware<PlcSoftware>(deviceName, p => p.Name);
            if (entry == null)
                throw new InvalidOperationException(
                    $"PLC '{deviceName ?? "(any)"}' not found. Use tia_list_devices to see the PLC names.");
            return entry.Software;
        }

        private HmiTarget RequireHmi(string deviceName)
        {
            var entry = FindSoftware<HmiTarget>(deviceName, h => h.Name);
            if (entry == null)
                throw new InvalidOperationException(
                    $"Classic (Comfort/Basic) HMI '{deviceName ?? "(any)"}' not found. " +
                    "For WinCC Unified panels use the tia_hmi_* tools.");
            return entry.Software;
        }

        private HmiSoftware RequireHmiUnified(string deviceName)
        {
            var entry = FindSoftware<HmiSoftware>(deviceName, h => h.Name);
            if (entry == null)
                throw new InvalidOperationException(
                    $"WinCC Unified HMI '{deviceName ?? "(any)"}' not found. Use tia_list_devices to see the HMI names.");
            return entry.Software;
        }

        private Device RequireDevice(string deviceName)
        {
            EnsureProject();
            var device = AllDevices(_project)
                .FirstOrDefault(d => d.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
            if (device == null)
                throw new InvalidOperationException($"Device '{deviceName}' not found. Use tia_list_devices.");
            return device;
        }

        private static DeviceItem RequireDeviceItem(Device device, string itemName)
        {
            var item = DeviceItemsRecursive(device)
                .FirstOrDefault(di => di.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase));
            if (item == null)
                throw new InvalidOperationException($"DeviceItem '{itemName}' not found under '{device.Name}'.");
            return item;
        }

        // ===== Project tree walkers ============================================

        private static IEnumerable<Device> AllDevices(Project project)
        {
            foreach (Device d in project.Devices) yield return d;
            foreach (var group in project.DeviceGroups)
                foreach (var d in DevicesInGroup(group))
                    yield return d;

            // "Ungrouped devices" is a separate system group: it holds what was added in
            // the network view without a station of its own (distributed I/O, GSD devices).
            var ungrouped = Try(() => project.UngroupedDevicesGroup);
            if (ungrouped != null)
                foreach (Device d in ungrouped.Devices)
                    yield return d;
        }

        private static IEnumerable<Device> DevicesInGroup(DeviceUserGroup group)
        {
            foreach (Device d in group.Devices) yield return d;
            foreach (var sub in group.Groups)
                foreach (var d in DevicesInGroup(sub))
                    yield return d;
        }

        private static IEnumerable<DeviceItem> DeviceItemsRecursive(Device device)
        {
            foreach (DeviceItem item in device.DeviceItems)
                foreach (var di in DeviceItemsRecursive(item))
                    yield return di;
        }

        private static IEnumerable<DeviceItem> DeviceItemsRecursive(DeviceItem item)
        {
            yield return item;
            foreach (DeviceItem child in item.DeviceItems)
                foreach (var di in DeviceItemsRecursive(child))
                    yield return di;
        }

        private static IEnumerable<PlcBlock> AllBlocks(PlcBlockGroup group)
        {
            foreach (PlcBlock b in group.Blocks) yield return b;
            foreach (PlcBlockUserGroup sub in group.Groups)
                foreach (var b in AllBlocks(sub))
                    yield return b;
        }

        private static IEnumerable<PlcType> AllTypes(PlcTypeGroup group)
        {
            foreach (PlcType t in group.Types) yield return t;
            foreach (PlcTypeUserGroup sub in group.Groups)
                foreach (var t in AllTypes(sub))
                    yield return t;
        }

        private static IEnumerable<PlcTagTable> AllTagTables(PlcTagTableGroup group)
        {
            foreach (PlcTagTable t in group.TagTables) yield return t;
            foreach (PlcTagTableUserGroup sub in group.Groups)
                foreach (var t in AllTagTables(sub))
                    yield return t;
        }

        private static PlcBlock TryFindBlock(PlcSoftware plc, string name) =>
            AllBlocks(plc.BlockGroup).FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        private static PlcBlock RequireBlock(PlcSoftware plc, string name)
        {
            var block = TryFindBlock(plc, name);
            if (block == null)
                throw new InvalidOperationException(
                    $"Block '{name}' not found on '{plc.Name}'. Use tia_list_blocks to see the block names.");
            return block;
        }

        /// <summary>
        /// Fail-safe (F) program blocks are out of bounds for deleting or overwriting:
        /// a safety program is changed by a person, in TIA Portal, under its own password
        /// and acceptance procedure.
        /// </summary>
        private static void RefuseSafetyBlock(IEngineeringObject obj, string action)
        {
            if (!(obj is PlcBlock block)) return;
            var language = Try(() => block.ProgrammingLanguage.ToString(), "");
            if (language.StartsWith("F_", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(
                    $"Refused to {action} '{block.Name}': it is a fail-safe ({language}) block. This server never " +
                    "deletes or overwrites safety program blocks; that has to be done by a person in TIA Portal.");
        }

        private static PlcType TryFindType(PlcSoftware plc, string name) =>
            AllTypes(plc.TypeGroup).FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>"Folder/Sub" path of the user groups an object sits in ("" for the root).</summary>
        private static string GroupPath(IEngineeringObject parent)
        {
            var parts = new List<string>();
            while (parent != null)
            {
                if (parent is PlcBlockUserGroup bg) { parts.Insert(0, bg.Name); parent = bg.Parent; }
                else if (parent is PlcTypeUserGroup tg) { parts.Insert(0, tg.Name); parent = tg.Parent; }
                else if (parent is PlcTagTableUserGroup gg) { parts.Insert(0, gg.Name); parent = gg.Parent; }
                else break;
            }
            return string.Join("/", parts);
        }

        private static string[] SplitGroupPath(string path) =>
            (path ?? "").Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();

        /// <summary>Resolves "Folder/Sub" under Program blocks; optionally creating missing groups.</summary>
        private static PlcBlockGroup ResolveBlockGroup(PlcSoftware plc, string path, bool create)
        {
            PlcBlockGroup current = plc.BlockGroup;
            foreach (var part in SplitGroupPath(path))
            {
                var next = current.Groups.Find(part);
                if (next == null)
                {
                    if (!create)
                        throw new InvalidOperationException(
                            $"Block group '{path}' not found on '{plc.Name}' (missing '{part}'). " +
                            "Create it with tia_create_block_group.");
                    next = current.Groups.Create(part);
                }
                current = next;
            }
            return current;
        }

        // ===== Files ===========================================================

        private static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        /// <summary>A fresh private scratch directory; callers delete it when done.</summary>
        private static DirectoryInfo NewTempDir()
        {
            var dir = Path.Combine(ServerOptions.Current.TempDir, Guid.NewGuid().ToString("N"));
            return Directory.CreateDirectory(dir);
        }

        private static void TryDeleteDir(DirectoryInfo dir)
        {
            try { if (dir != null && dir.Exists) dir.Delete(true); } catch { /* scratch only */ }
        }

        /// <summary>
        /// Returns the file a tool should import: the given path, or inline content
        /// written to a scratch file (UTF-8 with BOM, which is what TIA itself writes).
        /// </summary>
        private static FileInfo MaterializeInput(string path, string content, string fileName, DirectoryInfo scratch, string what)
        {
            if (!string.IsNullOrWhiteSpace(content))
            {
                var file = new FileInfo(Path.Combine(scratch.FullName, SafeFileName(fileName)));
                File.WriteAllText(file.FullName, content, new UTF8Encoding(true));
                return file;
            }

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException($"Provide the {what} either as a file path or inline.");
            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");
            return new FileInfo(path);
        }

        /// <summary>File extension TIA expects when generating the source of an object.</summary>
        private static string SourceExtension(object obj)
        {
            if (obj is PlcType) return ".udt";
            if (obj is DataBlock) return ".db";
            if (obj is PlcBlock b &&
                (b.ProgrammingLanguage == ProgrammingLanguage.STL || b.ProgrammingLanguage == ProgrammingLanguage.F_STL))
                return ".awl";
            return ".scl";
        }

        /// <summary>
        /// Best-effort safety copy of a block or data type taken right before it is
        /// deleted or replaced: its source text when TIA can generate it, otherwise its
        /// SimaticML export. Returns the file path, or a note explaining why there is none.
        /// </summary>
        private string Backup(PlcSoftware plc, IEngineeringObject obj, string name, string reason)
        {
            if (!ServerOptions.Current.Backups) return "(backups disabled)";

            DirectoryInfo scratch = null;
            try
            {
                var dir = Path.Combine(ServerOptions.Current.EffectiveBackupDir,
                                       SafeFileName(_project.Name), DateTime.Now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dir);
                var stem = Path.Combine(dir, $"{DateTime.Now:HHmmss}_{SafeFileName(name)}_{reason}");

                // TIA writes into a scratch folder under the object's own name (exactly as the
                // export tools do); the file is then moved to its timestamped backup name.
                scratch = NewTempDir();
                string Keep(FileInfo produced, string extension)
                {
                    var target = stem + extension;
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(produced.FullName, target);
                    return target;
                }

                string sourceError;
                try
                {
                    var extension = SourceExtension(obj);
                    var src = new FileInfo(Path.Combine(scratch.FullName, SafeFileName(name) + extension));
                    plc.ExternalSourceGroup.GenerateSource(new[] { (IGenerateSource)obj }, src, GenerateOptions.None);
                    return Keep(src, extension);
                }
                catch (Exception ex)
                {
                    sourceError = Brief(ex.Message);
                }

                // LAD/FBD/GRAPH have no text source; fall back to the XML export.
                try
                {
                    var xml = new FileInfo(Path.Combine(scratch.FullName, SafeFileName(name) + ".xml"));
                    if (obj is PlcBlock block) block.Export(xml, ExportOptions.WithDefaults);
                    else ((PlcType)obj).Export(xml, ExportOptions.WithDefaults);
                    return Keep(xml, ".xml");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Backup of '{name}' failed. Source: {sourceError} / XML: {Brief(ex.Message)}");
                    return $"(no backup - source: {sourceError} / xml: {Brief(ex.Message)})";
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Backup of '{name}' failed: {Brief(ex.Message)}");
                return $"(no backup: {Brief(ex.Message)})";
            }
            finally
            {
                TryDeleteDir(scratch);
            }
        }

        // ===== Small utilities =================================================

        private static object SafeGet(Func<object> f)
        {
            try { return f(); } catch (Exception ex) { return "ERR:" + Brief(ex.Message); }
        }

        private static T Try<T>(Func<T> f, T fallback = default(T))
        {
            try { return f(); } catch { return fallback; }
        }

        /// <summary>
        /// An exception message on one line. Openness messages are multi-line: a generic
        /// "Error when calling method X" first, the actual reason below - keep both.
        /// </summary>
        private static string Brief(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var line = string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(l => l.Trim()).Where(l => l.Length > 0));
            return line.Length > 600 ? line.Substring(0, 600) + "..." : line;
        }

        private static string FirstText(MultilingualText text)
        {
            foreach (MultilingualTextItem item in text.Items)
                return item.Text;
            return null;
        }

        /// <summary>Sets a plain-text multilingual field (PLC comments etc.) in every project language.</summary>
        private static void SetPlainText(MultilingualText mlText, string text)
        {
            foreach (MultilingualTextItem item in mlText.Items)
                item.Text = text ?? "";
        }

        public void Dispose()
        {
            try { _tia?.Dispose(); } catch { /* ignore */ }
            _tia = null;
            _project = null;
        }
    }
}
