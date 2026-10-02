using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaMcpServer.Mcp
{
    /// <summary>How much of the tool surface a server instance exposes.</summary>
    internal enum AccessLevel
    {
        /// <summary>Only tools that never modify the project.</summary>
        ReadOnly = 0,
        /// <summary>Read + tools that create or modify, but nothing that deletes or overwrites.</summary>
        Write = 1,
        /// <summary>Everything, destructive tools included (each still needs confirm:true).</summary>
        Full = 2
    }

    /// <summary>
    /// Process-wide configuration, read once from the command line and environment.
    /// Command-line flags win over environment variables.
    /// </summary>
    internal sealed class ServerOptions
    {
        // Declared before Current: static initializers run in textual order and the
        // constructor reads this.
        public static readonly string[] AllGroups = { "core", "plc", "hmi", "hardware", "s7", "diag" };

        public static ServerOptions Current { get; private set; } = new ServerOptions();

        public AccessLevel Access { get; private set; } = AccessLevel.Full;
        public HashSet<string> Groups { get; } = new HashSet<string>(AllGroups, StringComparer.OrdinalIgnoreCase);
        public List<string> AllowedDirs { get; } = new List<string>();
        public string AuditLogPath { get; private set; }
        public string BackupDir { get; private set; }
        public bool Backups { get; private set; } = true;
        public bool Transactions { get; private set; } = true;

        /// <summary>False with --no-save: the server can edit but only the human saves, in TIA Portal.</summary>
        public bool AllowSave { get; private set; } = true;

        /// <summary>Extra hosts the live S7 tools may contact, besides those configured in the open project.</summary>
        public HashSet<string> S7Hosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True with --s7-any-host: the live S7 tools may contact any address.</summary>
        public bool S7AnyHost { get; private set; }

        /// <summary>Set when the process should print something and exit instead of serving MCP.</summary>
        public string OneShotCommand { get; private set; }
        public string OneShotArgument { get; private set; }

        public string DefaultBackupDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaMcpServer", "backups");

        public string EffectiveBackupDir => string.IsNullOrWhiteSpace(BackupDir) ? DefaultBackupDir : BackupDir;

        /// <summary>Scratch directory for inline content the server has to hand to TIA as a file.</summary>
        public string TempDir => Path.Combine(Path.GetTempPath(), "TiaMcpServer");

        public static ServerOptions Parse(string[] args)
        {
            var o = new ServerOptions();

            // Environment first, so flags can override.
            o.ApplyAccess(Environment.GetEnvironmentVariable("TIA_MCP_ACCESS"), "TIA_MCP_ACCESS");
            o.ApplyGroups(Environment.GetEnvironmentVariable("TIA_MCP_GROUPS"), "TIA_MCP_GROUPS");
            foreach (var d in Split(Environment.GetEnvironmentVariable("TIA_MCP_ALLOWED_DIRS"), ';'))
                o.AddAllowedDir(d);
            o.AuditLogPath = NullIfEmpty(Environment.GetEnvironmentVariable("TIA_MCP_AUDIT_LOG"));
            o.BackupDir = NullIfEmpty(Environment.GetEnvironmentVariable("TIA_MCP_BACKUP_DIR"));
            foreach (var h in Split(Environment.GetEnvironmentVariable("TIA_MCP_S7_HOSTS"), ';'))
                o.S7Hosts.Add(h);

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string Next()
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException($"Option '{a}' needs a value.");
                    return args[++i];
                }

                switch (a.ToLowerInvariant())
                {
                    case "--access": o.ApplyAccess(Next(), a); break;
                    case "--read-only":
                    case "--readonly": o.Access = AccessLevel.ReadOnly; break;
                    case "--groups": o.ApplyGroups(Next(), a); break;
                    case "--allowed-dir": o.AddAllowedDir(Next()); break;
                    case "--audit-log": o.AuditLogPath = Next(); break;
                    case "--backup-dir": o.BackupDir = Next(); break;
                    case "--no-backups": o.Backups = false; break;
                    case "--no-transactions": o.Transactions = false; break;
                    case "--no-save": o.AllowSave = false; break;
                    case "--s7-host": o.S7Hosts.Add(Next()); break;
                    case "--s7-any-host": o.S7AnyHost = true; break;
                    case "--doctor": o.OneShotCommand = "doctor"; break;
                    case "--dump-tools":
                        o.OneShotCommand = "dump-tools";
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) o.OneShotArgument = args[++i];
                        break;
                    case "--version": o.OneShotCommand = "version"; break;
                    case "--help":
                    case "-h":
                    case "/?": o.OneShotCommand = "help"; break;
                    default:
                        throw new ArgumentException($"Unknown option '{a}'. Run with --help for the list.");
                }
            }

            Current = o;
            return o;
        }

        private void ApplyAccess(string value, string source)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            switch (value.Trim().ToLowerInvariant())
            {
                case "readonly":
                case "read-only":
                case "read": Access = AccessLevel.ReadOnly; break;
                case "write": Access = AccessLevel.Write; break;
                case "full": Access = AccessLevel.Full; break;
                default:
                    throw new ArgumentException($"{source}: unknown access level '{value}'. Use readonly | write | full.");
            }
        }

        private void ApplyGroups(string value, string source)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Groups.Clear();
            Groups.Add("core"); // connect/state/project tools are always available
            foreach (var g in Split(value, ','))
            {
                if (g.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var all in AllGroups) Groups.Add(all);
                    continue;
                }
                if (!AllGroups.Contains(g, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException(
                        $"{source}: unknown tool group '{g}'. Valid groups: {string.Join(", ", AllGroups)}.");
                Groups.Add(g);
            }
        }

        private void AddAllowedDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            AllowedDirs.Add(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        /// <summary>
        /// Enforces the --allowed-dir sandbox on a file or directory argument. With no
        /// allowed directories configured every path is accepted.
        /// </summary>
        public void CheckPath(string path, string argName)
        {
            if (AllowedDirs.Count == 0 || string.IsNullOrWhiteSpace(path)) return;

            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) { throw new ArgumentException($"Argument '{argName}' is not a valid path: {ex.Message}"); }

            foreach (var dir in AllowedDirs)
            {
                if (full.Equals(dir, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            throw new UnauthorizedAccessException(
                $"Path '{full}' (argument '{argName}') is outside the directories this server is allowed to use: " +
                string.Join("; ", AllowedDirs) + ". Use a path inside one of them, or restart the server with another --allowed-dir.");
        }

        private static IEnumerable<string> Split(string value, char sep) =>
            (value ?? "").Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.Trim())
                         .Where(s => s.Length > 0);

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        public const string HelpText =
@"TiaMcpServer - MCP server for Siemens TIA Portal V20 (TIA Openness)

Normally started by an MCP client (Claude Desktop, Claude Code, ...) with no
arguments; it then speaks newline-delimited JSON-RPC on stdin/stdout.

Options
  --access <level>       readonly | write | full            (default: full)
                           readonly  only tools that never change the project
                           write     + create/modify tools, nothing destructive
                           full      + delete/overwrite tools (each needs confirm:true)
  --read-only            Same as --access readonly
  --groups <a,b,...>     Tool groups to expose: plc, hmi, hardware, s7, diag, all
                         (default: all; 'core' is always on)
  --allowed-dir <dir>    Restrict every file/directory argument to this folder.
                         Repeatable. Default: no restriction.
  --audit-log <file>     Append one JSON line per tool call to this file
  --backup-dir <dir>     Where automatic pre-delete/pre-overwrite backups go
                         (default: %LOCALAPPDATA%\TiaMcpServer\backups)
  --no-backups           Do not take automatic backups
  --no-transactions      Do not wrap edits in a TIA undo transaction
  --no-save              Refuse tia_save_project: only the human saves, in TIA Portal
  --s7-host <ip>         Also let the live S7 tools contact this address. Repeatable.
                         Default: only addresses configured in the open project.
  --s7-any-host          Let the live S7 tools contact any address
  --doctor               Check the installation (Openness, user group, TIA) and exit
  --dump-tools [fmt]     Print the tool reference (markdown | json) and exit
  --version              Print the version and exit
  --help                 Show this help

Environment variables (flags win): TIA_MCP_ACCESS, TIA_MCP_GROUPS,
  TIA_MCP_ALLOWED_DIRS (';'-separated), TIA_MCP_AUDIT_LOG, TIA_MCP_BACKUP_DIR,
  TIA_MCP_S7_HOSTS (';'-separated)
";
    }

    /// <summary>
    /// Thrown when a destructive operation was requested without confirm:true. The
    /// message is written for the model: it says what would be lost and what to do.
    /// </summary>
    internal sealed class ConfirmationRequiredException : Exception
    {
        public ConfirmationRequiredException(string message) : base(message) { }
    }

    /// <summary>The single gate every delete/overwrite goes through.</summary>
    internal static class SafetyPolicy
    {
        /// <summary>
        /// Call before deleting or overwriting existing project content.
        /// <paramref name="what"/> completes the sentence "This will ...".
        /// </summary>
        public static void RequireDestructive(bool confirmed, string what)
        {
            if (ServerOptions.Current.Access != AccessLevel.Full)
                throw new UnauthorizedAccessException(
                    $"Refused: this will {what}, and destructive operations are disabled on this server " +
                    $"(access level '{ServerOptions.Current.Access.ToString().ToLowerInvariant()}'). " +
                    "The user can enable them by starting the server with --access full.");

            if (!confirmed)
                throw new ConfirmationRequiredException(
                    $"Confirmation required: this will {what}. Nothing was changed. " +
                    "Tell the user exactly what will be deleted or overwritten, and once they agree " +
                    "call the tool again with confirm:true.");
        }
    }
}
