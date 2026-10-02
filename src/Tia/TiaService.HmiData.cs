using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiAlarm;
using Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon;
using Siemens.Engineering.HmiUnified.HmiTags;
using Siemens.Engineering.HmiUnified.UI.Controls;
using Siemens.Engineering.HmiUnified.UI.Dynamization;
using Siemens.Engineering.HmiUnified.UI.Dynamization.Script;
using Siemens.Engineering.HmiUnified.UI.Parts;
using Siemens.Engineering.HmiUnified.UI.Screens;
using Siemens.Engineering.HmiUnified.UI.Widgets;
using TiaMcpServer.Mcp;

namespace TiaMcpServer
{
    // WinCC Unified data side: connections, HMI tags, alarms, tag usage, faceplates.
    internal sealed partial class TiaService
    {
        // ---- Overview ----------------------------------------------------------

        public object DumpHmiUnified(string deviceName)
        {
            var hmi = RequireHmiUnified(deviceName);

            var connections = hmi.Connections.Select(c => new
            {
                name = c.Name,
                communicationDriver = c.CommunicationDriver,
                initialAddress = c.InitialAddress,
                disabledAtStartup = c.DisabledAtStartup,
                node = SafeGet(() => c.Node),
                partner = SafeGet(() => c.Partner),
                station = SafeGet(() => c.Station),
                comment = c.Comment
            }).ToArray();

            var tagTables = hmi.TagTables.Select(tt => new
            {
                table = tt.Name,
                tags = tt.Tags.Select(DescribeHmiTag).ToArray()
            }).ToArray();

            var screens = AllUnifiedScreens(hmi).Select(s => new
            {
                name = s.Name,
                group = NullIfEmpty(ScreenGroupPath(s)),
                items = s.ScreenItems.Select(i => DescribeItem(i, true)).ToArray()
            }).ToArray();

            return new
            {
                device = hmi.Name,
                startScreen = Try(() => hmi.RuntimeSettings.StartScreen),
                connections,
                tagTables,
                screens
            };
        }

        public object SetHmiConnectionEnabled(string deviceName, string connectionName, bool enabled)
        {
            var hmi = RequireHmiUnified(deviceName);
            var conn = hmi.Connections.Find(connectionName);
            if (conn == null)
                throw new InvalidOperationException(
                    $"HMI connection '{connectionName}' not found on '{hmi.Name}'. Use tia_dump_hmi_unified to list the connections.");

            return Edit($"{(enabled ? "enable" : "disable")} connection {connectionName}", () =>
            {
                conn.DisabledAtStartup = !enabled;
                return new { connection = conn.Name, disabledAtStartup = conn.DisabledAtStartup };
            });
        }

        // ---- HMI tags ----------------------------------------------------------

        private static object DescribeHmiTag(HmiTag t) => new
        {
            name = t.Name,
            table = Try(() => t.TagTableName),
            connection = t.Connection,
            plcTag = NullIfEmpty(t.PlcTag),
            dataType = t.DataType,
            hmiDataType = Try(() => t.HmiDataType),
            address = NullIfEmpty(Try(() => t.Address)),
            accessMode = Try(() => t.AccessMode.ToString()),
            acquisitionCycle = Try(() => t.AcquisitionCycle)
        };

        private static HmiTag FindHmiTag(HmiSoftware hmi, string name) =>
            hmi.TagTables.SelectMany(tt => tt.Tags)
               .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        private static HmiTagTable RequireHmiTagTable(HmiSoftware hmi, string tableName)
        {
            var table = string.IsNullOrEmpty(tableName) ? hmi.TagTables.FirstOrDefault() : hmi.TagTables.Find(tableName);
            if (table == null)
                throw new InvalidOperationException(
                    $"HMI tag table '{tableName ?? "(default)"}' not found on '{hmi.Name}'. Create it with tia_hmi_create_tag_table.");
            return table;
        }

        public object ListHmiTags(string deviceName, string tableName, string filter)
        {
            var hmi = RequireHmiUnified(deviceName);
            var tags = hmi.TagTables
                .Where(tt => tableName == null || tt.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                .SelectMany(tt => tt.Tags)
                .Where(t => string.IsNullOrEmpty(filter) ||
                            t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (t.PlcTag ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(DescribeHmiTag)
                .ToArray();

            return new
            {
                device = hmi.Name,
                tables = hmi.TagTables.Select(tt => new { name = tt.Name, tagCount = tt.Tags.Count }).ToArray(),
                connections = hmi.Connections.Select(c => c.Name).ToArray(),
                count = tags.Length,
                tags
            };
        }

        public object CreateHmiTagTable(string deviceName, string name)
        {
            var hmi = RequireHmiUnified(deviceName);
            if (hmi.TagTables.Find(name) != null)
                return new { created = false, exists = true, table = name };

            return Edit($"create HMI tag table {name}", () =>
            {
                var table = hmi.TagTables.Create(name);
                return new { created = true, exists = false, table = table.Name };
            });
        }

        /// <summary>
        /// Creates one HMI tag. With a connection + plcTag it is linked to the PLC
        /// (Connection must be set before PlcTag, or TIA cannot resolve the PLC tag);
        /// without them it is an HMI-internal tag.
        /// </summary>
        private static object CreateOneHmiTag(HmiSoftware hmi, HmiTagTable table, Args spec)
        {
            var name = spec.ReqStr("name");
            var existing = FindHmiTag(hmi, name);
            if (existing != null)
            {
                if (!spec.Bool("overwrite"))
                    return new { name, status = "exists", note = "left untouched; pass overwrite:true to recreate it" };
                existing.Delete();
            }

            var connection = spec.Str("connection");
            var plcTag = spec.Str("plcTag");
            if ((connection == null) != (plcTag == null))
                throw new ArgumentException(
                    $"Tag '{name}': give both 'connection' and 'plcTag' to link it to the PLC, or neither for an internal tag.");

            if (connection != null && hmi.Connections.Find(connection) == null)
                throw new InvalidOperationException(
                    $"Tag '{name}': HMI connection '{connection}' does not exist. Available: " +
                    string.Join(", ", hmi.Connections.Select(c => c.Name)) + ".");

            var tag = table.Tags.Create(name);
            var dataType = spec.Str("dataType");
            if (connection != null)
            {
                tag.Connection = connection;
                tag.PlcTag = plcTag;
                if (dataType != null) tag.DataType = dataType;
            }
            else
            {
                tag.DataType = dataType ?? "Bool";
            }

            if (spec.Has("acquisitionCycle")) tag.AcquisitionCycle = spec.Str("acquisitionCycle");

            var issues = tag.Validate()
                .Select(v => new
                {
                    property = v.PropertyName,
                    errors = v.Errors.Cast<object>().Select(e => e.ToString()).ToArray(),
                    warnings = v.Warnings.Cast<object>().Select(w => w.ToString()).ToArray()
                })
                .Where(v => v.errors.Length > 0 || v.warnings.Length > 0)
                .ToArray();

            return new
            {
                name = tag.Name,
                status = existing != null ? "replaced" : "created",
                kind = connection != null ? "plc" : "internal",
                table = table.Name,
                connection = tag.Connection,
                plcTag = NullIfEmpty(tag.PlcTag),
                dataType = tag.DataType,
                validationIssues = issues.Length == 0 ? null : issues
            };
        }

        public object CreateHmiTag(string deviceName, string name, string connectionName,
                                   string plcTag, string dataType, string tableName, bool overwrite)
        {
            var spec = new JObject { ["name"] = name, ["overwrite"] = overwrite };
            if (connectionName != null) spec["connection"] = connectionName;
            if (plcTag != null) spec["plcTag"] = plcTag;
            if (dataType != null) spec["dataType"] = dataType;

            var hmi = RequireHmiUnified(deviceName);
            var table = RequireHmiTagTable(hmi, tableName);
            return Edit($"create HMI tag {name}", () => CreateOneHmiTag(hmi, table, new Args(spec)));
        }

        public object CreateHmiTags(string deviceName, string tableName, JArray tags)
        {
            var hmi = RequireHmiUnified(deviceName);
            var defaultTable = RequireHmiTagTable(hmi, tableName);

            return Edit($"create {tags.Count} HMI tag(s)", () =>
            {
                var results = new List<object>();
                foreach (var token in tags)
                {
                    var spec = new Args(token as JObject);
                    var name = spec.Str("name");
                    try
                    {
                        var table = spec.Has("table") ? RequireHmiTagTable(hmi, spec.Str("table")) : defaultTable;
                        results.Add(CreateOneHmiTag(hmi, table, spec));
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { name, status = "error", reason = Brief(ex.Message) });
                    }
                }
                return new { device = hmi.Name, results };
            });
        }

        public object DeleteHmiTag(string deviceName, string tagName)
        {
            var hmi = RequireHmiUnified(deviceName);
            var tag = FindHmiTag(hmi, tagName);
            if (tag == null)
                throw new InvalidOperationException($"HMI tag '{tagName}' not found on '{hmi.Name}'.");

            var removed = DescribeHmiTag(tag);
            return Edit($"delete HMI tag {tagName}", () =>
            {
                tag.Delete();
                return new { deleted = true, removed, note = "Screen bindings to this tag are now broken; check with tia_hmi_find_tag_usage." };
            });
        }

        public object SetHmiTagAcquisitionCycle(string deviceName, string tagName, string cycle)
        {
            var hmi = RequireHmiUnified(deviceName);
            var tag = FindHmiTag(hmi, tagName);
            if (tag == null)
                throw new InvalidOperationException($"HMI tag '{tagName}' not found on '{hmi.Name}'.");

            if (string.IsNullOrEmpty(tag.PlcTag) && string.IsNullOrEmpty(Try(() => tag.Address)))
                throw new InvalidOperationException(
                    $"'{tagName}' is an HMI-internal tag: it is not read from a PLC, so it has no acquisition cycle.");

            var previous = Try(() => tag.AcquisitionCycle);
            return Edit($"set acquisition cycle of {tagName}", () =>
            {
                tag.AcquisitionCycle = cycle;
                return new { set = true, tag = tagName, acquisitionCycle = tag.AcquisitionCycle, previous };
            });
        }

        /// <summary>
        /// Assigns a number to an Openness property typed as Object. Those reject
        /// strings and want the tag's own numeric type, which is not known here, so the
        /// plausible .NET types are tried in turn.
        /// </summary>
        private static void SetNumber(Action<object> setter, string text, string what)
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            var candidates = new List<object>();
            if (long.TryParse(text, System.Globalization.NumberStyles.Integer, invariant, out var whole))
            {
                if (whole >= int.MinValue && whole <= int.MaxValue) candidates.Add((int)whole);
                candidates.Add(whole);
                candidates.Add((double)whole);
                candidates.Add((float)whole);
            }
            else if (double.TryParse(text, System.Globalization.NumberStyles.Float, invariant, out var real))
            {
                candidates.Add(real);
                candidates.Add((float)real);
            }
            else
            {
                throw new ArgumentException($"'{what}' must be a number, got '{text}'.");
            }

            Exception last = null;
            foreach (var candidate in candidates)
            {
                try { setter(candidate); return; }
                catch (Exception ex) { last = ex; }
            }
            throw new InvalidOperationException($"TIA rejected '{text}' for '{what}': {Brief(last?.Message)}");
        }

        // ---- Tag usage ---------------------------------------------------------

        private sealed class TagUse
        {
            public string screen, item, kind, where, value;
        }

        private static IEnumerable<TagUse> TagUses(HmiScreen screen)
        {
            IEnumerable<TagUse> Of(object target, string itemName)
            {
                foreach (var d in DynamizationsOf(target))
                {
                    if (d is TagDynamization td)
                        yield return new TagUse { screen = screen.Name, item = itemName, kind = "TagDynamization", where = td.PropertyName, value = td.Tag };
                    else if (d is ScriptDynamization sd)
                        yield return new TagUse { screen = screen.Name, item = itemName, kind = "ScriptDynamization", where = sd.PropertyName, value = sd.ScriptCode };
                }

                if (GetProp(target, "EventHandlers") is IEnumerable handlers)
                {
                    foreach (var h in handlers)
                    {
                        var code = Try(() => (GetProp(h, "Script") as IHmiScript)?.ScriptCode);
                        if (!string.IsNullOrEmpty(code))
                            yield return new TagUse { screen = screen.Name, item = itemName, kind = "EventScript", where = GetProp(h, "EventType")?.ToString(), value = code };
                    }
                }

                if (target is HmiFaceplateContainer fp)
                {
                    foreach (HmiFaceplateInterface iface in fp.Interface)
                        yield return new TagUse { screen = screen.Name, item = itemName, kind = "FaceplateInterface", where = iface.PropertyName, value = iface.Value?.ToString() ?? "" };
                }
            }

            foreach (var use in Of(screen, ScreenSelf)) yield return use;
            foreach (var item in screen.ScreenItems)
                foreach (var use in Of(item, item.Name))
                    yield return use;
        }

        /// <summary>
        /// Where HMI tags are used on screens: tag bindings, faceplate interface
        /// parameters, and (as text matches) property/event scripts.
        /// </summary>
        public object FindHmiTagUsage(string deviceName, string screenName, string filter)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screens = screenName != null
                ? new List<HmiScreen> { RequireUnifiedScreen(hmi, screenName) }
                : AllUnifiedScreens(hmi).ToList();

            var uses = screens.SelectMany(TagUses)
                .Where(u => string.IsNullOrEmpty(filter) ||
                            (u.value ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                // Without a filter, scripts are noise: list only the direct tag references.
                .Where(u => !string.IsNullOrEmpty(filter) || u.kind == "TagDynamization" || u.kind == "FaceplateInterface")
                .Select(u => new
                {
                    u.screen,
                    u.item,
                    u.kind,
                    u.where,
                    value = u.kind.EndsWith("Script") || u.kind == "ScriptDynamization" ? Excerpt(u.value, filter) : u.value
                })
                .ToArray();

            return new { device = hmi.Name, screensSearched = screens.Count, filter, count = uses.Length, uses };
        }

        private static string Excerpt(string text, string term)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return text;
            var i = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return text;
            var start = Math.Max(0, i - 60);
            var end = Math.Min(text.Length, i + term.Length + 60);
            return (start > 0 ? "..." : "") + text.Substring(start, end - start).Replace("\r", "").Replace("\n", " ") + (end < text.Length ? "..." : "");
        }

        /// <summary>
        /// Regex find/replace over tag bindings and faceplate interface parameters, on one
        /// screen or all of them. dryRun lists the changes without applying them.
        /// </summary>
        public object ReplaceHmiTagRefs(string deviceName, string screenName, string pattern, string replacement, bool dryRun)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screens = screenName != null
                ? new List<HmiScreen> { RequireUnifiedScreen(hmi, screenName) }
                : AllUnifiedScreens(hmi).ToList();

            Regex rx;
            try { rx = new Regex(pattern, RegexOptions.IgnoreCase); }
            catch (ArgumentException ex) { throw new ArgumentException($"'pattern' is not a valid regular expression: {ex.Message}"); }

            object Run()
            {
                var changes = new List<object>();
                foreach (var screen in screens)
                {
                    var targets = new List<KeyValuePair<string, object>> { new KeyValuePair<string, object>(ScreenSelf, screen) };
                    targets.AddRange(screen.ScreenItems.Select(i => new KeyValuePair<string, object>(i.Name, (object)i)));

                    foreach (var t in targets)
                    {
                        foreach (var d in DynamizationsOf(t.Value))
                        {
                            if (!(d is TagDynamization td)) continue;
                            var current = td.Tag ?? "";
                            if (!rx.IsMatch(current)) continue;
                            var updated = rx.Replace(current, replacement);
                            if (updated == current) continue;
                            if (!dryRun) td.Tag = updated;
                            changes.Add(new { screen = screen.Name, item = t.Key, kind = "TagDynamization", property = td.PropertyName, from = current, to = updated });
                        }

                        if (t.Value is HmiFaceplateContainer fp)
                        {
                            foreach (HmiFaceplateInterface iface in fp.Interface)
                            {
                                var current = iface.Value?.ToString() ?? "";
                                if (!rx.IsMatch(current)) continue;
                                var updated = rx.Replace(current, replacement);
                                if (updated == current) continue;
                                if (!dryRun) iface.Value = updated;
                                changes.Add(new { screen = screen.Name, item = t.Key, kind = "FaceplateInterface", property = iface.PropertyName, from = current, to = updated });
                            }
                        }
                    }
                }

                return new
                {
                    dryRun,
                    applied = !dryRun,
                    count = changes.Count,
                    changes,
                    note = dryRun ? "Nothing was changed. Review the list with the user, then call again with dryRun:false and confirm:true." : null
                };
            }

            return dryRun ? Run() : Edit($"replace tag references /{pattern}/", Run);
        }

        public object UpdateFaceplateInterface(string deviceName, string screenName, JArray updates)
        {
            var hmi = RequireHmiUnified(deviceName);
            var screen = RequireUnifiedScreen(hmi, screenName);

            return Edit($"update faceplate parameters on {screenName}", () =>
            {
                var results = new List<object>();
                foreach (var token in updates)
                {
                    var u = new Args(token as JObject);
                    var container = u.ReqStr("container");
                    var parameter = u.ReqStr("parameter");
                    var value = u.ReqStr("value");

                    var fp = RequireScreenItem(screen, container) as HmiFaceplateContainer;
                    if (fp == null)
                        throw new InvalidOperationException($"'{container}' on '{screen.Name}' is not a faceplate container.");

                    var iface = fp.Interface.Cast<HmiFaceplateInterface>()
                        .FirstOrDefault(i => i.PropertyName.Equals(parameter, StringComparison.OrdinalIgnoreCase));
                    if (iface == null)
                        throw new InvalidOperationException(
                            $"Faceplate '{container}' has no interface parameter '{parameter}'. Available: " +
                            string.Join(", ", fp.Interface.Cast<HmiFaceplateInterface>().Select(i => i.PropertyName)) + ".");

                    var previous = iface.Value?.ToString();
                    iface.Value = value;
                    results.Add(new { container = fp.Name, parameter = iface.PropertyName, value, previous });
                }
                return new { screen = screen.Name, updated = results.Count, results };
            });
        }

        // ---- Alarms ------------------------------------------------------------

        public object ListHmiAlarms(string deviceName)
        {
            var hmi = RequireHmiUnified(deviceName);

            return new
            {
                device = hmi.Name,
                alarmClasses = hmi.AlarmClasses.Select(c => new
                {
                    name = c.Name,
                    priority = Try(() => (int?)c.Priority),
                    isSystem = Try(() => (bool?)c.IsSystem)
                }).ToArray(),
                discreteAlarms = hmi.DiscreteAlarms.Select(a => new
                {
                    name = a.Name,
                    id = Try(() => (uint?)a.Id),
                    alarmClass = a.AlarmClass,
                    tag = a.RaisedStateTag,
                    bit = Try(() => (uint?)a.RaisedStateTagBitNumber),
                    triggerMode = Try(() => a.TriggerMode.ToString()),
                    text = PlainText(Try(() => FirstText(a.EventText))),
                    priority = Try(() => (int?)a.Priority)
                }).ToArray(),
                analogAlarms = hmi.AnalogAlarms.Select(a => new
                {
                    name = a.Name,
                    id = Try(() => (uint?)a.Id),
                    alarmClass = a.AlarmClass,
                    tag = a.RaisedStateTag,
                    condition = Try(() => a.Condition.ToString()),
                    conditionValue = Try(() => a.ConditionValue?.ToString()),
                    text = PlainText(Try(() => FirstText(a.EventText))),
                    priority = Try(() => (int?)a.Priority)
                }).ToArray()
            };
        }

        /// <summary>
        /// Creates a discrete alarm (raised by one bit of an HMI tag) or an analog alarm
        /// (raised when an HMI tag crosses a limit).
        /// </summary>
        public object CreateHmiAlarm(string deviceName, string kind, string name, string tag, int? bit,
                                     string alarmClass, string text, int? priority,
                                     string triggerMode, string condition, string conditionValue, bool overwrite)
        {
            var hmi = RequireHmiUnified(deviceName);
            kind = (kind ?? "discrete").ToLowerInvariant();
            if (kind != "discrete" && kind != "analog")
                throw new ArgumentException("kind must be discrete or analog.");

            if (FindHmiTag(hmi, tag) == null)
                throw new InvalidOperationException(
                    $"HMI tag '{tag}' does not exist on '{hmi.Name}'. Create it first (tia_hmi_create_tags).");
            if (alarmClass != null && hmi.AlarmClasses.Find(alarmClass) == null)
                throw new InvalidOperationException(
                    $"Alarm class '{alarmClass}' does not exist. Available: " +
                    string.Join(", ", hmi.AlarmClasses.Select(c => c.Name)) + ".");

            var existingDiscrete = hmi.DiscreteAlarms.Find(name);
            var existingAnalog = hmi.AnalogAlarms.Find(name);
            var exists = existingDiscrete != null || existingAnalog != null;
            if (exists && !overwrite)
                return new { created = false, exists = true, name };

            return Edit($"create {kind} alarm {name}", () =>
            {
                existingDiscrete?.Delete();
                existingAnalog?.Delete();

                if (kind == "discrete")
                {
                    var alarm = hmi.DiscreteAlarms.Create(name);
                    alarm.RaisedStateTag = tag;
                    if (bit != null) alarm.RaisedStateTagBitNumber = (uint)bit.Value;
                    if (alarmClass != null) alarm.AlarmClass = alarmClass;
                    if (priority != null) alarm.Priority = (byte)priority.Value;
                    if (triggerMode != null)
                        alarm.TriggerMode = (HmiDiscreteAlarmTriggerMode)ConvertValue(triggerMode, typeof(HmiDiscreteAlarmTriggerMode), "triggerMode");
                    if (text != null) SetMultilingualText(alarm.EventText, text);

                    return (object)new
                    {
                        created = true, exists = false, kind, name = alarm.Name,
                        tag = alarm.RaisedStateTag,
                        bit = Try(() => (uint?)alarm.RaisedStateTagBitNumber),
                        alarmClass = alarm.AlarmClass,
                        text = PlainText(Try(() => FirstText(alarm.EventText))),
                        replacedExisting = exists
                    };
                }
                else
                {
                    if (condition == null || conditionValue == null)
                        throw new ArgumentException("An analog alarm needs 'condition' and 'conditionValue'.");

                    var alarm = hmi.AnalogAlarms.Create(name);
                    alarm.RaisedStateTag = tag;
                    alarm.Condition = (HmiAlarmCondition)ConvertValue(condition, typeof(HmiAlarmCondition), "condition");
                    SetNumber(v => alarm.ConditionValue = v, conditionValue, "conditionValue");
                    if (alarmClass != null) alarm.AlarmClass = alarmClass;
                    if (priority != null) alarm.Priority = (byte)priority.Value;
                    if (text != null) SetMultilingualText(alarm.EventText, text);

                    return (object)new
                    {
                        created = true, exists = false, kind, name = alarm.Name,
                        tag = alarm.RaisedStateTag,
                        condition = alarm.Condition.ToString(),
                        conditionValue = Try(() => alarm.ConditionValue?.ToString()),
                        alarmClass = alarm.AlarmClass,
                        text = PlainText(Try(() => FirstText(alarm.EventText))),
                        replacedExisting = exists
                    };
                }
            });
        }

        public object DeleteHmiAlarm(string deviceName, string name)
        {
            var hmi = RequireHmiUnified(deviceName);
            var discrete = hmi.DiscreteAlarms.Find(name);
            var analog = hmi.AnalogAlarms.Find(name);
            if (discrete == null && analog == null)
                throw new InvalidOperationException($"No discrete or analog alarm named '{name}' on '{hmi.Name}'.");

            return Edit($"delete alarm {name}", () =>
            {
                discrete?.Delete();
                analog?.Delete();
                return new { deleted = true, name, kind = discrete != null ? "discrete" : "analog" };
            });
        }
    }
}
