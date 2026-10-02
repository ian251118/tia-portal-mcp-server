using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sharp7;

namespace TiaMcpServer
{
    /// <summary>
    /// Reads LIVE values from the PLC over the S7 "classic" protocol (Sharp7).
    /// TIA Openness cannot do this (it has no runtime / monitoring API), so this
    /// is a separate network client, independent of the Openness session.
    ///
    /// READ-ONLY BY DESIGN: there is deliberately no write path here. Writing to a
    /// running machine is a different risk class and is left out of this server.
    ///
    /// Requirements on the CPU:
    ///   - "Permit access with PUT/GET communication" enabled (Protection &amp; Security).
    ///   - Network reachability to the PLC IP.
    /// For reading optimized DB contents, use a NON-optimized DB; the process image
    /// (I/Q, area PE/PA) and M area are always addressable.
    ///
    /// Connects per call (connect -> read -> disconnect) for robustness; the last
    /// ip/rack/slot are cached so they are optional on subsequent calls.
    /// </summary>
    internal static class S7Live
    {
        private static string _lastIp;
        private static int _lastRack = 0;
        private static int _lastSlot = 1;   // S7-1200 / S7-1500 CPU is rack 0, slot 1

        public static string LastIp => _lastIp;

        // ---- Connection --------------------------------------------------------

        private static T WithClient<T>(string ip, int? rack, int? slot, Func<S7Client, string, T> body)
        {
            if (string.IsNullOrWhiteSpace(ip)) ip = _lastIp;
            if (string.IsNullOrWhiteSpace(ip))
                throw new ArgumentException(
                    "No PLC IP given and none cached yet. Pass 'ip' (tia_list_ip_addresses shows the configured one).");
            int r = rack ?? _lastRack;
            int s = slot ?? _lastSlot;

            var client = new S7Client();
            int res = client.ConnectTo(ip, r, s);
            if (res != 0)
                throw new InvalidOperationException(
                    $"S7 connect to {ip} (rack {r}, slot {s}) failed: {client.ErrorText(res)}. " +
                    "Check the IP / network and that 'Permit access with PUT/GET' is enabled on the CPU.");

            try
            {
                _lastIp = ip; _lastRack = r; _lastSlot = s;
                return body(client, ip);
            }
            finally
            {
                try { client.Disconnect(); } catch { /* already gone */ }
            }
        }

        // ---- Raw bytes ---------------------------------------------------------

        public static object Read(string ip, string area, int db, int start, int size, int? rack, int? slot)
        {
            if (size <= 0 || size > 8192)
                throw new ArgumentException("size must be between 1 and 8192 bytes.");
            int areaCode = AreaCode(area);

            return WithClient(ip, rack, slot, (client, usedIp) =>
            {
                var buffer = new byte[size];
                int res = client.ReadArea(areaCode, db, start, size, S7Consts.S7WLByte, buffer);
                if (res != 0)
                    throw new InvalidOperationException(
                        $"S7 read {area} db={db} start={start} size={size} failed: {client.ErrorText(res)}");

                return new
                {
                    ip = usedIp,
                    area = (area ?? "DB").ToUpperInvariant(),
                    db = areaCode == S7Consts.S7AreaDB ? db : (int?)null,
                    start,
                    size,
                    hex = BitConverter.ToString(buffer).Replace("-", ""),
                    bytes = buffer.Select(b => (int)b).ToArray(),
                    ascii = ToPrintableAscii(buffer)
                };
            });
        }

        private static int AreaCode(string area)
        {
            switch ((area ?? "DB").Trim().ToUpperInvariant())
            {
                case "DB": return S7Consts.S7AreaDB;
                case "I": case "E": case "PE": case "INPUT": case "INPUTS": return S7Consts.S7AreaPE;
                case "Q": case "A": case "PA": case "OUTPUT": case "OUTPUTS": return S7Consts.S7AreaPA;
                case "M": case "MK": case "MERKER": case "FLAG": case "FLAGS": return S7Consts.S7AreaMK;
                default: throw new ArgumentException($"Unknown area '{area}'. Use DB | I | Q | M.");
            }
        }

        private static string ToPrintableAscii(byte[] buf)
        {
            var chars = buf.Select(b => (b >= 32 && b < 127) ? (char)b : '.').ToArray();
            return new string(chars);
        }

        // ---- Typed values ------------------------------------------------------

        /// <summary>One value to read: where it is and how to decode it.</summary>
        public sealed class Item
        {
            public string Label;      // what the caller called it (address or tag name)
            public string Address;    // S7 absolute address, e.g. %I0.0, MW10, DB5.DBD4
            public string Type;       // PLC data type name, e.g. Int or String[20]; null = derived from the address
            public string Error;      // set when the item could not even be resolved
        }

        private sealed class Location
        {
            public int Area, Db, Byte, Bit;
            public char Size;   // X (bit), B, W, D
        }

        private static readonly Regex DbAddress = new Regex(
            @"^DB(\d+)\.DB([XBWD])(\d+)(?:\.([0-7]))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AreaAddress = new Regex(
            @"^([IEQAM])([XBWD]?)(\d+)(?:\.([0-7]))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static Location ParseAddress(string address)
        {
            var a = (address ?? "").Trim().TrimStart('%').Replace(" ", "");
            Match m;

            if ((m = DbAddress.Match(a)).Success)
            {
                var size = char.ToUpperInvariant(m.Groups[2].Value[0]);
                if (size == 'X' && !m.Groups[4].Success)
                    throw new ArgumentException($"'{address}': a DBX address needs a bit, e.g. DB1.DBX0.3.");
                return new Location
                {
                    Area = S7Consts.S7AreaDB,
                    Db = int.Parse(m.Groups[1].Value),
                    Size = size,
                    Byte = int.Parse(m.Groups[3].Value),
                    Bit = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0
                };
            }

            if ((m = AreaAddress.Match(a)).Success)
            {
                var hasBit = m.Groups[4].Success;
                var sizeText = m.Groups[2].Value.ToUpperInvariant();
                var size = sizeText.Length > 0 ? sizeText[0] : 'X';
                if (size == 'X' && !hasBit)
                    throw new ArgumentException($"'{address}': a bit address needs the bit, e.g. I0.3; for a byte use IB0.");
                return new Location
                {
                    Area = AreaCode(m.Groups[1].Value),
                    Size = size,
                    Byte = int.Parse(m.Groups[3].Value),
                    Bit = hasBit ? int.Parse(m.Groups[4].Value) : 0
                };
            }

            throw new ArgumentException(
                $"'{address}' is not an S7 absolute address. Examples: I0.0, QB4, MW10, MD20, DB5.DBX0.1, DB5.DBW2, DB5.DBD4.");
        }

        /// <summary>Bytes a value of this type occupies, or 0 for a single bit.</summary>
        private static int ByteCount(string type, int? length)
        {
            switch (type)
            {
                case "BOOL": return 0;
                case "BYTE": case "USINT": case "SINT": case "CHAR": return 1;
                case "WORD": case "UINT": case "INT": case "DATE": case "S5TIME": return 2;
                case "DWORD": case "UDINT": case "DINT": case "REAL": case "TIME": case "TIME_OF_DAY": case "TOD": return 4;
                case "LWORD": case "ULINT": case "LINT": case "LREAL": return 8;
                case "STRING": return (length ?? 254) + 2;
                default:
                    throw new ArgumentException(
                        $"Data type '{type}' cannot be decoded. Supported: Bool, Byte, SInt, USInt, Char, Word, Int, UInt, " +
                        "DWord, DInt, UDInt, Real, LReal, LInt, ULInt, LWord, Time, String.");
            }
        }

        private static string DefaultType(char size)
        {
            switch (size)
            {
                case 'X': return "BOOL";
                case 'B': return "BYTE";
                case 'W': return "WORD";
                default: return "DWORD";
            }
        }

        private static ulong BigEndian(byte[] b, int count)
        {
            ulong v = 0;
            for (int i = 0; i < count; i++) v = (v << 8) | b[i];
            return v;
        }

        private static object Decode(string type, byte[] b, int bit)
        {
            switch (type)
            {
                case "BOOL": return (b[0] & (1 << bit)) != 0;
                case "BYTE": case "USINT": return (int)b[0];
                case "SINT": return (int)(sbyte)b[0];
                case "CHAR": return ((char)b[0]).ToString();
                case "WORD": case "UINT": case "DATE": case "S5TIME": return (int)BigEndian(b, 2);
                case "INT": return (int)(short)BigEndian(b, 2);
                case "DWORD": case "UDINT": case "TIME_OF_DAY": case "TOD": return (long)BigEndian(b, 4);
                case "DINT": return (int)BigEndian(b, 4);
                case "TIME": return (int)BigEndian(b, 4);   // milliseconds
                case "REAL": return (double)BitConverter.ToSingle(BitConverter.GetBytes((uint)BigEndian(b, 4)), 0);
                case "LREAL": return BitConverter.Int64BitsToDouble((long)BigEndian(b, 8));
                case "LINT": return (long)BigEndian(b, 8);
                case "LWORD": case "ULINT": return BigEndian(b, 8).ToString(CultureInfo.InvariantCulture);
                case "STRING":
                    // S7 String: [max length][current length][characters...]
                    var current = Math.Min((int)b[1], b.Length - 2);
                    return Encoding.GetEncoding(1252).GetString(b, 2, current);
                default: throw new ArgumentException($"Data type '{type}' cannot be decoded.");
            }
        }

        /// <summary>Reads several typed values in one connection; a failing item does not stop the others.</summary>
        public static object ReadValues(string ip, IList<Item> items, int? rack, int? slot)
        {
            if (items == null || items.Count == 0)
                throw new ArgumentException("Nothing to read.");

            return WithClient(ip, rack, slot, (client, usedIp) =>
            {
                var values = new List<object>();
                foreach (var item in items)
                {
                    try
                    {
                        if (item.Error != null) throw new InvalidOperationException(item.Error);

                        var loc = ParseAddress(item.Address);
                        var type = string.IsNullOrWhiteSpace(item.Type)
                            ? DefaultType(loc.Size)
                            : item.Type.Trim().ToUpperInvariant().Replace(" ", "");

                        // "String[20]" carries its own length; a bare String is the 254-character default.
                        var sm = Regex.Match(type, @"^STRING\[(\d+)\]$");
                        int? length = null;
                        if (sm.Success) { type = "STRING"; length = int.Parse(sm.Groups[1].Value); }

                        var bytes = ByteCount(type, length);
                        if (type == "BOOL" && loc.Size != 'X')
                            throw new ArgumentException("a Bool needs a bit address such as I0.3 or DB1.DBX0.3.");

                        var buffer = new byte[Math.Max(1, bytes)];
                        int res = client.ReadArea(loc.Area, loc.Db, loc.Byte, buffer.Length, S7Consts.S7WLByte, buffer);
                        if (res != 0) throw new InvalidOperationException(client.ErrorText(res));

                        var value = Decode(type, buffer, loc.Bit);
                        values.Add(new
                        {
                            name = item.Label,
                            address = item.Address,
                            type = type[0] + type.Substring(1).ToLowerInvariant(),
                            value,
                            hex = type == "BOOL" || type == "STRING" ? null : BitConverter.ToString(buffer).Replace("-", "")
                        });
                    }
                    catch (Exception ex)
                    {
                        values.Add(new { name = item.Label, address = item.Address, error = ex.Message });
                    }
                }

                return new { ip = usedIp, readAt = DateTime.Now.ToString("HH:mm:ss.fff"), values };
            });
        }

        // ---- CPU information ---------------------------------------------------

        public static object CpuInfo(string ip, int? rack, int? slot)
        {
            return WithClient(ip, rack, slot, (client, usedIp) =>
            {
                // S7-1200/1500 answer only part of these classic queries; report each on its own.
                string status;
                int raw = 0;
                int res = client.PlcGetStatus(ref raw);
                if (res != 0) status = "unavailable (" + client.ErrorText(res) + ")";
                else if (raw == S7Consts.S7CpuStatusRun) status = "RUN";
                else if (raw == S7Consts.S7CpuStatusStop) status = "STOP";
                else status = "unknown (0x" + raw.ToString("X2") + ")";

                // The version bytes that come with the order code are not meaningful on
                // S7-1200/1500; the firmware is in the hardware configuration (tia_list_modules).
                var order = new S7Client.S7OrderCode();
                res = client.GetOrderCode(ref order);
                var orderCode = res == 0 ? order.Code?.Trim('\0', ' ') : null;

                var cpu = new S7Client.S7CpuInfo();
                res = client.GetCpuInfo(ref cpu);

                return new
                {
                    ip = usedIp,
                    status,
                    orderCode,
                    moduleType = res == 0 ? cpu.ModuleTypeName : null,
                    moduleName = res == 0 ? cpu.ModuleName : null,
                    stationName = res == 0 ? cpu.ASName : null,
                    serialNumber = res == 0 ? cpu.SerialNumber : null,
                    note = res == 0 ? null : "The CPU did not answer the identification query (" + client.ErrorText(res) + ")."
                };
            });
        }
    }
}
