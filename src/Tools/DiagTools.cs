using TiaMcpServer.Mcp;

namespace TiaMcpServer.Tools
{
    /// <summary>
    /// Read-only helpers for exploring the project and the Openness object model.
    /// Useful when extending this server or debugging a project; a normal engineering
    /// session does not need them (start the server without the 'diag' group).
    /// </summary>
    internal static class DiagTools
    {
        public const string Group = "diag";

        private const string Hmi = "HMI name (optional: defaults to the first WinCC Unified HMI)";

        public static void Register(ToolRegistry r, TiaService tia)
        {
            r.Add("tia_dump_hmi_unified", Group, Safety.ReadOnly, "Dump a whole Unified HMI",
                    "Everything in a WinCC Unified HMI in one (large) result: connections, tag tables with tags, and " +
                    "every screen with its items, bindings and scripts. Prefer tia_hmi_get_screen / " +
                    "tia_hmi_list_tags for targeted reads.")
                .Str("device", Hmi)
                .Run(a => tia.DumpHmiUnified(a.Str("device")));

            r.Add("tia_dump_screen_item_bounds", Group, Safety.ReadOnly, "Item names and bounds of a screen",
                    "Name, type, text and Left/Top/Width/Height of every item on a WinCC Unified screen, without " +
                    "bindings or scripts.")
                .Str("device", Hmi)
                .Str("screen", "Screen name", required: true)
                .Run(a => tia.DumpScreenItemBounds(a.Str("device"), a.ReqStr("screen")));

            r.Add("tia_dump_property_dynamization_colors", Group, Safety.ReadOnly, "Colour mapping of a tag binding",
                    "The mapping-table entries (condition and colour as hex) of a tag binding on an item property.")
                .Str("device", Hmi)
                .Str("screen", "Screen name", required: true)
                .Str("item", "Screen item name, or \"@screen\"", required: true)
                .Str("property", "Bound property, e.g. BackColor", required: true)
                .Run(a => tia.DumpPropertyDynamizationColors(a.Str("device"), a.ReqStr("screen"), a.ReqStr("item"), a.ReqStr("property")));

            r.Add("tia_debug_screen_item_creation_infos", Group, Safety.ReadOnly, "Creatable screen item types",
                    "List the screen-item types that can be created on a WinCC Unified screen through Openness " +
                    "(the values accepted as 'type' by tia_hmi_create_items / tia_hmi_create_widget).")
                .Str("device", Hmi)
                .Str("screen", "Any existing screen name", required: true)
                .Run(a => tia.DumpScreenItemCreationInfos(a.Str("device"), a.ReqStr("screen")));

            r.Add("tia_debug_screen_item_type_properties", Group, Safety.ReadOnly, "Properties of a screen item type",
                    "List the properties (name, type, readable, writable) of a creatable screen-item type, to learn " +
                    "how to configure a widget before creating one.")
                .Str("device", Hmi)
                .Str("screen", "Any existing screen name", required: true)
                .Str("typeName", "Type name, e.g. HmiSymbolicIOField", required: true)
                .Run(a => tia.DebugScreenItemTypeProperties(a.Str("device"), a.ReqStr("screen"), a.ReqStr("typeName")));

            r.Add("tia_debug_search_types", Group, Safety.ReadOnly, "Search Openness types",
                    "Search the Openness assembly for type names containing a text, e.g. \"TextList\". Reflection " +
                    "only; touches neither TIA nor the project.")
                .Str("pattern", "Text to look for in type names (case-insensitive)", required: true)
                .Str("assemblyHint", "Search loaded assemblies whose name contains this instead of the Openness one")
                .Run(a => tia.DebugSearchTypes(a.ReqStr("pattern"), a.Str("assemblyHint")));

            r.Add("tia_debug_reflect_type", Group, Safety.ReadOnly, "Reflect an Openness type",
                    "Properties and methods of any loaded type by name (or the values of an enum). Reflection only; " +
                    "touches neither TIA nor the project.")
                .Str("typeName", "Short or full type name, e.g. HmiSoftware", required: true)
                .Run(a => tia.DebugReflectType(a.ReqStr("typeName")));

            r.Add("tia_debug_device_tree", Group, Safety.ReadOnly, "Device item tree",
                    "The full tree of device items (rack, modules, interfaces, ports) of a device, marking which " +
                    "item holds software.")
                .Str("device", "Device name", required: true)
                .Run(a => tia.DebugDeviceTree(a.ReqStr("device")));

            r.Add("tia_debug_device_item_attributes", Group, Safety.ReadOnly, "Attributes of a device item",
                    "Every attribute of one device item with its value, e.g. to inspect firmware or interface settings.")
                .Str("device", "Device name", required: true)
                .Str("item", "DeviceItem name", required: true)
                .Run(a => tia.DumpDeviceItemAttributes(a.ReqStr("device"), a.ReqStr("item")));

            r.Add("tia_inspect_other_project", Group, Safety.ReadOnly, "Inspect another project",
                    "Summarise the Unified HMI (connections, tags, screens) and PLC protection of another project " +
                    "file, for comparison. It has to open that project in the connected TIA instance, which only " +
                    "works if TIA accepts a second project (usually: no other project open). Does not modify it.")
                .Path("path", "Absolute path to the other project file (.ap20)", required: true)
                .Run(a => tia.InspectOtherProject(a.ReqStr("path")));
        }
    }
}
