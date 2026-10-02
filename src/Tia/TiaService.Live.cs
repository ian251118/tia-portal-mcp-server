using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcpServer.Mcp;

namespace TiaMcpServer
{
    // Glue between the project (tag names, configured IP) and the live S7 reader.
    internal sealed partial class TiaService
    {
        private HashSet<string> _projectIps;
        private string _projectIpsOf;

        /// <summary>Every IP address configured on a network node of the open project.</summary>
        private HashSet<string> ProjectIpAddresses(bool refresh)
        {
            EnsureProject();
            var key = _project.Path?.FullName;
            if (!refresh && _projectIps != null && _projectIpsOf == key) return _projectIps;

            var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in AllDevices(_project))
            foreach (var item in DeviceItemsRecursive(device))
            {
                var netIf = item.GetService<NetworkInterface>();
                if (netIf == null) continue;
                foreach (Node node in netIf.Nodes)
                {
                    var address = Try(() => node.GetAttribute("Address")?.ToString());
                    if (!string.IsNullOrWhiteSpace(address)) ips.Add(address.Trim());
                }
            }

            _projectIps = ips;
            _projectIpsOf = key;
            return ips;
        }

        /// <summary>
        /// The live S7 tools open a network connection, so by default they are held to
        /// the addresses the open project itself configures: the server cannot be
        /// talked into probing other hosts. --s7-host / --s7-any-host widen that.
        /// </summary>
        private void CheckS7Target(string ip)
        {
            var options = ServerOptions.Current;
            if (options.S7AnyHost || options.S7Hosts.Contains(ip)) return;

            var known = Try(() => ProjectIpAddresses(false), null);
            if (known != null && !known.Contains(ip))
                known = Try(() => ProjectIpAddresses(true), known);   // the hardware config may have changed
            if (known != null && known.Contains(ip)) return;

            var scope = known == null
                ? "no TIA project is open, so no address is known to be a PLC of yours"
                : "the open project configures only " + (known.Count == 0 ? "no addresses" : string.Join(", ", known.OrderBy(x => x)));
            throw new UnauthorizedAccessException(
                $"Refused to contact {ip}: the live S7 tools only talk to addresses configured in the open TIA project, and {scope}. " +
                $"To allow this address the user can start the server with --s7-host {ip} (or --s7-any-host).");
        }

        /// <summary>
        /// IP to use for a live read: the explicit one, else the last one that worked,
        /// else the address configured for the PLC in the open project.
        /// </summary>
        public string ResolvePlcIp(string ip, string deviceName)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                ip = deviceName == null && !string.IsNullOrWhiteSpace(S7Live.LastIp)
                    ? S7Live.LastIp
                    : Try(() => PlcIpAddress(deviceName)) ?? S7Live.LastIp;
            }

            if (!string.IsNullOrWhiteSpace(ip)) CheckS7Target(ip.Trim());
            return ip;
        }

        /// <summary>
        /// Reads PLC tags by NAME: address and data type come from the project's tag
        /// tables, the values from the running PLC.
        /// </summary>
        public object S7ReadTags(string deviceName, JArray names, string ip, int? rack, int? slot)
        {
            var plc = RequirePlc(deviceName);

            var items = new List<S7Live.Item>();
            foreach (var token in names)
            {
                var name = (string)token;
                var tag = FindPlcTag(plc, name);
                if (tag == null)
                {
                    items.Add(new S7Live.Item { Label = name, Error = $"no PLC tag named '{name}' (DB members are not tags: read them by address with tia_s7_read_values)" });
                    continue;
                }

                items.Add(new S7Live.Item
                {
                    Label = tag.Name,
                    Address = tag.LogicalAddress,
                    Type = tag.DataTypeName,
                    Error = string.IsNullOrWhiteSpace(tag.LogicalAddress) ? "the tag has no absolute address" : null
                });
            }

            return S7Live.ReadValues(ResolvePlcIp(ip, deviceName), items, rack, slot);
        }
    }
}
