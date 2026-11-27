using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AliasPlugin
{
    /// <summary>
    /// Config file AliasPlugin.ini (next to the plugin DLL):
    ///   AliasFile=C:\path\xxx.txt     a specific file (repeatable, any file name)
    ///   AliasFolder=D:\MyAliases      Alias.txt in that folder (case-insensitive, repeatable)
    ///   TransitionAltitude=11000
    ///   ChatWindowOnly=false
    ///   Debug=false
    /// By default (no AliasFile / AliasFolder) only Alias.txt next to the DLL is read (case-insensitive), so other plugins' txt files are never picked up.
    /// </summary>
    internal class Config
    {
        public const string DefaultFileName = "alias.txt";

        public List<string> Files = new List<string>();
        public List<string> Folders = new List<string>();
        public int TransitionAltitude = 11000;
        public bool ChatWindowOnly = false;
        public bool Debug = false;

        /// <summary>Whether to add a callsign box to the left of message boxes.</summary>
        public bool CallsignBox = true;
        /// <summary>Which windows get a callsign box (window type names).</summary>
        public HashSet<string> CallsignBoxWindows = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "VSCSWindow", "ChatWindow" };
        /// <summary>Send format: {cs} = callsign, {msg} = message.</summary>
        public string CallsignFormat = "{cs}, {msg}";

        public string Path { get; private set; }

        public static Config Load(string pluginDir)
        {
            var cfg = new Config { Path = System.IO.Path.Combine(pluginDir, "AliasPlugin.ini") };
            if (File.Exists(cfg.Path))
            {
                foreach (var raw in File.ReadAllLines(cfg.Path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                        continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    var val = Environment.ExpandEnvironmentVariables(line.Substring(eq + 1).Trim().Trim('"'));
                    switch (key)
                    {
                        case "aliasfile": if (val != "") cfg.Files.Add(val); break;
                        case "aliasfolder": if (val != "") cfg.Folders.Add(val); break;
                        case "transitionaltitude": int.TryParse(val, out cfg.TransitionAltitude); break;
                        case "chatwindowonly": cfg.ChatWindowOnly = IsTrue(val); break;
                        case "debug": cfg.Debug = IsTrue(val); break;
                        case "callsignbox": cfg.CallsignBox = IsTrue(val); break;
                        case "callsignboxwindows":
                            cfg.CallsignBoxWindows = new HashSet<string>(
                                val.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries),
                                StringComparer.OrdinalIgnoreCase);
                            break;
                        case "callsignformat":
                            // Only accept formats that contain the message placeholder.
                            if (val.Contains("{msg}")) cfg.CallsignFormat = val;
                            break;
                    }
                }
            }

            if (cfg.Files.Count == 0 && cfg.Folders.Count == 0)
                cfg.Folders.Add(pluginDir);

            return cfg;
        }

        private static bool IsTrue(string v) =>
            v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase);

        public IEnumerable<string> ResolveFiles()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in Folders)
            {
                if (!Directory.Exists(d)) continue;
                foreach (var f in Directory.GetFiles(d, "*.txt")
                             .Where(x => string.Equals(System.IO.Path.GetFileName(x), DefaultFileName, StringComparison.OrdinalIgnoreCase)))
                    if (seen.Add(System.IO.Path.GetFullPath(f)))
                        yield return f;
            }
            foreach (var f in Files)
                if (File.Exists(f) && seen.Add(System.IO.Path.GetFullPath(f)))
                    yield return f;
        }
    }
}
