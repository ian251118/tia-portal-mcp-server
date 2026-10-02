using System;

namespace TiaMcpServer
{
    /// <summary>
    /// All diagnostics go to STDERR. STDOUT is reserved exclusively for MCP
    /// JSON-RPC messages; writing anything else there corrupts the protocol.
    /// </summary>
    internal static class Log
    {
        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg) => Write("ERROR", msg);

        private static void Write(string level, string msg)
        {
            try
            {
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}");
                Console.Error.Flush();
            }
            catch
            {
                /* never let logging crash the server */
            }
        }
    }
}
