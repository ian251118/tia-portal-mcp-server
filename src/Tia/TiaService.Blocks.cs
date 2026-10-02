using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcpServer.Lad;
using TiaMcpServer.Mcp;

namespace TiaMcpServer
{
    // PLC program blocks, PLC data types (UDTs), compilation and cross-references.
    internal sealed partial class TiaService
    {
        // ---- Listing / reading -------------------------------------------------

        public object ListBlocks(string deviceName)
        {
            var plc = RequirePlc(deviceName);
            var blocks = new List<object>();
            foreach (var b in AllBlocks(plc.BlockGroup))
                blocks.Add(new
                {
                    name = b.Name,
                    type = b.GetType().Name,
                    number = Try(() => (int?)b.Number),
                    language = Try(() => b.ProgrammingLanguage.ToString()),
                    group = GroupPath(b.Parent),
                    isConsistent = Try(() => (bool?)b.IsConsistent),
                    knowHowProtected = Try(() => (bool?)b.IsKnowHowProtected),
                    modifiedDate = Try(() => (DateTime?)b.ModifiedDate)
                });
            return new { device = plc.Name, count = blocks.Count, blocks };
        }

        public object ListTypes(string deviceName)
        {
            var plc = RequirePlc(deviceName);
            var types = AllTypes(plc.TypeGroup).Select(t => new
            {
                name = t.Name,
                group = GroupPath(t.Parent),
                isConsistent = Try(() => (bool?)t.IsConsistent),
                modifiedDate = Try(() => (DateTime?)t.ModifiedDate)
            }).ToArray();
            return new { device = plc.Name, count = types.Length, types };
        }

        /// <summary>A block or PLC data type by name (blocks win when both exist).</summary>
        private static IEngineeringObject RequireBlockOrType(PlcSoftware plc, string name)
        {
            var obj = (IEngineeringObject)TryFindBlock(plc, name) ?? TryFindType(plc, name);
            if (obj == null)
                throw new InvalidOperationException(
                    $"No block or PLC data type named '{name}' on '{plc.Name}'. Use tia_list_blocks / tia_list_types.");
            return obj;
        }

        private static void ExportXml(IEngineeringObject obj, FileInfo file)
        {
            if (obj is PlcBlock block) block.Export(file, ExportOptions.WithDefaults);
            else ((PlcType)obj).Export(file, ExportOptions.WithDefaults);
        }

        /// <summary>
        /// Returns a block's content inline: generated source text for SCL/STL blocks,
        /// DBs and data types, SimaticML XML for graphical languages (LAD/FBD/GRAPH).
        /// </summary>
        public object ReadBlock(string deviceName, string name, string format, int maxChars)
        {
            var plc = RequirePlc(deviceName);
            var obj = RequireBlockOrType(plc, name);
            format = (format ?? "auto").ToLowerInvariant();
            if (format != "auto" && format != "source" && format != "xml")
                throw new ArgumentException("format must be auto, source or xml.");

            var scratch = NewTempDir();
            try
            {
                string content = null, used = null, note = null;

                if (format != "xml")
                {
                    try
                    {
                        var file = new FileInfo(Path.Combine(scratch.FullName, SafeFileName(name) + SourceExtension(obj)));
                        plc.ExternalSourceGroup.GenerateSource(new[] { (IGenerateSource)obj }, file, GenerateOptions.None);
                        content = File.ReadAllText(file.FullName);
                        used = "source";
                    }
                    catch (Exception ex)
                    {
                        if (format == "source")
                            throw new InvalidOperationException(
                                $"TIA could not generate a text source for '{name}': {Brief(ex.Message)} " +
                                "Graphical blocks (LAD/FBD/GRAPH) have none; use format \"xml\".");
                        note = $"No text source ({Brief(ex.Message)}); returning SimaticML XML instead.";
                    }
                }

                if (content == null)
                {
                    var file = new FileInfo(Path.Combine(scratch.FullName, SafeFileName(name) + ".xml"));
                    ExportXml(obj, file);
                    content = File.ReadAllText(file.FullName);
                    used = "xml";
                }

                var total = content.Length;
                var truncated = maxChars > 0 && total > maxChars;
                if (truncated) content = content.Substring(0, maxChars);

                return new
                {
                    name,
                    kind = obj.GetType().Name,
                    language = obj is PlcBlock b ? Try(() => b.ProgrammingLanguage.ToString()) : null,
                    format = used,
                    totalChars = total,
                    truncated,
                    note = truncated
                        ? $"Output cut at {maxChars} characters; raise maxChars or use tia_export_block / tia_export_source to get a file."
                        : note,
                    content
                };
            }
            finally
            {
                TryDeleteDir(scratch);
            }
        }

        /// <summary>
        /// The interface (Input/Output/InOut/Static/Temp/Constant members, with data
        /// types, start values and comments) of a block, or the members of a DB / UDT.
        /// </summary>
        public object GetBlockInterface(string deviceName, string name)
        {
            var plc = RequirePlc(deviceName);
            var obj = RequireBlockOrType(plc, name);

            var scratch = NewTempDir();
            try
            {
                var file = new FileInfo(Path.Combine(scratch.FullName, "interface.xml"));
                try { ExportXml(obj, file); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Could not export '{name}' to read its interface: {Brief(ex.Message)} " +
                        "TIA only exports consistent objects; compile first (tia_compile).");
                }

                var doc = XDocument.Load(file.FullName);
                var sectionsRoot = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Sections");
                var sections = sectionsRoot == null
                    ? new object[0]
                    : sectionsRoot.Elements().Where(e => e.Name.LocalName == "Section")
                        .Select(s => new
                        {
                            section = (string)s.Attribute("Name"),
                            members = s.Elements().Where(e => e.Name.LocalName == "Member")
                                       .Select(m => InterfaceMember(m, 0)).ToArray()
                        })
                        .Where(s => s.members.Length > 0)
                        .Cast<object>().ToArray();

                return new
                {
                    name,
                    kind = obj.GetType().Name,
                    number = obj is PlcBlock b ? Try(() => (int?)b.Number) : null,
                    instanceOf = obj is InstanceDB idb ? Try(() => idb.InstanceOfName) : null,
                    sections
                };
            }
            finally
            {
                TryDeleteDir(scratch);
            }
        }

        private static Dictionary<string, object> InterfaceMember(XElement m, int depth)
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = (string)m.Attribute("Name"),
                ["datatype"] = (string)m.Attribute("Datatype")
            };

            var start = m.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue");
            if (start != null && start.Value.Length > 0) d["startValue"] = start.Value;

            var comment = m.Elements().FirstOrDefault(e => e.Name.LocalName == "Comment")?
                .Elements().Select(t => t.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (comment != null) d["comment"] = comment;

            // Struct members are nested directly; the expanded content of UDT / FB-instance
            // members sits under <Sections> and is left out on purpose (read that type instead).
            var children = m.Elements().Where(e => e.Name.LocalName == "Member").ToList();
            if (children.Count > 0 && depth < 4)
                d["members"] = children.Select(c => InterfaceMember(c, depth + 1)).ToArray();

            return d;
        }

        // ---- Export ------------------------------------------------------------

        public object ExportBlock(string deviceName, string blockName, string outDir)
        {
            var plc = RequirePlc(deviceName);
            var obj = RequireBlockOrType(plc, blockName);

            Directory.CreateDirectory(outDir);
            var file = new FileInfo(Path.Combine(outDir, SafeFileName(blockName) + ".xml"));
            if (file.Exists) file.Delete();

            ExportXml(obj, file);
            return new { exported = true, block = blockName, path = file.FullName };
        }

        public object ExportSource(string deviceName, string blockName, string outDir, bool withDependencies)
        {
            var plc = RequirePlc(deviceName);
            var obj = RequireBlockOrType(plc, blockName);

            Directory.CreateDirectory(outDir);
            var file = new FileInfo(Path.Combine(outDir, SafeFileName(blockName) + SourceExtension(obj)));
            if (file.Exists) file.Delete();

            plc.ExternalSourceGroup.GenerateSource(
                new[] { (IGenerateSource)obj }, file,
                withDependencies ? GenerateOptions.WithDependencies : GenerateOptions.None);
            return new { exported = true, block = blockName, path = file.FullName };
        }

        // ---- Import / generate -------------------------------------------------

        /// <summary>Name declared by the first object of a SimaticML document (null if unreadable).</summary>
        private static string DeclaredNameInXml(FileInfo file, string elementPrefix)
        {
            try
            {
                var doc = XDocument.Load(file.FullName);
                var root = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith(elementPrefix));
                return root?.Element("AttributeList")?.Element("Name")?.Value;
            }
            catch
            {
                return null;
            }
        }

        public object ImportBlock(string deviceName, string xmlPath, string xml, string group, bool overwrite)
        {
            var plc = RequirePlc(deviceName);
            var scratch = NewTempDir();
            try
            {
                var file = MaterializeInput(xmlPath, xml, "import.xml", scratch, "SimaticML XML");
                var declared = DeclaredNameInXml(file, "SW.Blocks.");
                var existing = declared == null ? null : TryFindBlock(plc, declared);
                if (existing != null && !overwrite)
                    throw new InvalidOperationException(
                        $"Block '{declared}' already exists on '{plc.Name}'. Nothing was imported. " +
                        "To replace it, call again with overwrite:true and confirm:true.");

                RefuseSafetyBlock(existing, "overwrite");
                var backup = existing == null ? null : Backup(plc, existing, existing.Name, "before-import");

                // Override only resolves against the composition that holds the block, so an
                // existing block is re-imported into its own group unless another is requested.
                var target = !string.IsNullOrWhiteSpace(group)
                    ? ResolveBlockGroup(plc, group, true)
                    : existing?.Parent as PlcBlockGroup ?? plc.BlockGroup;

                return Edit($"import block {declared ?? file.Name}", () =>
                {
                    var imported = target.Blocks.Import(file, overwrite ? ImportOptions.Override : ImportOptions.None);
                    return new
                    {
                        imported = imported.Count,
                        blocks = imported.Select(b => b.Name).ToArray(),
                        device = plc.Name,
                        replacedExisting = existing != null,
                        backup,
                        next = "Run tia_compile and read the messages."
                    };
                });
            }
            finally
            {
                TryDeleteDir(scratch);
            }
        }

        private static readonly Regex SourceDeclaration = new Regex(
            @"^\s*(FUNCTION_BLOCK|FUNCTION|ORGANIZATION_BLOCK|DATA_BLOCK|TYPE)\s+(?:""([^""]+)""|([A-Za-z_]\w*))",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>The blocks and data types an SCL/DB/UDT source file declares.</summary>
        private static List<KeyValuePair<string, string>> DeclaredObjectsInSource(string text)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (Match m in SourceDeclaration.Matches(text))
            {
                var kind = m.Groups[1].Value.ToUpperInvariant();
                var name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                list.Add(new KeyValuePair<string, string>(kind, name));
            }
            return list;
        }

        /// <summary>
        /// Generates blocks / data types from SCL-family source text. TIA replaces
        /// same-named objects in place, so when the source redefines something that
        /// already exists this is destructive and needs confirm:true; the objects
        /// about to be replaced are backed up first.
        /// </summary>
        public object ImportScl(string deviceName, string sclPath, string source, string sourceName,
                                string group, bool keepSource, bool compile, bool confirm)
        {
            var plc = RequirePlc(deviceName);
            var scratch = NewTempDir();
            try
            {
                FileInfo file;
                if (!string.IsNullOrWhiteSpace(source))
                {
                    var kinds = DeclaredObjectsInSource(source).Select(d => d.Key).Distinct().ToList();
                    var extension = kinds.Count == 1 && kinds[0] == "TYPE" ? ".udt"
                                  : kinds.Count == 1 && kinds[0] == "DATA_BLOCK" ? ".db"
                                  : ".scl";
                    var stem = string.IsNullOrWhiteSpace(sourceName) ? "inline" : Path.GetFileNameWithoutExtension(sourceName);
                    file = MaterializeInput(null, source, stem + extension, scratch, "SCL source");
                }
                else
                {
                    file = MaterializeInput(sclPath, null, null, scratch, "SCL source");
                }

                var declared = DeclaredObjectsInSource(File.ReadAllText(file.FullName));
                var existing = new List<IEngineeringObject>();
                foreach (var d in declared)
                {
                    var obj = d.Key == "TYPE"
                        ? (IEngineeringObject)TryFindType(plc, d.Value)
                        : TryFindBlock(plc, d.Value);
                    if (obj != null) existing.Add(obj);
                }

                string NameOf(IEngineeringObject o) => o is PlcBlock b ? b.Name : ((PlcType)o).Name;

                foreach (var o in existing) RefuseSafetyBlock(o, "overwrite");

                if (existing.Count > 0)
                    SafetyPolicy.RequireDestructive(confirm,
                        $"REPLACE {existing.Count} existing object(s) on '{plc.Name}' with the new source: " +
                        string.Join(", ", existing.Select(NameOf)) +
                        " (their current content is backed up first)");

                var backups = existing
                    .GroupBy(NameOf, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                    .ToDictionary(NameOf, o => Backup(plc, o, NameOf(o), "before-import"));

                PlcBlockUserGroup targetGroup = null;
                if (!string.IsNullOrWhiteSpace(group))
                    targetGroup = ResolveBlockGroup(plc, group, true) as PlcBlockUserGroup;

                var externalName = "MCP_" + Path.GetFileNameWithoutExtension(file.Name);

                var generated = Edit($"generate blocks from {file.Name}", () =>
                {
                    // Remove a stale external source object with the same name, if any.
                    plc.ExternalSourceGroup.ExternalSources
                        .FirstOrDefault(s => s.Name.Equals(externalName, StringComparison.OrdinalIgnoreCase))?.Delete();

                    var src = plc.ExternalSourceGroup.ExternalSources.CreateFromFile(externalName, file.FullName);
                    try
                    {
                        return targetGroup != null
                            ? src.GenerateBlocksFromSource(targetGroup, GenerateBlockOption.None)
                            : src.GenerateBlocksFromSource(GenerateBlockOption.None);
                    }
                    finally
                    {
                        if (!keepSource)
                        {
                            try { src.Delete(); } catch { /* leave the source if it can't be removed */ }
                        }
                    }
                });

                var names = declared.Select(d => d.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                // TIA generates a block even from source with syntax errors and says nothing
                // until it is compiled, so compile right away and hand the messages back.
                object compileResults = null;
                var errors = 0;
                if (compile)
                {
                    var list = new List<object>();
                    foreach (var name in names)
                    {
                        var obj = (IEngineeringObject)TryFindBlock(plc, name) ?? TryFindType(plc, name);
                        if (obj == null)
                        {
                            list.Add(new { target = name, state = "NotGenerated", note = "TIA did not create this object; check the source." });
                            errors++;
                            continue;
                        }
                        try
                        {
                            var compilable = obj is PlcBlock b ? b.GetService<ICompilable>() : ((PlcType)obj).GetService<ICompilable>();
                            var result = compilable.Compile();
                            errors += result.ErrorCount;
                            list.Add(CompileResult($"{obj.GetType().Name} {name}", result));
                        }
                        catch (Exception ex)
                        {
                            list.Add(new { target = name, state = "CompileFailed", note = Brief(ex.Message) });
                            errors++;
                        }
                    }
                    compileResults = list;
                }

                return new
                {
                    device = plc.Name,
                    generated = (generated ?? new List<IEngineeringObject>()).Count,
                    created = names.Except(backups.Keys, StringComparer.OrdinalIgnoreCase).ToArray(),
                    replaced = backups.Keys.ToArray(),
                    backups = backups.Count == 0 ? null : backups,
                    externalSourceKept = keepSource ? externalName : null,
                    compiled = compile,
                    errorCount = compile ? errors : (int?)null,
                    compile = compileResults,
                    next = compile
                        ? (errors > 0 ? "Fix the errors above and import again." : null)
                        : "Not compiled: run tia_compile and read the messages."
                };
            }
            finally
            {
                TryDeleteDir(scratch);
            }
        }

        // ---- Create / delete ---------------------------------------------------

        public object CreateInstanceDb(string deviceName, string name, string instanceOf, int? number, string group)
        {
            var plc = RequirePlc(deviceName);
            if (TryFindBlock(plc, name) != null)
                throw new InvalidOperationException($"A block named '{name}' already exists on '{plc.Name}'.");
            var fb = RequireBlock(plc, instanceOf);
            if (!(fb is FB))
                throw new InvalidOperationException($"'{instanceOf}' is a {fb.GetType().Name}, not a function block (FB).");

            return Edit($"create instance DB {name}", () =>
            {
                var target = ResolveBlockGroup(plc, group, true);
                var db = target.Blocks.CreateInstanceDB(name, number == null, number ?? 1, fb.Name);
                return new
                {
                    created = true,
                    name = db.Name,
                    number = Try(() => (int?)db.Number),
                    instanceOf = db.InstanceOfName,
                    group = GroupPath(db.Parent)
                };
            });
        }

        public object CreateBlockGroup(string deviceName, string path)
        {
            var plc = RequirePlc(deviceName);
            if (SplitGroupPath(path).Length == 0)
                throw new ArgumentException("path is empty. Give a group path such as \"Machine/Station1\".");

            return Edit($"create block group {path}", () =>
            {
                var group = ResolveBlockGroup(plc, path, true);
                return new { ok = true, group = GroupPath(group) };
            });
        }

        /// <summary>Deletes a block folder, but only an empty one: content is never removed implicitly.</summary>
        public object DeleteBlockGroup(string deviceName, string path)
        {
            var plc = RequirePlc(deviceName);
            var group = ResolveBlockGroup(plc, path, false) as PlcBlockUserGroup;
            if (group == null)
                throw new ArgumentException("path is empty; the root 'Program blocks' folder cannot be deleted.");

            var blocks = AllBlocks(group).Select(b => b.Name).ToArray();
            if (blocks.Length > 0 || group.Groups.Count > 0)
                throw new InvalidOperationException(
                    $"Block folder '{path}' is not empty ({blocks.Length} block(s), {group.Groups.Count} subfolder(s)). " +
                    "Delete or move its content first; this tool never deletes blocks.");

            return Edit($"delete block group {path}", () =>
            {
                group.Delete();
                return new { deleted = true, group = path };
            });
        }

        public object DeleteBlock(string deviceName, string blockName)
        {
            var plc = RequirePlc(deviceName);
            var block = RequireBlock(plc, blockName);
            RefuseSafetyBlock(block, "delete");
            var backup = Backup(plc, block, block.Name, "before-delete");

            return Edit($"delete block {blockName}", () =>
            {
                block.Delete();
                return new { deleted = true, block = blockName, device = plc.Name, backup };
            });
        }

        public object DeleteType(string deviceName, string typeName)
        {
            var plc = RequirePlc(deviceName);
            var type = TryFindType(plc, typeName);
            if (type == null)
                throw new InvalidOperationException($"PLC data type '{typeName}' not found on '{plc.Name}'.");
            var backup = Backup(plc, type, type.Name, "before-delete");

            return Edit($"delete data type {typeName}", () =>
            {
                type.Delete();
                return new { deleted = true, type = typeName, device = plc.Name, backup };
            });
        }

        // ---- LAD ---------------------------------------------------------------

        /// <summary>
        /// Builds a LAD block from a structured rung description (no hand-written XML,
        /// UIds or wires), imports it and compiles it. With dryRun nothing touches TIA.
        /// </summary>
        public object CreateLadBlock(string deviceName, string name, string type, int? number,
                                     LadInterface iface, List<LadNetwork> networks, string culture,
                                     string group, bool overwrite, bool compile, bool dryRun)
        {
            type = (type ?? "FB").ToUpperInvariant();

            if (dryRun)
            {
                // TIA rejects an auto-numbered OB, so show the number a real run would pick first.
                var previewNumber = number ?? (type == "OB" ? 123 : (int?)null);
                var preview = LadXmlBuilder.CreateLadBlockXml(name, type, previewNumber, iface, networks, culture);
                return new { dryRun = true, valid = true, name, xml = preview };
            }

            var plc = RequirePlc(deviceName);
            var existing = TryFindBlock(plc, name);
            if (existing != null && !overwrite)
                throw new InvalidOperationException(
                    $"Block '{name}' already exists on '{plc.Name}'. Nothing was changed. " +
                    "To replace it, call again with overwrite:true and confirm:true.");

            if (number == null && type == "OB")
            {
                var used = new HashSet<int>(AllBlocks(plc.BlockGroup).Select(b => Try(() => b.Number, -1)));
                number = 123;   // user OBs start at 123
                while (used.Contains(number.Value)) number++;
            }

            // Pure function: throws a clear validation error before TIA is involved.
            var xml = LadXmlBuilder.CreateLadBlockXml(name, type, number, iface, networks, culture);
            RefuseSafetyBlock(existing, "overwrite");
            var backup = existing == null ? null : Backup(plc, existing, existing.Name, "before-import");

            var scratch = NewTempDir();
            try
            {
                var file = new FileInfo(Path.Combine(scratch.FullName, SafeFileName(name) + ".xml"));
                File.WriteAllText(file.FullName, xml, new System.Text.UTF8Encoding(false));

                var target = !string.IsNullOrWhiteSpace(group)
                    ? ResolveBlockGroup(plc, group, true)
                    : existing?.Parent as PlcBlockGroup ?? plc.BlockGroup;

                Edit($"create LAD block {name}", () =>
                {
                    target.Blocks.Import(file, ImportOptions.Override);
                    return true;
                });
            }
            finally
            {
                TryDeleteDir(scratch);
            }

            object compileResult = null;
            if (compile)
                compileResult = Compile(plc.Name, name, null);

            return new
            {
                created = true,
                name,
                type,
                number,
                networks = networks.Count,
                replacedExisting = existing != null,
                backup,
                compile = compileResult,
                note = "A clean compile does not prove the logic is right: ask the user to review the rungs in TIA Portal."
            };
        }

        // ---- Compile -----------------------------------------------------------

        public object Compile(string deviceName, string blockName, string scope)
        {
            EnsureProject();
            var results = new List<object>();

            if (!string.IsNullOrWhiteSpace(blockName))
            {
                var plc = RequirePlc(deviceName);
                var obj = RequireBlockOrType(plc, blockName);
                var compilable = obj is PlcBlock b ? b.GetService<ICompilable>() : ((PlcType)obj).GetService<ICompilable>();
                if (compilable == null)
                    throw new InvalidOperationException($"'{blockName}' cannot be compiled on its own.");
                return CompileResult($"{obj.GetType().Name} {blockName}", compilable.Compile());
            }

            scope = (scope ?? "software").ToLowerInvariant();
            if (scope != "software" && scope != "hardware" && scope != "all")
                throw new ArgumentException("scope must be software, hardware or all.");

            // Any software (PLC or HMI) can be the compile target; default is the first PLC.
            var entry = SoftwareEntries<Software>().FirstOrDefault(e =>
                deviceName == null
                    ? e.Software is PlcSoftware
                    : e.Software.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase) ||
                      e.Item.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase) ||
                      e.Device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                throw new InvalidOperationException(
                    $"Nothing to compile for '{deviceName ?? "(first PLC)"}'. Use tia_list_devices for the names.");

            // Hardware (and HMI) compile is driven from the device item that owns the software.
            ICompilable Hardware() =>
                entry.Item.GetService<ICompilable>() ?? entry.Device.GetService<ICompilable>();

            if (entry.Software is PlcSoftware plcSoftware)
            {
                if (scope == "hardware" || scope == "all")
                {
                    var hw = Hardware();
                    if (hw == null) throw new InvalidOperationException($"'{entry.Item.Name}' has no hardware compiler.");
                    results.Add(CompileResult($"{entry.Item.Name} (hardware)", hw.Compile()));
                }
                if (scope == "software" || scope == "all")
                {
                    var sw = plcSoftware.GetService<ICompilable>() ?? Hardware();
                    if (sw == null) throw new InvalidOperationException($"'{plcSoftware.Name}' has no software compiler.");
                    results.Add(CompileResult($"{plcSoftware.Name} (software)", sw.Compile()));
                }
            }
            else
            {
                var c = Hardware();
                if (c == null)
                    throw new InvalidOperationException($"'{entry.Software.Name}' cannot be compiled through Openness.");
                results.Add(CompileResult(entry.Software.Name, c.Compile()));
            }

            return results.Count == 1 ? results[0] : new { results };
        }

        private static object CompileResult(string target, CompilerResult result)
        {
            const int maxMessages = 300;
            var list = new List<object>();
            CollectMessages(result.Messages, list, "", 0, maxMessages);
            return new
            {
                target,
                state = result.State.ToString(),
                errorCount = result.ErrorCount,
                warningCount = result.WarningCount,
                messages = list,
                messagesTruncated = list.Count >= maxMessages ? true : (bool?)null
            };
        }

        /// <summary>
        /// The compiler result is a tree (PLC > Program blocks > block > line) whose inner
        /// nodes carry no text. It is flattened to the messages that say something, each
        /// with where it applies; for SCL the last path element is the line number.
        /// </summary>
        private static void CollectMessages(CompilerResultMessageComposition messages,
                                            List<object> sink, string parentPath, int depth, int max)
        {
            if (messages == null || depth > 8) return;
            foreach (CompilerResultMessage m in messages)
            {
                if (sink.Count >= max) return;
                var path = string.IsNullOrEmpty(m.Path)
                    ? parentPath
                    : (parentPath.Length == 0 ? m.Path : parentPath + " > " + m.Path);

                if (!string.IsNullOrWhiteSpace(m.Description))
                    sink.Add(new { state = m.State.ToString(), at = NullIfEmpty(path), description = m.Description });

                CollectMessages(m.Messages, sink, path, depth + 1, max);
            }
        }

        // ---- Cross-references --------------------------------------------------

        /// <summary>Where a block, data type, tag or tag table is used (and what it uses).</summary>
        public object GetCrossReferences(string deviceName, string name, string filter, int maxLocations)
        {
            var plc = RequirePlc(deviceName);

            CrossReferenceService service;
            string kind;
            var block = TryFindBlock(plc, name);
            var type = block == null ? TryFindType(plc, name) : null;
            if (block != null) { service = block.GetService<CrossReferenceService>(); kind = block.GetType().Name; }
            else if (type != null) { service = type.GetService<CrossReferenceService>(); kind = "PlcType"; }
            else
            {
                var tag = FindPlcTag(plc, name);
                if (tag != null) { service = tag.GetService<CrossReferenceService>(); kind = "PlcTag"; }
                else
                {
                    var table = AllTagTables(plc.TagTableGroup)
                        .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (table == null)
                        throw new InvalidOperationException(
                            $"No block, data type, PLC tag or tag table named '{name}' on '{plc.Name}'.");
                    service = table.GetService<CrossReferenceService>();
                    kind = "PlcTagTable";
                }
            }

            if (service == null)
                throw new InvalidOperationException($"TIA offers no cross-reference information for '{name}'.");

            if (!Enum.TryParse(filter ?? "AllObjects", true, out CrossReferenceFilter crFilter))
                throw new ArgumentException(
                    "filter must be one of: " + string.Join(", ", Enum.GetNames(typeof(CrossReferenceFilter))));

            var result = service.GetCrossReferences(crFilter);
            var remaining = maxLocations <= 0 ? 200 : maxLocations;
            var budget = remaining;

            object Source(SourceObject s, int depth)
            {
                var references = new List<object>();
                foreach (ReferenceObject r in s.References)
                {
                    // TIA reports one location per use and several uses often share a
                    // location (SCL has no network numbers): fold repeats into a count.
                    var seen = new List<Tuple<string, string, string, string>>();
                    foreach (Location l in r.Locations)
                    {
                        if (remaining <= 0) break;
                        remaining--;
                        seen.Add(Tuple.Create(
                            Try(() => l.ReferenceLocation), Try(() => l.Access.ToString()),
                            Try(() => l.ReferenceType.ToString()), NullIfEmpty(Try(() => l.ReferencedAsName))));
                    }
                    var locations = seen.GroupBy(x => x).Select(g => new
                    {
                        location = g.Key.Item1,
                        access = g.Key.Item2,
                        referenceType = g.Key.Item3,
                        referencedAs = g.Key.Item4,
                        uses = g.Count()
                    }).ToArray();

                    references.Add(new
                    {
                        name = Try(() => r.Name),
                        typeName = Try(() => r.TypeName),
                        address = NullIfEmpty(Try(() => r.Address)),
                        path = Try(() => r.Path),
                        locations
                    });
                }

                var children = depth >= 3
                    ? new object[0]
                    : s.Children.Select(c => Source(c, depth + 1))
                       .ToArray();

                return new
                {
                    name = Try(() => s.Name),
                    typeName = Try(() => s.TypeName),
                    address = Try(() => s.Address),
                    references,
                    children = children.Length == 0 ? null : children
                };
            }

            var sources = result.Sources.Select(s => Source(s, 0)).ToArray();
            return new
            {
                name,
                kind,
                filter = crFilter.ToString(),
                sources,
                truncated = remaining <= 0,
                note = remaining <= 0 ? $"Stopped after {budget} locations; raise maxLocations for more." : null
            };
        }
    }
}
