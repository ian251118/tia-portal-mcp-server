using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer.Mcp
{
    /// <summary>
    /// MCP over newline-delimited JSON-RPC 2.0 on stdio, hand-written because the
    /// official .NET MCP SDK does not run on .NET Framework 4.8 (which TIA Openness
    /// requires). Owns protocol handling and the safety gates; knows nothing about TIA.
    /// </summary>
    internal sealed class McpServer
    {
        public const string ServerName = "tia-portal-mcp";
        public const string ServerVersion = "1.0.1";

        // Newest first. We answer with the client's version when we know it, else our newest.
        private static readonly string[] SupportedProtocolVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

        private const string Instructions =
            "Tools for engineering a Siemens TIA Portal V20 project through TIA Openness: PLC blocks (SCL/LAD), " +
            "tags, data types, hardware info, WinCC Unified HMI screens, and live S7 reads.\n" +
            "Start with tia_connect (TIA Portal must be running with the project open), then tia_state.\n" +
            "Every tool description starts with its safety class: [READ-ONLY], [WRITE] or [DESTRUCTIVE]. " +
            "Destructive calls need confirm:true and must only be confirmed after the user has agreed to " +
            "that specific deletion or overwrite.\n" +
            "Edits live in TIA Portal's memory until tia_save_project is called; do not save unless the user asks. " +
            "After changing PLC code call tia_compile and read the messages: importing without errors does not " +
            "prove the program is correct, and nothing here downloads to the PLC.";

        // Results are read by a model: absent beats "field": null, it costs fewer tokens.
        private static readonly JsonSerializerSettings ResultSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly ToolRegistry _registry;
        private readonly ServerOptions _options;

        public McpServer(ToolRegistry registry, ServerOptions options)
        {
            _registry = registry;
            _options = options;
        }

        public void Run()
        {
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            Log.Info($"{ServerName} {ServerVersion} started (access={_options.Access.ToString().ToLowerInvariant()}, " +
                     $"groups={string.Join(",", _options.Groups)}, tools={_registry.Enabled(_options).Count()}).");

            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                JToken response;
                try
                {
                    response = Handle(JObject.Parse(line));
                }
                catch (Exception ex)
                {
                    Log.Error($"Fatal parse/dispatch error: {ex}");
                    response = ErrorResponse(null, -32700, "Parse error: " + ex.Message);
                }

                if (response != null)
                    stdout.WriteLine(response.ToString(Formatting.None));
            }

            Log.Info("stdin closed, shutting down.");
        }

        // ---- JSON-RPC / MCP dispatch ------------------------------------------

        internal JToken Handle(JObject req)
        {
            var method = (string)req["method"];
            var id = req["id"];
            var isNotification = id == null;

            switch (method)
            {
                case "initialize":
                    var requested = (string)req["params"]?["protocolVersion"];
                    var version = SupportedProtocolVersions.Contains(requested)
                        ? requested
                        : SupportedProtocolVersions[0];
                    return Result(id, new JObject
                    {
                        ["protocolVersion"] = version,
                        ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JObject
                        {
                            ["name"] = ServerName,
                            ["version"] = ServerVersion
                        },
                        ["instructions"] = Instructions
                    });

                case "ping":
                    return Result(id, new JObject());

                case "tools/list":
                    return Result(id, new JObject
                    {
                        ["tools"] = new JArray(_registry.Enabled(_options).Select(t => t.ToMcp()))
                    });

                case "tools/call":
                    return HandleToolCall(id, req["params"] as JObject);

                default:
                    // Notifications (initialized, cancelled, ...) never get a response.
                    if (isNotification) return null;
                    return ErrorResponse(id, -32601, $"Method not found: {method}");
            }
        }

        private JToken HandleToolCall(JToken id, JObject @params)
        {
            var name = (string)@params?["name"];
            var raw = @params?["arguments"] as JObject ?? new JObject();

            var tool = _registry.Find(name);
            if (tool == null)
                return ErrorResponse(id, -32602, $"Unknown tool: {name}");

            var args = new Args(raw);
            var watch = Stopwatch.StartNew();
            string error = null;
            JObject result;

            try
            {
                Log.Info($"tools/call {name} {Abbreviate(raw).ToString(Formatting.None)}");
                Gate(tool, args);
                var data = tool.Handler(args);
                result = ToolText(JsonConvert.SerializeObject(data, Formatting.Indented, ResultSettings), false);
            }
            catch (Exception ex)
            {
                // Reflection-invoked Openness calls wrap the real failure.
                var real = ex is System.Reflection.TargetInvocationException && ex.InnerException != null
                    ? ex.InnerException
                    : ex;
                error = real.Message;
                if (real is ConfirmationRequiredException || real is UnauthorizedAccessException)
                    Log.Warn($"Tool '{name}' refused: {real.Message}");
                else if (real is InvalidOperationException || real is ArgumentException || real is FileNotFoundException)
                    Log.Warn($"Tool '{name}' failed: {real.Message}");   // an answer for the caller, not a server fault
                else
                    Log.Error($"Tool '{name}' failed: {real}");
                result = ToolText("ERROR: " + real.Message, true);
            }

            Audit(tool, raw, error, watch.ElapsedMilliseconds);
            return Result(id, result);
        }

        /// <summary>
        /// Everything that must hold before a handler runs: the tool is enabled under the
        /// current options, destructive calls are confirmed, and file arguments stay
        /// inside the sandbox. A tool hidden from tools/list is refused here too, so a
        /// model that guesses a name still cannot call it.
        /// </summary>
        private void Gate(ToolDef tool, Args args)
        {
            var reason = ToolRegistry.DisabledReason(tool, _options);
            if (reason != null)
                throw new UnauthorizedAccessException($"Tool '{tool.Name}' is not available: {reason}.");

            if (tool.Safety == Safety.Destructive)
                SafetyPolicy.RequireDestructive(args.Bool("confirm"), DescribeEffect(tool));
            else if (tool.DestructiveWhen != null && tool.DestructiveWhen(args))
                SafetyPolicy.RequireDestructive(args.Bool("confirm"),
                    $"replace or delete existing content ({tool.Name} with {tool.DestructiveWhenText})");

            foreach (var p in tool.PathParams)
                _options.CheckPath(args.Str(p), p);
        }

        private static string DescribeEffect(ToolDef tool) =>
            $"run '{tool.Name}' ({tool.Title}), which deletes, replaces or discards existing content";

        // ---- audit trail -------------------------------------------------------

        private void Audit(ToolDef tool, JObject args, string error, long ms)
        {
            if (string.IsNullOrEmpty(_options.AuditLogPath)) return;
            try
            {
                var entry = new JObject
                {
                    ["time"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
                    ["tool"] = tool.Name,
                    ["safety"] = tool.Safety.ToString(),
                    ["ok"] = error == null,
                    ["ms"] = ms,
                    ["args"] = Abbreviate(args)
                };
                if (error != null) entry["error"] = error;

                var dir = Path.GetDirectoryName(Path.GetFullPath(_options.AuditLogPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_options.AuditLogPath, entry.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not write the audit log: {ex.Message}");
            }
        }

        /// <summary>Copy of the arguments with long strings (source code, XML) cut down for logging.</summary>
        private static JObject Abbreviate(JObject args)
        {
            const int max = 300;
            var copy = (JObject)args.DeepClone();
            foreach (var value in copy.Descendants().OfType<JValue>().ToList())
            {
                if (value.Type == JTokenType.String && ((string)value).Length > max)
                {
                    var s = (string)value;
                    value.Value = s.Substring(0, max) + $"... [{s.Length} chars]";
                }
            }
            return copy;
        }

        // ---- JSON helpers ------------------------------------------------------

        private static JObject ToolText(string text, bool isError) => new JObject
        {
            ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text } },
            ["isError"] = isError
        };

        private static JObject Result(JToken id, JToken result) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };

        private static JObject ErrorResponse(JToken id, int code, string message) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JObject { ["code"] = code, ["message"] = message }
        };
    }
}
