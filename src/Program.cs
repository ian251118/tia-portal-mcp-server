using System;
using System.Text;
using TiaMcpServer.Mcp;
using TiaMcpServer.Tools;

namespace TiaMcpServer
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            ServerOptions options;
            try
            {
                options = ServerOptions.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }

            switch (options.OneShotCommand)
            {
                case "help":
                    Console.Out.Write(ServerOptions.HelpText);
                    return 0;
                case "version":
                    Console.Out.WriteLine($"{McpServer.ServerName} {McpServer.ServerVersion}");
                    return 0;
            }

            // Must run before any Siemens.Engineering type is JIT-loaded.
            OpennessResolver.Register();

            try
            {
                return Serve(options);
            }
            catch (Exception ex) when (ex is System.IO.FileNotFoundException || ex is TypeLoadException)
            {
                Console.Error.WriteLine(
                    $"TIA Portal Openness (V{OpennessResolver.TiaMajorVersion}) could not be loaded: {ex.Message}\n" +
                    $"Looked in: {OpennessResolver.DllDirectory}\n" +
                    "Install TIA Portal with the Openness option, or rebuild this server against your TIA version " +
                    "(see README, 'Other TIA Portal versions').");
                return 3;
            }
        }

        // Kept out of Main (and never inlined) so that no Siemens.Engineering type is
        // touched before OpennessResolver.Register() has installed the assembly resolver.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int Serve(ServerOptions options)
        {
            using (var tia = new TiaService())
            {
                var registry = BuildRegistry(tia);

                if (options.OneShotCommand == "doctor")
                {
                    Console.Out.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(
                        tia.Doctor(), Newtonsoft.Json.Formatting.Indented));
                    return 0;
                }

                if (options.OneShotCommand == "dump-tools")
                {
                    Console.OutputEncoding = new UTF8Encoding(false);
                    var json = string.Equals(options.OneShotArgument, "json", StringComparison.OrdinalIgnoreCase);
                    Console.Out.Write(json ? ToolDocs.Json(registry) : ToolDocs.Markdown(registry));
                    return 0;
                }

                new McpServer(registry, options).Run();
                return 0;
            }
        }

        /// <summary>
        /// The complete tool catalogue. To add a tool: write the method in the matching
        /// TiaService.*.cs file and register it in the matching Tools/*.cs file, giving
        /// it the right Safety class - that single value drives listing, gating,
        /// annotations and the generated documentation.
        /// </summary>
        internal static ToolRegistry BuildRegistry(TiaService tia)
        {
            var registry = new ToolRegistry();
            CoreTools.Register(registry, tia);
            PlcTools.Register(registry, tia);
            HmiTools.Register(registry, tia);
            HardwareTools.Register(registry, tia);
            DiagTools.Register(registry, tia);
            return registry;
        }
    }
}
