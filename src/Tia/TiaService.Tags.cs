using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.WatchAndForceTables;
using TiaMcpServer.Mcp;

namespace TiaMcpServer
{
    // PLC tag tables, tags, user constants and watch tables.
    internal sealed partial class TiaService
    {
        // ---- Tags --------------------------------------------------------------

        public object ListPlcTags(string deviceName, string tableName, string filter)
        {
            var plc = RequirePlc(deviceName);
            bool Match(string n) =>
                string.IsNullOrEmpty(filter) || n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            var tables = new List<object>();
            foreach (var table in AllTagTables(plc.TagTableGroup))
            {
                if (tableName != null && !table.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    continue;

                tables.Add(new
                {
                    table = table.Name,
                    group = GroupPath(table.Parent),
                    isDefault = Try(() => (bool?)table.IsDefault),
                    tags = table.Tags.Where(t => Match(t.Name)).Select(t => new
                    {
                        name = t.Name,
                        dataType = t.DataTypeName,
                        address = t.LogicalAddress,
                        comment = NullIfEmpty(Try(() => FirstText(t.Comment)))
                    }).ToArray(),
                    constants = table.UserConstants.Where(c => Match(c.Name)).Select(c => new
                    {
                        name = c.Name,
                        dataType = c.DataTypeName,
                        value = c.Value,
                        comment = NullIfEmpty(Try(() => FirstText(c.Comment)))
                    }).ToArray()
                });
            }

            if (tableName != null && tables.Count == 0)
                throw new InvalidOperationException($"Tag table '{tableName}' not found on '{plc.Name}'.");

            return new { device = plc.Name, tableCount = tables.Count, tagTables = tables };
        }

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        private static PlcTag FindPlcTag(PlcSoftware plc, string name)
        {
            foreach (var table in AllTagTables(plc.TagTableGroup))
            {
                var tag = table.Tags.Find(name);
                if (tag != null) return tag;
            }
            return null;
        }

        private static PlcTagTable FindTagTable(PlcSoftware plc, string name) =>
            AllTagTables(plc.TagTableGroup).FirstOrDefault(t =>
                name == null ? Try(() => t.IsDefault) : t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public object CreateTagTable(string deviceName, string name)
        {
            var plc = RequirePlc(deviceName);
            if (FindTagTable(plc, name) != null)
                return new { created = false, exists = true, table = name };

            return Edit($"create tag table {name}", () =>
            {
                var table = plc.TagTableGroup.TagTables.Create(name);
                return new { created = true, exists = false, table = table.Name };
            });
        }

        /// <summary>
        /// Creates PLC tags in one call. Existing names are skipped, never overwritten
        /// (tag names are unique across all tag tables of a PLC).
        /// </summary>
        public object CreatePlcTags(string deviceName, string tableName, JArray tags)
        {
            var plc = RequirePlc(deviceName);

            return Edit($"create {tags.Count} PLC tag(s)", () =>
            {
                var table = FindTagTable(plc, tableName);
                var tableCreated = false;
                if (table == null)
                {
                    if (tableName == null)
                        throw new InvalidOperationException($"'{plc.Name}' has no default tag table; pass 'table'.");
                    table = plc.TagTableGroup.TagTables.Create(tableName);
                    tableCreated = true;
                }

                var results = new List<object>();
                foreach (var token in tags)
                {
                    var t = new Args(token as JObject);
                    var name = t.Str("name");
                    try
                    {
                        if (string.IsNullOrWhiteSpace(name))
                            throw new ArgumentException("each tag needs a 'name'.");

                        var existing = FindPlcTag(plc, name);
                        if (existing != null)
                        {
                            results.Add(new
                            {
                                name,
                                status = "skipped",
                                reason = $"already exists ({existing.DataTypeName} {existing.LogicalAddress}); use tia_update_plc_tag to change it"
                            });
                            continue;
                        }

                        var dataType = t.Str("dataType", "Bool");
                        var address = t.Str("address");
                        var tag = address != null
                            ? table.Tags.Create(name, dataType, address)
                            : table.Tags.Create(name);
                        if (address == null) tag.DataTypeName = dataType;

                        if (t.Has("comment")) SetPlainText(tag.Comment, t.Str("comment"));
                        if (t.Has("externalAccessible")) tag.ExternalAccessible = t.Bool("externalAccessible");
                        if (t.Has("externalWritable")) tag.ExternalWritable = t.Bool("externalWritable");
                        if (t.Has("externalVisible")) tag.ExternalVisible = t.Bool("externalVisible");

                        results.Add(new
                        {
                            name = tag.Name,
                            status = "created",
                            dataType = tag.DataTypeName,
                            address = tag.LogicalAddress
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { name, status = "error", reason = Brief(ex.Message) });
                    }
                }

                return new { device = plc.Name, table = table.Name, tableCreated, results };
            });
        }

        public object CreatePlcConstants(string deviceName, string tableName, JArray constants)
        {
            var plc = RequirePlc(deviceName);

            return Edit($"create {constants.Count} PLC constant(s)", () =>
            {
                var table = FindTagTable(plc, tableName);
                if (table == null)
                    throw new InvalidOperationException(
                        $"Tag table '{tableName ?? "(default)"}' not found on '{plc.Name}'. Create it with tia_create_tag_table.");

                var results = new List<object>();
                foreach (var token in constants)
                {
                    var c = new Args(token as JObject);
                    var name = c.Str("name");
                    try
                    {
                        if (string.IsNullOrWhiteSpace(name))
                            throw new ArgumentException("each constant needs a 'name'.");

                        var existing = AllTagTables(plc.TagTableGroup)
                            .Select(tt => tt.UserConstants.Find(name)).FirstOrDefault(x => x != null);
                        if (existing != null)
                        {
                            results.Add(new { name, status = "skipped", reason = $"already exists (= {existing.Value})" });
                            continue;
                        }

                        var constant = table.UserConstants.Create(name, c.Str("dataType", "Int"), c.ReqStr("value"));
                        if (c.Has("comment")) SetPlainText(constant.Comment, c.Str("comment"));
                        results.Add(new { name = constant.Name, status = "created", dataType = constant.DataTypeName, value = constant.Value });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { name, status = "error", reason = Brief(ex.Message) });
                    }
                }

                return new { device = plc.Name, table = table.Name, results };
            });
        }

        public object UpdatePlcTag(string deviceName, string tagName, string newName, string dataType,
                                   string address, string comment)
        {
            var plc = RequirePlc(deviceName);
            var tag = FindPlcTag(plc, tagName);
            if (tag == null)
                throw new InvalidOperationException($"PLC tag '{tagName}' not found on '{plc.Name}'.");

            var before = new { name = tag.Name, dataType = tag.DataTypeName, address = tag.LogicalAddress, comment = Try(() => FirstText(tag.Comment)) };

            return Edit($"update PLC tag {tagName}", () =>
            {
                if (dataType != null) tag.DataTypeName = dataType;
                if (address != null) tag.LogicalAddress = address;
                if (comment != null) SetPlainText(tag.Comment, comment);
                if (newName != null && newName != tag.Name) tag.Name = newName;   // TIA updates the symbolic uses

                return new
                {
                    updated = true,
                    before,
                    after = new { name = tag.Name, dataType = tag.DataTypeName, address = tag.LogicalAddress, comment = Try(() => FirstText(tag.Comment)) }
                };
            });
        }

        public object DeletePlcTag(string deviceName, string tagName)
        {
            var plc = RequirePlc(deviceName);
            var tag = FindPlcTag(plc, tagName);
            if (tag == null)
                throw new InvalidOperationException($"PLC tag '{tagName}' not found on '{plc.Name}'.");

            var removed = new { name = tag.Name, dataType = tag.DataTypeName, address = tag.LogicalAddress, comment = Try(() => FirstText(tag.Comment)) };
            return Edit($"delete PLC tag {tagName}", () =>
            {
                tag.Delete();
                return new { deleted = true, removed, note = "Code that used this tag now refers to an undefined symbol; recompile to find it." };
            });
        }

        /// <summary>Exports a tag table into the backup folder; returns the path or a note.</summary>
        private string BackupTagTable(PlcTagTable table)
        {
            if (!ServerOptions.Current.Backups) return "(backups disabled)";
            return Try(() =>
            {
                var dir = Path.Combine(ServerOptions.Current.EffectiveBackupDir,
                                       SafeFileName(_project.Name), DateTime.Now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dir);
                var f = new FileInfo(Path.Combine(dir, $"{DateTime.Now:HHmmss}_{SafeFileName(table.Name)}_tagtable.xml"));
                table.Export(f, ExportOptions.WithDefaults);
                return f.FullName;
            }, "(no backup)");
        }

        public object DeleteTagTable(string deviceName, string tableName)
        {
            var plc = RequirePlc(deviceName);
            var table = FindTagTable(plc, tableName);
            if (table == null)
                throw new InvalidOperationException($"Tag table '{tableName}' not found on '{plc.Name}'.");
            if (Try(() => table.IsDefault))
                throw new InvalidOperationException("The default tag table cannot be deleted.");

            var tags = table.Tags.Count;
            var constants = table.UserConstants.Count;
            var backup = BackupTagTable(table);

            return Edit($"delete tag table {tableName}", () =>
            {
                table.Delete();
                return new { deleted = true, table = tableName, tagsRemoved = tags, constantsRemoved = constants, backup };
            });
        }

        public object ExportTagTable(string deviceName, string tableName, string outDir)
        {
            var plc = RequirePlc(deviceName);
            var table = FindTagTable(plc, tableName);
            if (table == null)
                throw new InvalidOperationException($"Tag table '{tableName ?? "(default)"}' not found on '{plc.Name}'.");

            Directory.CreateDirectory(outDir);
            var file = new FileInfo(Path.Combine(outDir, SafeFileName(table.Name) + ".xml"));
            if (file.Exists) file.Delete();
            table.Export(file, ExportOptions.WithDefaults);
            return new { exported = true, table = table.Name, path = file.FullName };
        }

        public object ImportTagTable(string deviceName, string xmlPath, string xml, bool overwrite)
        {
            var plc = RequirePlc(deviceName);
            var scratch = NewTempDir();
            try
            {
                var file = MaterializeInput(xmlPath, xml, "tagtable.xml", scratch, "tag table XML");
                var declared = DeclaredNameInXml(file, "SW.Tags.PlcTagTable");
                var existing = declared == null ? null : FindTagTable(plc, declared);
                if (existing != null && !overwrite)
                    throw new InvalidOperationException(
                        $"Tag table '{declared}' already exists on '{plc.Name}'. Nothing was imported. " +
                        "To replace it, call again with overwrite:true and confirm:true.");

                var backup = existing == null ? null : BackupTagTable(existing);

                return Edit($"import tag table {declared ?? file.Name}", () =>
                {
                    var imported = plc.TagTableGroup.TagTables.Import(
                        file, overwrite ? ImportOptions.Override : ImportOptions.None);
                    return new
                    {
                        imported = imported.Count,
                        tables = imported.Select(t => t.Name).ToArray(),
                        device = plc.Name,
                        replacedExisting = existing != null,
                        backup
                    };
                });
            }
            finally
            {
                TryDeleteDir(scratch);
            }
        }

        // ---- Watch tables ------------------------------------------------------

        public object ListWatchTables(string deviceName)
        {
            var plc = RequirePlc(deviceName);
            var tables = plc.WatchAndForceTableGroup.WatchTables.Select(wt => new
            {
                name = wt.Name,
                entries = wt.Entries.OfType<PlcWatchTableEntry>().Select(e => new
                {
                    name = NullIfEmpty(Try(() => e.Name)),
                    address = Try(() => e.Address),
                    displayFormat = Try(() => e.DisplayFormat.ToString()),
                    modifyValue = NullIfEmpty(Try(() => e.ModifyValue))
                }).ToArray()
            }).ToArray();
            return new { device = plc.Name, count = tables.Length, watchTables = tables };
        }

        public object CreateWatchTable(string deviceName, string name)
        {
            var plc = RequirePlc(deviceName);
            var existing = plc.WatchAndForceTableGroup.WatchTables.Find(name);
            if (existing != null)
                return new { created = false, exists = true, name };

            return Edit($"create watch table {name}", () =>
            {
                var wt = plc.WatchAndForceTableGroup.WatchTables.Create(name);
                return new { created = true, exists = false, name = wt.Name };
            });
        }

        public object ExportWatchTable(string deviceName, string name, string outDir)
        {
            var plc = RequirePlc(deviceName);
            var wt = plc.WatchAndForceTableGroup.WatchTables.Find(name);
            if (wt == null)
                throw new InvalidOperationException($"Watch table '{name}' not found on '{plc.Name}'.");
            Directory.CreateDirectory(outDir);
            var file = new FileInfo(Path.Combine(outDir, SafeFileName(name) + ".xml"));
            if (file.Exists) file.Delete();
            wt.Export(file, ExportOptions.WithDefaults);
            return new { exported = true, name = wt.Name, path = file.FullName };
        }

        public object ImportWatchTable(string deviceName, string xmlPath, string xml, bool overwrite)
        {
            var plc = RequirePlc(deviceName);
            var scratch = NewTempDir();
            try
            {
                var file = MaterializeInput(xmlPath, xml, "watchtable.xml", scratch, "watch table XML");
                var option = overwrite ? ImportOptions.Override : ImportOptions.None;
                var imported = plc.WatchAndForceTableGroup.WatchTables.Import(file, option);
                return new
                {
                    imported = imported.Count,
                    tables = imported.Select(t => t.Name).ToArray(),
                    device = plc.Name
                };
            }
            finally
            {
                TryDeleteDir(scratch);
            }
        }

        public object DeleteWatchTable(string deviceName, string name)
        {
            var plc = RequirePlc(deviceName);
            var wt = plc.WatchAndForceTableGroup.WatchTables.Find(name);
            if (wt == null)
                throw new InvalidOperationException($"Watch table '{name}' not found on '{plc.Name}'.");

            return Edit($"delete watch table {name}", () =>
            {
                wt.Delete();
                return new { deleted = true, name, device = plc.Name };
            });
        }
    }
}
