using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TiaMcpServer.Lad;
using TiaMcpServer.Mcp;

namespace TiaMcpServer.Tools
{
    /// <summary>PLC program: blocks, data types, tags, watch tables, cross-references.</summary>
    internal static class PlcTools
    {
        public const string Group = "plc";

        private const string Device = "PLC name (optional: defaults to the first PLC)";

        public static void Register(ToolRegistry r, TiaService tia)
        {
            // ---- Blocks: read ---------------------------------------------------

            r.Add("tia_list_blocks", Group, Safety.ReadOnly, "List blocks",
                    "List the program blocks (OB/FB/FC/DB) of a PLC with number, language, folder, consistency and " +
                    "know-how protection.")
                .Str("device", Device)
                .Run(a => tia.ListBlocks(a.Str("device")));

            r.Add("tia_read_block", Group, Safety.ReadOnly, "Read a block",
                    "Return the content of a block or PLC data type inline: source text for SCL/STL blocks, DBs and " +
                    "UDTs; SimaticML XML for graphical blocks (LAD/FBD/GRAPH). The usual way to read existing code " +
                    "before changing it. Fails while TIA is online.")
                .Str("device", Device)
                .Str("block", "Block or PLC data type name", required: true)
                .Str("format", "auto (default) | source | xml")
                .Int("maxChars", "Cut the content at this many characters (default 60000, 0 = no limit)")
                .Run(a => tia.ReadBlock(a.Str("device"), a.ReqStr("block"), a.Str("format"), a.Int("maxChars", 60000)));

            r.Add("tia_get_block_interface", Group, Safety.ReadOnly, "Block interface",
                    "The interface of a block (Input/Output/InOut/Static/Temp/Constant members with data types, start " +
                    "values and comments), or the members of a DB or PLC data type. Use it to call a block or address " +
                    "a DB correctly. The object must be consistent (compiled).")
                .Str("device", Device)
                .Str("block", "Block or PLC data type name", required: true)
                .Run(a => tia.GetBlockInterface(a.Str("device"), a.ReqStr("block")));

            r.Add("tia_export_block", Group, Safety.ReadOnly, "Export block (XML file)",
                    "Export a block or PLC data type to a SimaticML XML file on disk. Does not modify the project; " +
                    "overwrites <outDir>/<name>.xml. Only consistent (compiled) objects can be exported.")
                .Str("device", Device)
                .Str("block", "Block or PLC data type name", required: true)
                .Path("outDir", "Output directory for the XML file", required: true)
                .Run(a => tia.ExportBlock(a.Str("device"), a.ReqStr("block"), a.ReqStr("outDir")));

            r.Add("tia_export_source", Group, Safety.ReadOnly, "Export source (text file)",
                    "Generate the source text of a block or PLC data type to a file (.scl, .db, .udt or .awl). Does " +
                    "not modify the project; overwrites the file. Not available for graphical blocks or while online.")
                .Str("device", Device)
                .Str("block", "Block or PLC data type name", required: true)
                .Path("outDir", "Output directory for the source file", required: true)
                .Bool("withDependencies", "Also include the blocks and types this one depends on (default false)")
                .Run(a => tia.ExportSource(a.Str("device"), a.ReqStr("block"), a.ReqStr("outDir"), a.Bool("withDependencies")));

            r.Add("tia_get_cross_references", Group, Safety.ReadOnly, "Cross-references",
                    "Where a block, PLC data type, PLC tag or tag table is used, and what it uses. Check this before " +
                    "deleting or renaming anything.")
                .Str("device", Device)
                .Str("name", "Name of the block, data type, tag or tag table", required: true)
                .Str("filter", "AllObjects (default) | ObjectsWithReferences | ObjectsWithoutReferences | UnusedObjects")
                .Int("maxLocations", "Stop after this many use locations (default 200)")
                .Run(a => tia.GetCrossReferences(a.Str("device"), a.ReqStr("name"), a.Str("filter"), a.Int("maxLocations", 200)));

            // ---- Blocks: write --------------------------------------------------

            r.Add("tia_import_scl", Group, Safety.Write, "Write blocks from SCL source",
                    "Generate blocks (FB/FC/OB/DB) or PLC data types from SCL-family source, given inline or as a " +
                    "file. THE way to write PLC logic. New objects are simply created. If the source redefines an " +
                    "object that already exists, TIA replaces it in place: that needs confirm:true, and the old " +
                    "version is backed up first (path returned). The generated objects are compiled and the " +
                    "compiler messages returned: TIA accepts source with errors silently, so read errorCount. " +
                    "Fails while TIA is online.")
                .Str("device", Device)
                .Str("source", "Source text inline (use this or sclPath)")
                .Path("sclPath", "Absolute path to a .scl / .db / .udt source file (use this or source)")
                .Str("name", "Name for the inline source (only labels it in TIA)")
                .Str("group", "Block folder to generate into, e.g. \"Machine/Station1\" (created if missing; default: root)")
                .Bool("keepSource", "Keep the external source object in the project (default false)")
                .Bool("compile", "Compile the generated objects and return the messages (default true)")
                .ConfirmsInHandler("the source redefines blocks or data types that already exist")
                .Run(a => tia.ImportScl(a.Str("device"), a.Str("sclPath"), a.Str("source"), a.Str("name"),
                                        a.Str("group"), a.Bool("keepSource"), a.Bool("compile", true), a.Bool("confirm")));

            r.Add("tia_import_block", Group, Safety.Write, "Import block (SimaticML XML)",
                    "Import a block from SimaticML XML, inline or from a file. Use for LAD/FBD/GRAPH blocks or to " +
                    "restore an export. Without overwrite an existing block makes it fail; with overwrite:true the " +
                    "existing block is replaced (backed up first).")
                .Str("device", Device)
                .Str("xml", "SimaticML document inline (use this or xmlPath)")
                .Path("xmlPath", "Absolute path to the SimaticML XML file (use this or xml)")
                .Str("group", "Block folder to import into (default: the existing block's folder, else root)")
                .Bool("overwrite", "Replace the block if it already exists (default false)")
                .DestructiveIf(a => a.Bool("overwrite"), "overwrite:true")
                .Run(a => tia.ImportBlock(a.Str("device"), a.Str("xmlPath"), a.Str("xml"), a.Str("group"), a.Bool("overwrite")));

            r.Add("tia_create_lad_block", Group, Safety.Write, "Create a LAD block",
                    "Create a ladder (LAD) FB, FC or OB from a structured description of its rungs; the XML, UIds and " +
                    "wires are generated for you, then the block is imported and compiled. Each network is series " +
                    "logic in 'elements' ending in 'outputs'. A seal-in is one network: elements=[branch[[contact " +
                    "Start],[contact Motor]], contact Stop negated], outputs=[coil Motor]. Operands: a PLC tag (Tag), " +
                    "a DB member (\"DB\".Member), a local (#name) or a constant (T#5s, 10). Use dryRun:true to " +
                    "validate and see the XML without touching TIA. A clean compile does not prove the logic: ask " +
                    "the user to review it.")
                .Str("device", Device)
                .Str("name", "Block name", required: true)
                .Str("type", "FB (default) | FC | OB")
                .Int("number", "Block number (default: assigned automatically)")
                .Raw("interface", LadInterfaceSchema())
                .Raw("networks", LadNetworksSchema(), required: true)
                .Str("culture", "Project language for network titles/comments, e.g. en-US (see tia_project_info). Omit to leave them out.")
                .Str("group", "Block folder to create the block in (default: root)")
                .Bool("overwrite", "Replace an existing block of the same name (default false)")
                .Bool("compile", "Compile after importing (default true)")
                .Bool("dryRun", "Only validate and return the generated XML; TIA is not touched")
                .DestructiveIf(a => a.Bool("overwrite") && !a.Bool("dryRun"), "overwrite:true")
                .Run(a => tia.CreateLadBlock(a.Str("device"), a.ReqStr("name"), a.Str("type"), a.IntN("number"),
                                             a.To<LadInterface>("interface"), a.To<List<LadNetwork>>("networks"),
                                             a.Str("culture"), a.Str("group"), a.Bool("overwrite"),
                                             a.Bool("compile", true), a.Bool("dryRun")));

            r.Add("tia_create_instance_db", Group, Safety.Write, "Create an instance DB",
                    "Create an instance data block for an existing function block (FB).")
                .Str("device", Device)
                .Str("name", "Name of the new instance DB", required: true)
                .Str("instanceOf", "Name of the FB it is an instance of", required: true)
                .Int("number", "DB number (default: assigned automatically)")
                .Str("group", "Block folder to create it in (default: root)")
                .Run(a => tia.CreateInstanceDb(a.Str("device"), a.ReqStr("name"), a.ReqStr("instanceOf"), a.IntN("number"), a.Str("group")));

            r.Add("tia_create_block_group", Group, Safety.Write, "Create a block folder",
                    "Create a folder (group) under Program blocks. Nested paths are created as needed; an existing " +
                    "folder is left as it is.")
                .Str("device", Device)
                .Str("path", "Folder path, e.g. \"Machine/Station1\"", required: true)
                .Idempotent()
                .Run(a => tia.CreateBlockGroup(a.Str("device"), a.ReqStr("path")));

            r.Add("tia_delete_block_group", Group, Safety.Destructive, "Delete an empty block folder",
                    "Delete a folder under Program blocks. Only an EMPTY folder is deleted: if it still holds " +
                    "blocks or subfolders the call fails and nothing is removed.")
                .Str("device", Device)
                .Str("path", "Folder path, e.g. \"Machine/Station1\"", required: true)
                .Run(a => tia.DeleteBlockGroup(a.Str("device"), a.ReqStr("path")));

            r.Add("tia_delete_block", Group, Safety.Destructive, "Delete a block",
                    "Delete a program block. Its source (or XML) is backed up first and the path returned, but calls " +
                    "to it elsewhere in the program break. Check tia_get_cross_references first.")
                .Str("device", Device)
                .Str("block", "Name of the block to delete", required: true)
                .Run(a => tia.DeleteBlock(a.Str("device"), a.ReqStr("block")));

            // ---- PLC data types -------------------------------------------------

            r.Add("tia_list_types", Group, Safety.ReadOnly, "List PLC data types",
                    "List the PLC data types (UDTs) of a PLC. Read one with tia_read_block or tia_get_block_interface; " +
                    "create or change one with tia_import_scl (TYPE ... END_TYPE source).")
                .Str("device", Device)
                .Run(a => tia.ListTypes(a.Str("device")));

            r.Add("tia_delete_type", Group, Safety.Destructive, "Delete a PLC data type",
                    "Delete a PLC data type (UDT). Backed up first, but every block and DB using it becomes " +
                    "inconsistent. Check tia_get_cross_references first.")
                .Str("device", Device)
                .Str("type", "Name of the PLC data type to delete", required: true)
                .Run(a => tia.DeleteType(a.Str("device"), a.ReqStr("type")));

            // ---- Tags -----------------------------------------------------------

            r.Add("tia_list_plc_tags", Group, Safety.ReadOnly, "List PLC tags",
                    "PLC tag tables with their tags (name, data type, address, comment) and user constants.")
                .Str("device", Device)
                .Str("table", "Only this tag table (default: all)")
                .Str("filter", "Only names containing this text")
                .Run(a => tia.ListPlcTags(a.Str("device"), a.Str("table"), a.Str("filter")));

            r.Add("tia_create_tag_table", Group, Safety.Write, "Create a PLC tag table",
                    "Create an empty PLC tag table. An existing table of that name is left untouched.")
                .Str("device", Device)
                .Str("name", "Tag table name", required: true)
                .Idempotent()
                .Run(a => tia.CreateTagTable(a.Str("device"), a.ReqStr("name")));

            r.Add("tia_create_plc_tags", Group, Safety.Write, "Create PLC tags",
                    "Create one or more PLC tags. Names that already exist are skipped and reported, never " +
                    "overwritten (change them with tia_update_plc_tag). The tag table is created if missing.")
                .Str("device", Device)
                .Str("table", "Tag table to add the tags to (default: the default tag table)")
                .Arr("tags", "Tags to create", Schema.Object(
                    ("name", "string", "Tag name", true),
                    ("dataType", "string", "PLC data type, e.g. Bool, Int, Real, Word (default Bool)", false),
                    ("address", "string", "Absolute address, e.g. %I0.0, %Q4.1, %MW10, %MD20", false),
                    ("comment", "string", "Tag comment", false),
                    ("externalAccessible", "boolean", "Accessible from HMI/OPC UA", false),
                    ("externalWritable", "boolean", "Writable from HMI/OPC UA", false)), required: true)
                .Run(a => tia.CreatePlcTags(a.Str("device"), a.Str("table"), a.ReqArr("tags")));

            r.Add("tia_create_plc_constants", Group, Safety.Write, "Create PLC user constants",
                    "Create user constants in a PLC tag table. Existing names are skipped, never overwritten.")
                .Str("device", Device)
                .Str("table", "Tag table to add them to (default: the default tag table)")
                .Arr("constants", "Constants to create", Schema.Object(
                    ("name", "string", "Constant name", true),
                    ("dataType", "string", "Data type, e.g. Int, Real, Time (default Int)", false),
                    ("value", "string", "Value, e.g. 10, 3.14, T#5s", true),
                    ("comment", "string", "Comment", false)), required: true)
                .Run(a => tia.CreatePlcConstants(a.Str("device"), a.Str("table"), a.ReqArr("constants")));

            r.Add("tia_update_plc_tag", Group, Safety.Write, "Change a PLC tag",
                    "Rename a PLC tag or change its data type, address or comment. Only the given fields change; the " +
                    "previous values are returned. A rename is followed by TIA in the code that uses the tag " +
                    "symbolically; changing type or address can make that code inconsistent until recompiled.")
                .Str("device", Device)
                .Str("tag", "Current tag name", required: true)
                .Str("newName", "New name")
                .Str("dataType", "New data type")
                .Str("address", "New absolute address, e.g. %M10.0")
                .Str("comment", "New comment")
                .Run(a => tia.UpdatePlcTag(a.Str("device"), a.ReqStr("tag"), a.Str("newName"), a.Str("dataType"),
                                           a.Str("address"), a.Str("comment")));

            r.Add("tia_delete_plc_tag", Group, Safety.Destructive, "Delete a PLC tag",
                    "Delete a PLC tag. Code and HMI tags that use it are left pointing at an undefined symbol. Check " +
                    "tia_get_cross_references first.")
                .Str("device", Device)
                .Str("tag", "Name of the tag to delete", required: true)
                .Run(a => tia.DeletePlcTag(a.Str("device"), a.ReqStr("tag")));

            r.Add("tia_delete_tag_table", Group, Safety.Destructive, "Delete a PLC tag table",
                    "Delete a PLC tag table with all its tags and constants (exported to the backup folder first). " +
                    "Code using those tags is left with undefined symbols. The default tag table cannot be deleted.")
                .Str("device", Device)
                .Str("table", "Name of the tag table to delete", required: true)
                .Run(a => tia.DeleteTagTable(a.Str("device"), a.ReqStr("table")));

            r.Add("tia_export_tag_table", Group, Safety.ReadOnly, "Export a PLC tag table (XML file)",
                    "Export a PLC tag table to a SimaticML XML file. Does not modify the project; overwrites " +
                    "<outDir>/<table>.xml.")
                .Str("device", Device)
                .Str("table", "Tag table name (default: the default tag table)")
                .Path("outDir", "Output directory", required: true)
                .Run(a => tia.ExportTagTable(a.Str("device"), a.Str("table"), a.ReqStr("outDir")));

            r.Add("tia_import_tag_table", Group, Safety.Write, "Import a PLC tag table (XML)",
                    "Import a PLC tag table from SimaticML XML, inline or from a file. With overwrite:true an " +
                    "existing table of the same name is replaced as a whole (tags not in the XML are removed; the " +
                    "old table is backed up first).")
                .Str("device", Device)
                .Str("xml", "SimaticML document inline (use this or xmlPath)")
                .Path("xmlPath", "Absolute path to the XML file (use this or xml)")
                .Bool("overwrite", "Replace the table if it already exists (default false)")
                .DestructiveIf(a => a.Bool("overwrite"), "overwrite:true")
                .Run(a => tia.ImportTagTable(a.Str("device"), a.Str("xmlPath"), a.Str("xml"), a.Bool("overwrite")));

            // ---- Watch tables ---------------------------------------------------

            r.Add("tia_list_watchtables", Group, Safety.ReadOnly, "List watch tables",
                    "Watch tables of a PLC with their entries (address, display format, modify value).")
                .Str("device", Device)
                .Run(a => tia.ListWatchTables(a.Str("device")));

            r.Add("tia_create_watchtable", Group, Safety.Write, "Create a watch table",
                    "Create an empty watch table. Openness cannot add entries one by one: to fill a table, write its " +
                    "XML (export an existing one as a template) and use tia_import_watchtable.")
                .Str("device", Device)
                .Str("name", "Watch table name", required: true)
                .Idempotent()
                .Run(a => tia.CreateWatchTable(a.Str("device"), a.ReqStr("name")));

            r.Add("tia_export_watchtable", Group, Safety.ReadOnly, "Export a watch table (XML file)",
                    "Export a watch table to an XML file. Does not modify the project; overwrites <outDir>/<name>.xml.")
                .Str("device", Device)
                .Str("name", "Watch table name", required: true)
                .Path("outDir", "Output directory", required: true)
                .Run(a => tia.ExportWatchTable(a.Str("device"), a.ReqStr("name"), a.ReqStr("outDir")));

            r.Add("tia_import_watchtable", Group, Safety.Write, "Import a watch table (XML)",
                    "Create a watch table from XML, inline or from a file. With overwrite:true an existing table of " +
                    "the same name is replaced.")
                .Str("device", Device)
                .Str("xml", "Watch table XML inline (use this or xmlPath)")
                .Path("xmlPath", "Absolute path to the XML file (use this or xml)")
                .Bool("overwrite", "Replace the table if it already exists (default false)")
                .DestructiveIf(a => a.Bool("overwrite"), "overwrite:true")
                .Run(a => tia.ImportWatchTable(a.Str("device"), a.Str("xmlPath"), a.Str("xml"), a.Bool("overwrite")));

            r.Add("tia_delete_watchtable", Group, Safety.Destructive, "Delete a watch table",
                    "Delete a watch table. Watch tables do not affect the PLC program.")
                .Str("device", Device)
                .Str("name", "Name of the watch table to delete", required: true)
                .Run(a => tia.DeleteWatchTable(a.Str("device"), a.ReqStr("name")));

            // ---- Online state ---------------------------------------------------

            r.Add("tia_online_state", Group, Safety.ReadOnly, "Online state",
                    "Whether TIA Portal is online with each PLC. Does not go online or offline. Source generation " +
                    "and block imports are refused by TIA while online.")
                .Str("device", "PLC name (default: all PLCs)")
                .Run(a => tia.OnlineState(a.Str("device")));
        }

        // ---- LAD schemas ---------------------------------------------------------

        private static JObject LadMemberArray() => new JObject
        {
            ["type"] = "array",
            ["items"] = Schema.Object(
                ("name", "string", "Member name", true),
                ("datatype", "string", "e.g. Bool, Int, Real, Time, TON_TIME", true))
        };

        private static JObject LadInterfaceSchema() => new JObject
        {
            ["type"] = "object",
            ["description"] = "Interface members by section. FB: input/output/inOut/static/temp/constant; FC: no static; OB: input/temp/constant.",
            ["properties"] = new JObject
            {
                ["input"] = LadMemberArray(),
                ["output"] = LadMemberArray(),
                ["inOut"] = LadMemberArray(),
                ["static"] = LadMemberArray(),
                ["temp"] = LadMemberArray(),
                ["constant"] = LadMemberArray()
            }
        };

        private static JObject LadNetworksSchema() => new JObject
        {
            ["type"] = "array",
            ["description"] = "Networks (rungs), in order.",
            ["items"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["title"] = Schema.String("Network title (needs 'culture')"),
                    ["comment"] = Schema.String("Network comment (needs 'culture')"),
                    ["elements"] = LadElementArray(1, "Series logic, left to right"),
                    ["outputs"] = new JObject
                    {
                        ["type"] = "array",
                        ["description"] = "Coils driven by the rung's power flow",
                        ["items"] = Schema.Object(
                            ("type", "string", "coil | scoil (set) | rcoil (reset)", true),
                            ("operand", "string", "The tag the coil writes", true))
                    }
                }
            }
        };

        // 'branch' nests elements; JSON Schema fragments are finite, so nesting is unrolled to a fixed depth.
        private static JObject LadElementArray(int depth, string description)
        {
            var props = new JObject
            {
                ["type"] = Schema.String("elements: contact | eq ne gt ge lt le | pbox nbox | ton tof tp | ctu ctd | sr rs | branch | call | move | add sub mul div mod | norm_x scale_x | part. outputs: coil | scoil | rcoil"),
                ["operand"] = Schema.String("contact/coil: the tag. compare: left value. pbox/nbox: edge-memory Bool. move/arithmetic/norm_x/scale_x: destination. sr/rs: the bit"),
                ["negated"] = new JObject { ["type"] = "boolean", ["description"] = "contact: true = normally closed" },
                ["operand2"] = Schema.String("compare: right-hand value"),
                ["dataType"] = Schema.String("compare: operand type (default Int). norm_x/scale_x: input type. ctu/ctd: counter type"),
                ["instance"] = Schema.String("timer/counter/FB call: \"#Name\" for a multi-instance (a static of the FB) or an instance DB name"),
                ["pt"] = Schema.String("timer: preset, e.g. T#5s"),
                ["pv"] = Schema.String("ctu/ctd: preset value"),
                ["source"] = Schema.String("move: value or tag to copy into 'operand'. A move ends the rung (no outputs after it)"),
                ["operands"] = new JObject { ["type"] = "array", ["items"] = Schema.String(), ["description"] = "add/mul: 2+ input values; sub/div/mod: exactly 2. These end the rung like move" },
                ["destType"] = Schema.String("norm_x/scale_x: result type (default Real)"),
                ["block"] = Schema.String("call: name of the FB/FC"),
                ["blockType"] = Schema.String("call: FB | FC"),
                ["parameters"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "call: parameters to wire",
                    ["items"] = Schema.Object(
                        ("name", "string", "Parameter name", true),
                        ("section", "string", "Input | Output | InOut", true),
                        ("datatype", "string", "Parameter data type", true),
                        ("operand", "string", "Tag, \"DB\".Member, #local or constant", true))
                },
                ["name"] = Schema.String("part: instruction name exactly as TIA exports it, e.g. WR_SYS_T"),
                ["version"] = Schema.String("part: instruction version, e.g. 1.0"),
                ["templates"] = new JObject { ["type"] = "object", ["description"] = "part: template values, e.g. {\"date_type\":\"DTL\"}" },
                ["inPins"] = new JObject { ["type"] = "object", ["description"] = "part: input pin -> operand. norm_x/scale_x: needs min, value, max" },
                ["outPins"] = new JObject { ["type"] = "object", ["description"] = "part: output pin -> destination operand" },
                ["eno"] = new JObject { ["type"] = "boolean", ["description"] = "part: true if power flow continues from the box" }
            };

            if (depth > 0)
            {
                // Nested element lists have the same shape as this one; spelling the schema
                // out again per level would multiply the size of the tool definition.
                var nested = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } };
                props["branches"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "branch: 2 or more parallel paths; each path is an array of elements (same fields as here)",
                    ["items"] = nested
                };
                props["other"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "sr: reset path; rs: set path; ctu: reset path; ctd: load path. An array of elements (same fields as here) starting at the power rail",
                    ["items"] = new JObject { ["type"] = "object" }
                };
            }

            return new JObject
            {
                ["type"] = "array",
                ["description"] = description,
                ["items"] = new JObject { ["type"] = "object", ["properties"] = props, ["required"] = new JArray("type") }
            };
        }
    }
}
