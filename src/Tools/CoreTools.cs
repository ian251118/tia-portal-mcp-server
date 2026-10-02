using TiaMcpServer.Mcp;

namespace TiaMcpServer.Tools
{
    /// <summary>Connection, project lifecycle, devices and compile. Always enabled.</summary>
    internal static class CoreTools
    {
        public const string Group = "core";

        public static void Register(ToolRegistry r, TiaService tia)
        {
            r.Add("tia_connect", Group, Safety.ReadOnly, "Connect to TIA Portal",
                    "Connect this server to TIA Portal. Call this first. 'attach' (default) joins the TIA Portal the user " +
                    "already has open; 'new_ui' / 'new_headless' start a fresh instance with / without a window. " +
                    "Changes nothing in the project. The first connection after the server is (re)built makes TIA Portal " +
                    "show an Openness access prompt that the user must accept.")
                .Str("mode", "attach | new_ui | new_headless (default attach)")
                .Path("projectPath", "attach only: prefer the running instance that has this .apNN project open")
                .Run(a => tia.Connect(a.Str("mode"), a.Str("projectPath")));

            r.Add("tia_state", Group, Safety.ReadOnly, "Connection state",
                    "Connection state, the open project, whether it has unsaved changes, and how this server is " +
                    "configured (access level, tool groups, backups).")
                .Run(a => tia.State());

            r.Add("tia_doctor", Group, Safety.ReadOnly, "Check the environment",
                    "Checks the prerequisites for talking to TIA Portal: Openness installed, the Windows user in the " +
                    "'Siemens TIA Openness' group, running TIA instances, connection and project. Works before " +
                    "tia_connect; use it when connecting fails.")
                .Run(a => tia.Doctor());

            r.Add("tia_open_project", Group, Safety.ReadOnly, "Open a project",
                    "Open a TIA Portal project file in the connected instance. Does not modify the project. TIA holds " +
                    "one project at a time: prefer tia_connect (attach) when the user already has it open.")
                .Path("path", "Absolute path to the project file (.ap20)", required: true)
                .Run(a => tia.OpenProject(a.ReqStr("path")));

            r.Add("tia_save_project", Group, Safety.Write, "Save the project",
                    "Save the open project to disk. This is the point of no return: until it is called, every change " +
                    "made through this server lives only in TIA Portal's memory and can be undone or discarded by " +
                    "closing without saving. Only save when the user asks for it. Refused when the server runs " +
                    "with --no-save.")
                .Idempotent()
                .Run(a => tia.SaveProject());

            r.Add("tia_close_project", Group, Safety.Destructive, "Close the project",
                    "Close the open project. Unsaved changes are LOST unless save:true. Closes it in the user's TIA " +
                    "Portal window too when attached.")
                .Bool("save", "Save before closing (default false: unsaved changes are discarded)")
                .Run(a => tia.CloseProject(a.Bool("save")));

            r.Add("tia_project_info", Group, Safety.ReadOnly, "Project information",
                    "Project metadata: path, author, TIA version, last modification, unsaved-changes flag, project " +
                    "languages (needed for multilingual texts), installed products it uses, and device names.")
                .Run(a => tia.ProjectInfo());

            r.Add("tia_archive_project", Group, Safety.ReadOnly, "Archive the project (backup)",
                    "Write a compressed archive (.zapNN) of the project to disk as a restore point. Does not modify " +
                    "the project. Take it BEFORE large or destructive changes: TIA refuses to archive a project " +
                    "that has unsaved changes. Restore in TIA Portal with Project > Retrieve.")
                .Path("targetDir", "Directory for the archive; must be outside the project folder (default: the server's backup folder)")
                .Str("name", "Archive file name without extension (default: <project>_<timestamp>)")
                .Bool("compressed", "Compress the archive (default true)")
                .Run(a => tia.ArchiveProject(a.Str("targetDir"), a.Str("name"), a.Bool("compressed", true)));

            r.Add("tia_list_devices", Group, Safety.ReadOnly, "List devices",
                    "All devices in the project and the software each one holds: 'plc', 'hmiUnified' (WinCC Unified) " +
                    "or 'hmiClassic' (Comfort/Basic). The software name is what the 'device' argument of the other " +
                    "tools expects.")
                .Run(a => tia.ListDevices());

            r.Add("tia_compile", Group, Safety.Write, "Compile",
                    "Compile a PLC, an HMI or a single block and return the compiler messages. Changes the compile " +
                    "state of the project, not its logic. Always read the messages: a successful import does not mean " +
                    "the code is correct. Nothing is downloaded to the PLC.")
                .Str("device", "PLC or HMI name (default: first PLC)")
                .Str("block", "Compile only this block or PLC data type")
                .Str("scope", "For a PLC: software (default) | hardware | all")
                .Idempotent()
                .Run(a => tia.Compile(a.Str("device"), a.Str("block"), a.Str("scope")));
        }
    }
}
