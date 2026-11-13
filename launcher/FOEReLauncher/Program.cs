using System;
using System.IO;
using System.Windows.Forms;

namespace FOEReLauncher
{
    internal static class Program
    {
        /// <summary>
        /// Arguments: --base-url URL (download site for this run, e.g. http://127.0.0.1:8765/site/);
        /// --game-settings FILE (the game's settings.json to use instead of the one in LocalLow; tests, not in the UI);
        /// --write-version FILE (writes the launcher version into FILE and exits; used by the self-update test).
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            string? baseUrl = null;
            string? gameSettings = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--base-url" && i + 1 < args.Length)
                {
                    baseUrl = args[++i];
                }
                else if (args[i] == "--game-settings" && i + 1 < args.Length)
                {
                    gameSettings = args[++i];
                }
                else if (args[i] == "--write-version" && i + 1 < args.Length)
                {
                    File.WriteAllText(args[i + 1], LauncherService.CurrentLauncherVersion);
                    return 0;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(new LauncherService(null, baseUrl, null, gameSettings), args));
            return 0;
        }
    }
}
