using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer.Mcp
{
    /// <summary>What a tool can do to the TIA project. Drives listing, gating and annotations.</summary>
    internal enum Safety
    {
        /// <summary>Never modifies the project (may write an export file to disk).</summary>
        ReadOnly,
        /// <summary>Creates new objects or changes properties of existing ones; nothing is removed.</summary>
        Write,
        /// <summary>Deletes or replaces existing content, or discards unsaved work. Needs confirm:true.</summary>
        Destructive
    }

    /// <summary>One MCP tool: metadata, JSON schema and handler.</summary>
    internal sealed class ToolDef
    {
        public string Name { get; }
        public string Group { get; }
        public Safety Safety { get; }
        public string Title { get; }
        public string Summary { get; }

        public JObject Properties { get; } = new JObject();
        public List<string> Required { get; } = new List<string>();
        public List<string> PathParams { get; } = new List<string>();

        public Func<Args, object> Handler { get; private set; }

        /// <summary>For Write tools that turn destructive with certain arguments (e.g. overwrite:true).</summary>
        public Func<Args, bool> DestructiveWhen { get; private set; }
        public string DestructiveWhenText { get; private set; }

        /// <summary>True when the handler itself decides whether confirmation is needed.</summary>
        public bool HandlerConfirms { get; private set; }

        public bool IsIdempotent { get; private set; }
        public bool IsOpenWorld { get; private set; }

        public const string ConfirmDescription =
            "Set true only after the user has approved this specific deletion/overwrite " +
            "(an explicit instruction to build or iterate on these objects counts). Without it the call is refused.";

        public ToolDef(string name, string group, Safety safety, string title, string summary)
        {
            Name = name;
            Group = group;
            Safety = safety;
            Title = title;
            Summary = summary;

            if (safety == Safety.Destructive)
            {
                Properties["confirm"] = Prop("boolean", "Must be true. " + ConfirmDescription);
                Required.Add("confirm");
            }
        }

        /// <summary>True when this tool can, with some arguments, delete or overwrite content.</summary>
        public bool CanBeDestructive =>
            Safety == Safety.Destructive || DestructiveWhen != null || HandlerConfirms;

        // ---- fluent schema builder --------------------------------------------

        private static JObject Prop(string type, string description) =>
            new JObject { ["type"] = type, ["description"] = description };

        private ToolDef Add(string name, JObject schema, bool required)
        {
            // Keep 'confirm' last so the real arguments read first.
            var confirm = Properties["confirm"];
            if (confirm != null) Properties.Remove("confirm");
            Properties[name] = schema;
            if (confirm != null) Properties["confirm"] = confirm;

            if (required) Required.Insert(Required.Contains("confirm") ? Required.Count - 1 : Required.Count, name);
            return this;
        }

        public ToolDef Str(string name, string description, bool required = false) =>
            Add(name, Prop("string", description), required);

        public ToolDef Int(string name, string description, bool required = false) =>
            Add(name, Prop("integer", description), required);

        public ToolDef Num(string name, string description, bool required = false) =>
            Add(name, Prop("number", description), required);

        public ToolDef Bool(string name, string description, bool required = false) =>
            Add(name, Prop("boolean", description), required);

        /// <summary>A file or directory argument; subject to the --allowed-dir sandbox.</summary>
        public ToolDef Path(string name, string description, bool required = false)
        {
            PathParams.Add(name);
            return Add(name, Prop("string", description), required);
        }

        /// <summary>An array argument whose items follow <paramref name="itemSchema"/>.</summary>
        public ToolDef Arr(string name, string description, JObject itemSchema, bool required = false) =>
            Add(name, new JObject { ["type"] = "array", ["description"] = description, ["items"] = itemSchema }, required);

        /// <summary>An argument with a hand-written JSON schema.</summary>
        public ToolDef Raw(string name, JObject schema, bool required = false) => Add(name, schema, required);

        /// <summary>Marks a Write tool as destructive when the predicate holds; adds an optional 'confirm'.</summary>
        public ToolDef DestructiveIf(Func<Args, bool> predicate, string whenText)
        {
            DestructiveWhen = predicate;
            DestructiveWhenText = whenText;
            AddOptionalConfirm("Required when " + whenText + ". ");
            return this;
        }

        /// <summary>
        /// The handler inspects the project and calls SafetyPolicy.RequireDestructive itself
        /// when (and only when) the call would actually replace something.
        /// </summary>
        public ToolDef ConfirmsInHandler(string whenText)
        {
            HandlerConfirms = true;
            DestructiveWhenText = whenText;
            AddOptionalConfirm("Required when " + whenText + ". ");
            return this;
        }

        private void AddOptionalConfirm(string prefix)
        {
            if (Properties["confirm"] == null)
                Properties["confirm"] = Prop("boolean", prefix + ConfirmDescription);
        }

        public ToolDef Idempotent() { IsIdempotent = true; return this; }

        /// <summary>The tool talks to something outside TIA Portal (the physical PLC, the network).</summary>
        public ToolDef OpenWorld() { IsOpenWorld = true; return this; }

        public ToolDef Run(Func<Args, object> handler) { Handler = handler; return this; }

        // ---- MCP rendering -----------------------------------------------------

        /// <summary>Safety tag shown in front of every description, for clients that ignore annotations.</summary>
        public string SafetyTag
        {
            get
            {
                switch (Safety)
                {
                    case Safety.ReadOnly: return "[READ-ONLY]";
                    case Safety.Destructive: return "[DESTRUCTIVE - needs confirm:true]";
                    default:
                        return CanBeDestructive
                            ? "[WRITE - DESTRUCTIVE when " + DestructiveWhenText + "]"
                            : "[WRITE]";
                }
            }
        }

        public string Description => SafetyTag + " " + Summary;

        public JObject ToMcp()
        {
            var schema = new JObject { ["type"] = "object", ["properties"] = Properties };
            if (Required.Count > 0) schema["required"] = new JArray(Required);

            return new JObject
            {
                ["name"] = Name,
                ["title"] = Title,
                ["description"] = Description,
                ["inputSchema"] = schema,
                ["annotations"] = new JObject
                {
                    ["title"] = Title,
                    ["readOnlyHint"] = Safety == Safety.ReadOnly,
                    ["destructiveHint"] = CanBeDestructive,
                    ["idempotentHint"] = Safety == Safety.ReadOnly || IsIdempotent,
                    ["openWorldHint"] = IsOpenWorld
                }
            };
        }
    }

    /// <summary>All tools known to the server, plus the subset the current options expose.</summary>
    internal sealed class ToolRegistry
    {
        private readonly List<ToolDef> _tools = new List<ToolDef>();

        public IReadOnlyList<ToolDef> All => _tools;

        public ToolDef Add(string name, string group, Safety safety, string title, string summary)
        {
            if (_tools.Any(t => t.Name == name))
                throw new InvalidOperationException($"Duplicate tool name '{name}'.");
            var tool = new ToolDef(name, group, safety, title, summary);
            _tools.Add(tool);
            return tool;
        }

        public ToolDef Find(string name) => _tools.FirstOrDefault(t => t.Name == name);

        /// <summary>Why a tool is not available under the given options, or null if it is.</summary>
        public static string DisabledReason(ToolDef tool, ServerOptions options)
        {
            if (!options.Groups.Contains(tool.Group))
                return $"its tool group '{tool.Group}' is not enabled (server started with --groups {string.Join(",", options.Groups)})";

            if (tool.Safety == Safety.Write && options.Access == AccessLevel.ReadOnly)
                return "it modifies the project and this server runs with --access readonly";

            if (tool.Safety == Safety.Destructive && options.Access != AccessLevel.Full)
                return $"it is destructive and this server runs with --access {options.Access.ToString().ToLowerInvariant()}";

            return null;
        }

        public IEnumerable<ToolDef> Enabled(ServerOptions options) =>
            _tools.Where(t => DisabledReason(t, options) == null);
    }

    /// <summary>Shorthand for the item schemas used by array arguments.</summary>
    internal static class Schema
    {
        public static JObject Object(params (string name, string type, string description, bool required)[] fields)
        {
            var props = new JObject();
            var required = new JArray();
            foreach (var f in fields)
            {
                props[f.name] = new JObject { ["type"] = f.type, ["description"] = f.description };
                if (f.required) required.Add(f.name);
            }
            var o = new JObject { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0) o["required"] = required;
            return o;
        }

        public static JObject String(string description = null)
        {
            var o = new JObject { ["type"] = "string" };
            if (description != null) o["description"] = description;
            return o;
        }
    }
}
