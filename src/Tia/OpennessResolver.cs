using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace TiaMcpServer
{
    /// <summary>
    /// Resolves Siemens.Engineering* assemblies from the installed TIA Portal at
    /// runtime. The DLLs are version-locked to the installed TIA Portal, so we never
    /// ship them; instead we look them up in the registry (with a path fallback).
    ///
    /// IMPORTANT: Register() must run before any Siemens.Engineering type is touched
    /// by the JIT. Program.Main calls it before anything else that could.
    /// </summary>
    internal static class OpennessResolver
    {
        private static string _dllDir;
        private static bool _registered;

        /// <summary>Directory the Openness DLLs are loaded from (null before Register()).</summary>
        public static string DllDirectory => _dllDir;

        /// <summary>
        /// TIA Portal major version this build targets (20 for V20). Taken from the
        /// Siemens.Engineering reference the server was compiled against, so building
        /// against another version's DLL retargets the lookup automatically.
        /// </summary>
        public static int TiaMajorVersion { get; } =
            Assembly.GetExecutingAssembly().GetReferencedAssemblies()
                .FirstOrDefault(a => a.Name == "Siemens.Engineering")?.Version.Major ?? 20;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            _dllDir = ResolveDllDirectory();
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
            Log.Info($"Openness DLL directory (TIA Portal V{TiaMajorVersion}): {_dllDir}");
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            var requested = new AssemblyName(args.Name).Name;
            if (!requested.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
                return null;

            var candidate = Path.Combine(_dllDir, requested + ".dll");
            if (File.Exists(candidate))
            {
                Log.Info($"Resolving {requested} -> {candidate}");
                return Assembly.LoadFrom(candidate);
            }

            // Internal Siemens assemblies (Contract, ClientAdapter...) are not in the public
            // API folder; returning null hands them back to TIA's own resolver.
            return null;
        }

        private static string ResolveDllDirectory()
        {
            var version = TiaMajorVersion + ".0";

            // Registry: HKLM\SOFTWARE\Siemens\Automation\Openness\20.0\PublicAPI\20.0.0.0
            // value "Siemens.Engineering" = full path to Siemens.Engineering.dll
            try
            {
                using (var key = RegistryKey
                           .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                           .OpenSubKey($@"SOFTWARE\Siemens\Automation\Openness\{version}\PublicAPI\{version}.0.0"))
                {
                    var path = key?.GetValue("Siemens.Engineering") as string;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        return Path.GetDirectoryName(path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Registry lookup for Openness path failed: {ex.Message}");
            }

            // Fallback: the default installation folder.
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Siemens", "Automation", $"Portal V{TiaMajorVersion}", "PublicAPI", $"V{TiaMajorVersion}");
        }
    }
}
