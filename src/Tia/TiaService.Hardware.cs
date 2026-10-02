using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;

namespace TiaMcpServer
{
    // Devices, modules, network and online state.
    internal sealed partial class TiaService
    {
        public object ListDevices()
        {
            EnsureProject();
            var result = new List<object>();
            foreach (var device in AllDevices(_project))
            {
                var software = new List<object>();
                foreach (var item in DeviceItemsRecursive(device))
                {
                    var sw = item.GetService<SoftwareContainer>()?.Software;
                    if (sw == null) continue;
                    software.Add(new
                    {
                        kind = sw is PlcSoftware ? "plc"
                             : sw is HmiSoftware ? "hmiUnified"
                             : sw is HmiTarget ? "hmiClassic"
                             : sw.GetType().Name,
                        name = sw.Name,
                        deviceItem = item.Name
                    });
                }

                result.Add(new
                {
                    name = device.Name,
                    typeIdentifier = device.TypeIdentifier,
                    software
                });
            }
            return new { count = result.Count, devices = result };
        }

        public object ListIpAddresses()
        {
            EnsureProject();
            var result = new List<object>();
            foreach (var device in AllDevices(_project))
            foreach (var item in DeviceItemsRecursive(device))
            {
                var netIf = item.GetService<NetworkInterface>();
                if (netIf == null) continue;
                foreach (Node node in netIf.Nodes)
                {
                    result.Add(new
                    {
                        device = device.Name,
                        item = item.Name,
                        node = node.Name,
                        nodeType = node.NodeType.ToString(),
                        address = (string)SafeGet(() => node.GetAttribute("Address")?.ToString()),
                        subnetMask = (string)SafeGet(() => node.GetAttribute("SubnetMask")?.ToString()),
                        subnet = (string)SafeGet(() => node.ConnectedSubnet?.Name)
                    });
                }
            }
            return new { count = result.Count, nodes = result };
        }

        /// <summary>IP address configured on the first network node of a PLC, or null.</summary>
        private string PlcIpAddress(string deviceName)
        {
            var entry = FindSoftware<PlcSoftware>(deviceName, p => p.Name);
            if (entry == null) return null;
            foreach (var item in DeviceItemsRecursive(entry.Device))
            {
                var netIf = item.GetService<NetworkInterface>();
                if (netIf == null) continue;
                foreach (Node node in netIf.Nodes)
                {
                    var address = Try(() => node.GetAttribute("Address")?.ToString());
                    if (!string.IsNullOrWhiteSpace(address)) return address;
                }
            }
            return null;
        }

        /// <summary>
        /// Modules of a device (or of every device) with slot, article number and the
        /// I/O address ranges they occupy - what is needed to map physical I/O to tags.
        /// </summary>
        public object ListModules(string deviceName)
        {
            EnsureProject();
            var devices = deviceName == null
                ? AllDevices(_project).ToList()
                : new List<Device> { RequireDevice(deviceName) };

            var result = devices.Select(device => new
            {
                device = device.Name,
                modules = DeviceItemsRecursive(device)
                    .Select(item => new { item, addresses = IoAddresses(item) })
                    .Where(x => x.addresses.Count > 0 ||
                                x.item.Classification != DeviceItemClassifications.None)
                    .Select(x => new
                    {
                        name = x.item.Name,
                        typeIdentifier = Try(() => x.item.TypeIdentifier),
                        classification = x.item.Classification == DeviceItemClassifications.None
                            ? null : x.item.Classification.ToString(),
                        slot = Try(() => (int?)x.item.PositionNumber),
                        addresses = x.addresses.Count == 0 ? null : x.addresses
                    })
                    .ToArray()
            }).ToArray();

            return new
            {
                devices = result,
                note = "Address 'bits' is the width TIA reports; 'range' is the byte range it covers."
            };
        }

        private static List<object> IoAddresses(DeviceItem item)
        {
            var list = new List<object>();
            try
            {
                foreach (Address a in item.Addresses)
                {
                    if (a.IoType != AddressIoType.Input && a.IoType != AddressIoType.Output) continue;
                    if (a.StartAddress < 0) continue;   // not assigned
                    var letter = a.IoType == AddressIoType.Input ? "I" : "Q";
                    var bytes = Math.Max(1, (a.Length + 7) / 8);
                    list.Add(new
                    {
                        direction = a.IoType.ToString(),
                        start = a.StartAddress,
                        bits = a.Length,
                        range = bytes == 1
                            ? $"%{letter}B{a.StartAddress}"
                            : $"%{letter}B{a.StartAddress}..%{letter}B{a.StartAddress + bytes - 1}"
                    });
                }
            }
            catch { /* item has no address space */ }
            return list;
        }

        /// <summary>Online/offline state of each PLC as TIA Portal sees it (not a network probe).</summary>
        public object OnlineState(string deviceName)
        {
            EnsureProject();
            var result = new List<object>();
            foreach (var entry in SoftwareEntries<PlcSoftware>())
            {
                if (deviceName != null &&
                    !entry.Software.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase) &&
                    !entry.Item.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase) &&
                    !entry.Device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var provider = Try(() => entry.Item.GetService<OnlineProvider>());
                result.Add(new
                {
                    plc = entry.Software.Name,
                    state = provider == null ? "Unknown" : (string)SafeGet(() => provider.State.ToString()),
                    connectionConfigured = provider == null ? (bool?)null : Try(() => (bool?)provider.Configuration.IsConfigured)
                });
            }

            return new
            {
                plcs = result,
                note = "While a PLC is Online, TIA refuses source generation and block imports " +
                       "(tia_import_scl, tia_export_source, tia_read_block). Go offline in TIA Portal first."
            };
        }

        public object FindHardwareCatalogEntries(string filter)
        {
            EnsureTia();
            var entries = _tia.HardwareCatalog.Find(filter);
            return new
            {
                count = entries.Count,
                entries = entries.Select(e => new
                {
                    articleNumber = e.ArticleNumber,
                    version = e.Version,
                    typeName = e.TypeName,
                    typeIdentifier = e.TypeIdentifier,
                    catalogPath = e.CatalogPath
                }).ToArray()
            };
        }

        public object ChangeDeviceItemType(string deviceName, string itemName, string newTypeIdentifier)
        {
            var item = RequireDeviceItem(RequireDevice(deviceName), itemName);
            var before = item.TypeIdentifier;
            item.ChangeType(newTypeIdentifier);
            return new { changed = true, item = itemName, before, after = item.TypeIdentifier };
        }

        public object DebugDeviceTree(string deviceName)
        {
            var device = RequireDevice(deviceName);

            object Describe(DeviceItem item)
            {
                var container = item.GetService<SoftwareContainer>();
                return new
                {
                    name = item.Name,
                    typeIdentifier = item.TypeIdentifier,
                    hasSoftwareContainer = container != null,
                    softwareTypeName = container?.Software?.GetType().FullName,
                    children = item.DeviceItems.Cast<DeviceItem>().Select(Describe).ToArray()
                };
            }

            return new
            {
                device = device.Name,
                typeIdentifier = device.TypeIdentifier,
                items = device.DeviceItems.Cast<DeviceItem>().Select(Describe).ToArray()
            };
        }

        public object DumpDeviceItemAttributes(string deviceName, string itemName)
        {
            var item = RequireDeviceItem(RequireDevice(deviceName), itemName);

            var attrs = item.GetAttributeInfos().Select(ai => ai.Name)
                .Select(n => new { name = n, value = (string)SafeGet(() => item.GetAttribute(n)?.ToString()) })
                .OrderBy(a => a.name)
                .ToArray();

            return new { device = deviceName, item = item.Name, typeIdentifier = item.TypeIdentifier, attributes = attrs };
        }

        public object DumpPlcProtection(string deviceName)
        {
            var entry = FindSoftware<PlcSoftware>(deviceName, p => p.Name);
            if (entry == null)
                throw new InvalidOperationException($"PLC '{deviceName ?? "(any)"}' not found.");

            var item = entry.Item;
            var accessProvider = item.GetService<PlcAccessLevelProvider>();
            var attrInfoNames = item.GetAttributeInfos().Select(ai => ai.Name).ToList();
            var attrs = attrInfoNames
                .Select(n => new { name = n, value = (string)SafeGet(() => item.GetAttribute(n)?.ToString()) })
                .Where(a => a.name.IndexOf("access", StringComparison.OrdinalIgnoreCase) >= 0
                         || a.name.IndexOf("protect", StringComparison.OrdinalIgnoreCase) >= 0
                         || a.name.IndexOf("putget", StringComparison.OrdinalIgnoreCase) >= 0
                         || a.name.IndexOf("remote", StringComparison.OrdinalIgnoreCase) >= 0
                         || a.name.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(a => a.name)
                .ToArray();

            return new
            {
                plc = entry.Software.Name,
                protectionAccessLevel = accessProvider?.PlcProtectionAccessLevel.ToString(),
                relatedAttributes = attrs,
                allAttributeNames = attrInfoNames.OrderBy(n => n).ToArray()
            };
        }
    }
}
