using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Hmi.Screen;

namespace TiaMcpServer
{
    // Classic WinCC panels (Comfort / Basic / Advanced): screens as SimaticML XML.
    // Classic screens have no object-level API in Openness, only export / import.
    internal sealed partial class TiaService
    {
        private static IEnumerable<Screen> AllScreens(ScreenFolder folder)
        {
            foreach (Screen s in folder.Screens) yield return s;
            foreach (ScreenFolder sub in folder.Folders)
                foreach (var s in AllScreens(sub))
                    yield return s;
        }

        public object ExportScreen(string deviceName, string screenName, string outDir)
        {
            var hmi = RequireHmi(deviceName);
            var screen = AllScreens(hmi.ScreenFolder)
                .FirstOrDefault(s => s.Name.Equals(screenName, StringComparison.OrdinalIgnoreCase));
            if (screen == null)
                throw new InvalidOperationException($"Screen '{screenName}' not found on '{hmi.Name}'.");

            Directory.CreateDirectory(outDir);
            var file = new FileInfo(Path.Combine(outDir, SafeFileName(screen.Name) + ".xml"));
            if (file.Exists) file.Delete();

            screen.Export(file, ExportOptions.WithDefaults);
            return new { exported = true, screen = screen.Name, path = file.FullName };
        }

        public object ImportScreen(string deviceName, string xmlPath, bool overwrite)
        {
            var hmi = RequireHmi(deviceName);
            if (!File.Exists(xmlPath))
                throw new FileNotFoundException($"XML file not found: {xmlPath}");

            var option = overwrite ? ImportOptions.Override : ImportOptions.None;
            var imported = hmi.ScreenFolder.Screens.Import(new FileInfo(xmlPath), option);
            return new
            {
                imported = imported.Count,
                screens = imported.Select(s => s.Name).ToArray(),
                device = hmi.Name
            };
        }
    }
}
