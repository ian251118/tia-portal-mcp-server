using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TiaMcpServer.Mcp;

namespace TiaMcpServer.Tools
{
    /// <summary>Hardware configuration (read, plus one type change) and live S7 reads.</summary>
    internal static class HardwareTools
    {
        public static void Register(ToolRegistry r, TiaService tia)
        {
            const string hw = "hardware";

            r.Add("tia_list_ip_addresses", hw, Safety.ReadOnly, "List IP addresses",
                    "Configured IP address and subnet of every networked device in the project (PLC, HMI, IO " +
                    "devices), read from the hardware configuration. Use this to find the PLC IP instead of " +
                    "scanning the network.")
                .Run(a => tia.ListIpAddresses());

            r.Add("tia_list_modules", hw, Safety.ReadOnly, "List modules and I/O addresses",
                    "Modules of a device with slot, article number and the input/output address ranges they occupy. " +
                    "Use it to map physical I/O before creating PLC tags.")
                .Str("device", "Device name as in tia_list_devices (default: all devices)")
                .Run(a => tia.ListModules(a.Str("device")));

            r.Add("tia_find_hardware_catalog_entries", hw, Safety.ReadOnly, "Search the hardware catalog",
                    "Search the hardware catalog of the installed TIA Portal, e.g. to see which firmware versions of " +
                    "an article number are available. Reads the catalog, not the project.")
                .Str("filter", "Search text, e.g. an article number like 6AV2128-3GB06-0AX0", required: true)
                .Run(a => tia.FindHardwareCatalogEntries(a.ReqStr("filter")));

            r.Add("tia_change_device_item_type", hw, Safety.Destructive, "Change a module's type/firmware",
                    "Change the catalog type or firmware version of a module (DeviceItem.ChangeType), e.g. to match " +
                    "the firmware of the physical device. This rewrites hardware configuration and can reset " +
                    "module parameters; the previous type identifier is returned so it can be changed back.")
                .Str("device", "Device name", required: true)
                .Str("item", "Name of the module (DeviceItem) to change", required: true)
                .Str("typeIdentifier", "New type identifier, e.g. OrderNumber:6AV2 128-3GB06-0AX1/20.0.0.0", required: true)
                .Run(a => tia.ChangeDeviceItemType(a.ReqStr("device"), a.ReqStr("item"), a.ReqStr("typeIdentifier")));

            r.Add("tia_dump_plc_protection", hw, Safety.ReadOnly, "PLC protection settings",
                    "The PLC's protection access level and the access/connection-related attributes (PUT/GET, " +
                    "secure communication), to diagnose why an HMI or S7 client cannot connect. Passwords are not read.")
                .Str("device", "PLC name (default: first PLC)")
                .Run(a => tia.DumpPlcProtection(a.Str("device")));

            // ---- Live S7 (talks to the physical PLC, read-only) -------------------

            const string s7 = "s7";
            const string live =
                " Talks to the running PLC over the network, independently of TIA Portal; needs 'Permit access " +
                "with PUT/GET communication' enabled on the CPU. Never writes to the PLC.";
            const string ip = "PLC IP address (default: last one used, else the address configured in the project)";

            r.Add("tia_s7_read", s7, Safety.ReadOnly, "Live read: raw bytes",
                    "Read raw bytes from a memory area of the running PLC; returns hex, a byte array and printable " +
                    "ASCII. For typed values prefer tia_s7_read_values / tia_s7_read_tags. Optimized DBs cannot be " +
                    "read by offset: use a non-optimized DB, or I/Q/M." + live)
                .Str("ip", ip)
                .Str("area", "DB | I | Q | M", required: true)
                .Int("db", "DB number (area DB only)")
                .Int("start", "Start byte offset, e.g. 68 for %IB68", required: true)
                .Int("size", "Number of bytes (1-8192)", required: true)
                .Int("rack", "Rack (default 0)")
                .Int("slot", "Slot (default 1 for S7-1200/1500)")
                .OpenWorld()
                .Run(a => S7Live.Read(tia.ResolvePlcIp(a.Str("ip"), null), a.ReqStr("area"), a.Int("db", 0),
                                      a.ReqInt("start"), a.ReqInt("size"), a.IntN("rack"), a.IntN("slot")));

            r.Add("tia_s7_read_values", s7, Safety.ReadOnly, "Live read: typed values by address",
                    "Read typed values from the running PLC by absolute address (I0.0, QB4, MW10, MD20, DB5.DBX0.1, " +
                    "DB5.DBW2, DB5.DBD4). Several in one call; one failing address does not stop the others." + live)
                .Str("ip", ip)
                .Arr("items", "Values to read", Schema.Object(
                    ("address", "string", "S7 absolute address, e.g. I0.0, MW10, DB5.DBD4", true),
                    ("type", "string", "Bool, Byte, SInt, USInt, Char, Word, Int, UInt, DWord, DInt, UDInt, Real, LReal, LInt, ULInt, LWord, Time, String[n] (default: from the address size)", false),
                    ("name", "string", "Label to return with the value", false)), required: true)
                .Int("rack", "Rack (default 0)")
                .Int("slot", "Slot (default 1 for S7-1200/1500)")
                .OpenWorld()
                .Run(a =>
                {
                    var items = new List<S7Live.Item>();
                    foreach (var token in a.ReqArr("items"))
                    {
                        // A bare string is an address.
                        var o = token.Type == JTokenType.String
                            ? new Args(new JObject { ["address"] = token })
                            : new Args(token as JObject);
                        var address = o.ReqStr("address");
                        items.Add(new S7Live.Item { Label = o.Str("name") ?? address, Address = address, Type = o.Str("type") });
                    }
                    return S7Live.ReadValues(tia.ResolvePlcIp(a.Str("ip"), null), items, a.IntN("rack"), a.IntN("slot"));
                });

            r.Add("tia_s7_read_tags", s7, Safety.ReadOnly, "Live read: PLC tags by name",
                    "Read the live value of PLC tags by NAME: address and data type are taken from the project's tag " +
                    "tables (so TIA must be connected), the value from the running PLC. Only tags with an absolute " +
                    "address (I/Q/M); DB members are read by address with tia_s7_read_values." + live)
                .Str("device", "PLC name (default: first PLC)")
                .Arr("tags", "PLC tag names", Schema.String(), required: true)
                .Str("ip", ip)
                .Int("rack", "Rack (default 0)")
                .Int("slot", "Slot (default 1 for S7-1200/1500)")
                .OpenWorld()
                .Run(a => tia.S7ReadTags(a.Str("device"), a.ReqArr("tags"), a.Str("ip"), a.IntN("rack"), a.IntN("slot")));

            r.Add("tia_s7_cpu_info", s7, Safety.ReadOnly, "Live read: CPU status",
                    "RUN/STOP state and article number of the running CPU (plus module and station name where the " +
                    "CPU answers that query; S7-1200/1500 often do not)." + live)
                .Str("ip", ip)
                .Int("rack", "Rack (default 0)")
                .Int("slot", "Slot (default 1 for S7-1200/1500)")
                .OpenWorld()
                .Run(a => S7Live.CpuInfo(tia.ResolvePlcIp(a.Str("ip"), null), a.IntN("rack"), a.IntN("slot")));
        }
    }
}
