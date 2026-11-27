using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using vatsys;
using vatsys.Plugin;

namespace AliasPlugin
{
    /// <summary>
    /// vatSys alias plugin: type ".alias args" in a message box and press Enter to expand it
    /// into the full text (CRC/VRC alias file format).
    /// </summary>
    [Export(typeof(IPlugin))]
    public class AliasPlugin : IPlugin, IMessageFilter
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_TAB = 0x09;
        private const int VK_RETURN = 0x0D;
        private const int VK_LEFT = 0x25;
        private const int VK_RIGHT = 0x27;

        public string Name => "Alias Expander";

        private readonly string pluginDir;
        private Config cfg;
        private Variables vars;
        private volatile Dictionary<string, string> aliases = new Dictionary<string, string>();
        private ToolStripMenuItem statusItem;
        private CallsignBoxManager callsigns;

        public AliasPlugin()
        {
            pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Reload();
            // vatSys loads plugins on the UI thread, so this filter sees the main UI message loop.
            Application.AddMessageFilter(this);
            AddMenu();
            callsigns = new CallsignBoxManager(() => cfg) { Log = Log };
        }

        // ------------------------------------------------------------------ loading

        private void Reload()
        {
            var newCfg = Config.Load(pluginDir);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var loaded = new List<string>();
            foreach (var f in newCfg.ResolveFiles())
            {
                try
                {
                    int n = AliasFile.LoadInto(f, dict);
                    loaded.Add($"{Path.GetFileName(f)} ({n})");
                }
                catch (Exception ex)
                {
                    try { Errors.Add(new Exception("Failed to read alias file: " + f, ex), Name); } catch { }
                }
            }
            cfg = newCfg;
            vars = new Variables(newCfg);
            aliases = dict;
            Log($"Loaded {dict.Count} aliases from: {string.Join(", ", loaded)}; ChatWindowOnly={newCfg.ChatWindowOnly}");

            if (statusItem != null)
                statusItem.Text = $"{dict.Count} aliases loaded";
        }

        private void AddMenu()
        {
            try
            {
                var root = new ToolStripMenuItem("Aliases");
                statusItem = new ToolStripMenuItem($"{aliases.Count} aliases loaded") { Enabled = false };
                var reload = new ToolStripMenuItem("Reload Aliases");
                reload.Click += (s, e) => Reload();
                var open = new ToolStripMenuItem("Open Plugin Folder");
                open.Click += (s, e) =>
                {
                    try { Process.Start("explorer.exe", "\"" + pluginDir + "\""); } catch { }
                };
                root.DropDownItems.Add(statusItem);
                root.DropDownItems.Add(reload);
                root.DropDownItems.Add(open);
                MMI.AddCustomMenuItem(new CustomToolStripMenuItem(
                    CustomToolStripMenuItemWindowType.Main, CustomToolStripMenuItemCategory.Tools, root));
            }
            catch
            {
                // A menu failure must not break the main feature.
            }
        }

        // ------------------------------------------------------------------ keyboard

        /// <summary>The text box currently in "fill in the variables" mode (unfilled $ placeholders remain).</summary>
        private WeakReference<TextBoxBase> session;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_KEYDOWN)
                return false;
            int key = (int)m.WParam;
            if (key != VK_RETURN && key != VK_TAB && key != VK_LEFT && key != VK_RIGHT)
                return false;

            var mods = Control.ModifierKeys;
            if ((mods & (Keys.Control | Keys.Alt)) != 0)
                return false;
            bool shift = (mods & Keys.Shift) != 0;

            try
            {
                var ctl = Control.FromHandle(m.HWnd);
                if (!(ctl is TextBoxBase tb) || tb.ReadOnly || !tb.Enabled)
                {
                    if (key == VK_RETURN)
                        Log($"Enter ignored: focused control {ctl?.GetType().FullName ?? "(not a WinForms control)"} is not an editable text box");
                    return false;
                }

                // The callsign box itself: Enter / Tab jump back to the message box next to it.
                if (callsigns != null && callsigns.IsCallsignBox(tb))
                {
                    if ((key == VK_RETURN || key == VK_TAB) && !shift)
                    {
                        FocusSiblingMessageBox(tb);
                        return true;
                    }
                    return false;
                }

                bool inSession = session != null && session.TryGetTarget(out var s) && ReferenceEquals(s, tb);
                bool managed = callsigns != null && callsigns.IsManaged(tb);

                // ---------- Left / Right: jump between variables ----------
                if (key == VK_LEFT || key == VK_RIGHT)
                {
                    if (!inSession || shift || !IsPlaceholderSelected(tb))
                        return false; // normal caret movement
                    if (key == VK_RIGHT) SelectNext(tb, wrap: false);
                    else SelectPrev(tb, wrap: false);
                    return true;
                }

                // ---------- Tab: variable navigation / callsign entry ----------
                if (key == VK_TAB)
                {
                    if (!inSession)
                    {
                        // Callsign typed in the message box + Tab -> move it into the callsign box.
                        if (managed && !shift && MoveCallsignToBox(tb)) return true;
                        return false; // normal Tab
                    }
                    bool found = shift ? SelectPrev(tb, wrap: true) : SelectNext(tb, wrap: true);
                    if (!found)
                    {
                        // All filled in: put the caret at the end and keep focus here.
                        session = null;
                        tb.SelectionStart = tb.TextLength;
                        tb.SelectionLength = 0;
                    }
                    return true;
                }

                // ---------- Enter: expand aliases; otherwise let vatSys send ----------
                if (shift) return false; // leave Shift+Enter to vatSys

                Form form = tb.FindForm();
                string formType = form?.GetType().Name ?? "";
                if (cfg.ChatWindowOnly && !MessageForms.Contains(formType))
                {
                    Log($"Enter ignored: window {formType} is not a message window (ChatWindowOnly=true)");
                    return false;
                }

                string boxCallsign = managed ? callsigns.GetCallsign(tb) : null;
                string target = boxCallsign ?? GetTargetCallsign(form);
                string before = tb.Text;
                var res = TryExpand(tb, target, out var unknownAlias);
                Log($"Enter: window={formType} control={tb.GetType().Name} target={target ?? "-"} text=\"{before}\" result={res}");
                if (res == ExpandResult.Unknown)
                {
                    // Unknown alias: report it and do NOT send.
                    ReportUnknownAlias(tb, unknownAlias);
                    return true;
                }
                if (res == ExpandResult.Expanded)
                {
                    // Select the first unfilled variable, swallow this Enter (do not send).
                    session = SelectNext(tb, wrap: false, from: 0) ? new WeakReference<TextBoxBase>(tb) : null;
                    return true;
                }

                session = null;

                // Plain text -> prefix the callsign from the callsign box, then let vatSys send it.
                if (boxCallsign != null)
                {
                    string body = tb.Text.Trim();
                    if (body.Length > 0 && !StartsWithCallsign(body, boxCallsign))
                    {
                        tb.Text = cfg.CallsignFormat.Replace("{cs}", boxCallsign).Replace("{msg}", body);
                        tb.SelectionStart = tb.TextLength;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                try { Errors.Add(new Exception("Alias expansion error: " + ex.Message, ex), Name); } catch { }
                return false;
            }
        }

        // ------------------------------------------------------------------ callsign box

        private static bool StartsWithCallsign(string text, string cs)
        {
            if (!text.StartsWith(cs, StringComparison.OrdinalIgnoreCase)) return false;
            return text.Length == cs.Length || !char.IsLetterOrDigit(text[cs.Length]);
        }

        /// <summary>Moves the first word of the message box into the callsign box; the rest stays.</summary>
        private bool MoveCallsignToBox(TextBoxBase tb)
        {
            string text = tb.Text.Trim();
            if (text.Length == 0 || text[0] == '.') return false;
            int sp = AliasFile.IndexOfWhitespace(text, 0);
            string cs = sp < 0 ? text : text.Substring(0, sp);
            string rest = sp < 0 ? "" : text.Substring(sp).TrimStart();
            if (cs.Length < 2 || cs.Length > 10 || !cs.All(char.IsLetterOrDigit)) return false;

            callsigns.SetCallsign(tb, cs);
            tb.Text = rest;
            tb.SelectionStart = tb.TextLength;
            tb.SelectionLength = 0;
            Log($"Callsign box <- {cs.ToUpperInvariant()} (manual)");
            return true;
        }

        private void FocusSiblingMessageBox(TextBoxBase box)
        {
            var parent = box.Parent;
            if (parent == null) return;
            foreach (Control c in parent.Controls)
                if (c is TextBoxBase t && callsigns.IsManaged(t))
                {
                    t.Focus();
                    t.SelectionStart = t.TextLength;
                    return;
                }
        }

        // ------------------------------------------------------------------ variable navigation

        private static bool IsPlaceholderSelected(TextBoxBase tb)
        {
            if (tb.SelectionLength == 0) return false;
            var m = Variables.PlaceholderRx.Match(tb.SelectedText);
            return m.Success && m.Index == 0 && m.Length == tb.SelectionLength;
        }

        /// <summary>Selects the next $variable after the caret (end of selection).</summary>
        private static bool SelectNext(TextBoxBase tb, bool wrap, int? from = null)
        {
            var matches = Variables.PlaceholderRx.Matches(tb.Text);
            if (matches.Count == 0) return false;
            int pos = from ?? (tb.SelectionStart + tb.SelectionLength);
            foreach (Match m in matches)
            {
                if (m.Index >= pos)
                {
                    tb.Select(m.Index, m.Length);
                    return true;
                }
            }
            if (!wrap) return IsPlaceholderSelected(tb); // already on the last one: stay
            tb.Select(matches[0].Index, matches[0].Length);
            return true;
        }

        /// <summary>Selects the previous $variable before the caret (start of selection).</summary>
        private static bool SelectPrev(TextBoxBase tb, bool wrap)
        {
            var matches = Variables.PlaceholderRx.Matches(tb.Text);
            if (matches.Count == 0) return false;
            int pos = tb.SelectionStart;
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var m = matches[i];
                if (m.Index + m.Length <= pos)
                {
                    tb.Select(m.Index, m.Length);
                    return true;
                }
            }
            if (!wrap) return IsPlaceholderSelected(tb);
            var last = matches[matches.Count - 1];
            tb.Select(last.Index, last.Length);
            return true;
        }

        // ------------------------------------------------------------------ target aircraft

        /// <summary>vatSys windows that send text to pilots / controllers.</summary>
        private static readonly HashSet<string> MessageForms = new HashSet<string>
        {
            "ChatWindow",           // private / ATC / system chat
            "MessageEditorWindow",  // Message Editor (freetext to an aircraft)
            "VSCSWindow",           // frequency text box in the VSCS panel
            "CurrentMessageWindow",
            "MainForm",
        };

        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>
        /// Private chat -> the other party's callsign; Message Editor -> the aircraft being edited;
        /// anything else -> null (falls back to the selected track).
        /// </summary>
        private static string GetTargetCallsign(Form form)
        {
            if (form == null) return null;
            var t = form.GetType();
            try
            {
                switch (t.Name)
                {
                    case "ChatWindow":
                        {
                            var typeField = t.GetField("WindowType", AnyInstance);
                            if (typeField != null && typeField.GetValue(form)?.ToString() != "Text")
                                return null;
                            var r = t.GetField("Recipient", AnyInstance)?.GetValue(form) as string;
                            return string.IsNullOrWhiteSpace(r) ? null : r;
                        }
                    case "MessageEditorWindow":
                        {
                            if (t.GetField("_fdr", AnyInstance)?.GetValue(form) is FDP2.FDR fdr && !string.IsNullOrEmpty(fdr.Callsign))
                                return fdr.Callsign;
                            var rm = t.GetField("_rm", AnyInstance)?.GetValue(form);
                            var addr = rm?.GetType().GetField("Address", AnyInstance)?.GetValue(rm) as string
                                       ?? rm?.GetType().GetProperty("Address", AnyInstance)?.GetValue(rm) as string;
                            return string.IsNullOrWhiteSpace(addr) ? null : addr;
                        }
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------------ debug log

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "vatSys Files", "AliasPlugin_log.txt");

        private void Log(string msg)
        {
            if (cfg == null || !cfg.Debug) return;
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + msg + Environment.NewLine); } catch { }
        }

        // ------------------------------------------------------------------ expansion

        private enum ExpandResult { None, Expanded, Unknown }

        /// <summary>An alias word: at the start or after whitespace, '.' followed by a letter/digit, up to the next space.</summary>
        private static readonly Regex AliasTokenRx = new Regex(@"(?<!\S)\.[A-Za-z0-9]\S*", RegexOptions.Compiled);

        /// <summary>
        /// Replaces every ".alias" in the message with its full text.
        ///  - Message starts with ".alias" and contains no other alias: the following words become $1 $2 ...
        ///  - Aliases elsewhere in the message: expanded without arguments (fill variables with Tab).
        ///  - Any alias that does not exist: text is left unchanged, the alias is selected, returns Unknown.
        /// </summary>
        private ExpandResult TryExpand(TextBoxBase tb, string target, out string unknown)
        {
            unknown = null;
            var dict = aliases;
            string text = tb.Text;

            var tokens = AliasTokenRx.Matches(text).Cast<Match>().ToList();
            if (tokens.Count == 0) return ExpandResult.None;

            // Validate all of them first.
            foreach (var t in tokens)
            {
                if (!dict.ContainsKey(t.Value))
                {
                    unknown = t.Value;
                    tb.Select(t.Index, t.Length);
                    return ExpandResult.Unknown;
                }
            }

            string result;
            if (tokens.Count == 1 && tokens[0].Index == 0)
            {
                // ".alias arg1 arg2"
                var t = tokens[0];
                string[] args = text.Substring(t.Length).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                result = vars.Expand(dict[t.Value], args, target);
            }
            else
            {
                // Aliases inside a sentence: replace back-to-front so indices stay valid.
                result = text;
                for (int i = tokens.Count - 1; i >= 0; i--)
                {
                    var t = tokens[i];
                    string exp = vars.Expand(dict[t.Value], new string[0], target);
                    result = result.Substring(0, t.Index) + exp + result.Substring(t.Index + t.Length);
                }
            }

            tb.Text = result;
            tb.SelectionStart = result.Length;
            tb.SelectionLength = 0;
            return ExpandResult.Expanded;
        }

        private void ReportUnknownAlias(TextBoxBase tb, string name)
        {
            Log($"Unknown alias: {name} - message not sent");
            System.Media.SystemSounds.Beep.Play();
            if (tb is TextField tf)
            {
                tf.ErrorMode(true);
                EventHandler reset = null;
                reset = (s, e) => { tf.TextChanged -= reset; try { tf.ErrorMode(false); } catch { } };
                tf.TextChanged += reset;
            }
            try { Errors.Add(new Exception($"Unknown alias: {name} (message not sent)"), Name); } catch { }
        }

        // ------------------------------------------------------------------ IPlugin

        public void OnFDRUpdate(FDP2.FDR updated) { }

        public void OnRadarTrackUpdate(RDP.RadarTrack updated) { }
    }
}
