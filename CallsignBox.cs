using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace AliasPlugin
{
    /// <summary>
    /// Inserts a "callsign box" to the left of vatSys message text boxes:
    ///  - automatically filled with the aircraft currently selected in vatSys;
    ///  - with nothing selected, type a callsign in the message box and press Tab to put it there (a later selection overrides it);
    ///  - the callsign is prefixed to every message sent.
    /// </summary>
    internal class CallsignBoxManager
    {
        /// <summary>Control names of vatSys message text boxes: ChatWindow.messageBox and the VSCS textMessageBox.</summary>
        private static readonly HashSet<string> MessageBoxNames = new HashSet<string> { "messageBox", "textMessageBox" };

        private readonly Func<Config> cfg;
        private readonly Dictionary<TextBoxBase, Binding> bindings = new Dictionary<TextBoxBase, Binding>();
        private readonly HashSet<TextBoxBase> callsignBoxes = new HashSet<TextBoxBase>();
        private readonly Timer scanTimer;
        private string lastSelected;

        public CallsignBoxManager(Func<Config> cfg)
        {
            this.cfg = cfg;
            scanTimer = new Timer { Interval = 500 };
            scanTimer.Tick += (s, e) => Scan();
            scanTimer.Start();
            MMI.SelectedTrackChanged += OnSelectedTrackChanged;
        }

        public bool IsCallsignBox(TextBoxBase tb) => callsignBoxes.Contains(tb);

        public bool IsManaged(TextBoxBase tb) => bindings.ContainsKey(tb);

        /// <summary>The callsign in the callsign box attached to this message box (null if empty).</summary>
        public string GetCallsign(TextBoxBase msg)
        {
            if (bindings.TryGetValue(msg, out var b))
            {
                var cs = b.Box.Text.Trim();
                return cs.Length == 0 ? null : cs;
            }
            return null;
        }

        public void SetCallsign(TextBoxBase msg, string callsign)
        {
            if (bindings.TryGetValue(msg, out var b))
                b.Box.Text = (callsign ?? "").Trim().ToUpperInvariant();
        }

        // ------------------------------------------------------------ selected aircraft

        private void OnSelectedTrackChanged(object sender, EventArgs e)
        {
            // The event may be raised off the UI thread.
            var any = bindings.Values.FirstOrDefault()?.Box;
            if (any != null && any.InvokeRequired)
            {
                try { any.BeginInvoke((MethodInvoker)ApplySelection); } catch { }
                return;
            }
            ApplySelection();
        }

        private static string SelectedCallsign()
        {
            try
            {
                var t = MMI.SelectedTrack;
                if (t == null) return null;
                var cs = t.GetFDR()?.Callsign;
                if (string.IsNullOrWhiteSpace(cs)) cs = t.GetPilot()?.Callsign;
                return string.IsNullOrWhiteSpace(cs) ? null : cs.Trim().ToUpperInvariant();
            }
            catch { return null; }
        }

        private void ApplySelection()
        {
            string cs = SelectedCallsign();
            if (cs == lastSelected) return;
            foreach (var b in bindings.Values)
            {
                if (cs != null)
                    b.Box.Text = cs; // a selected aircraft overrides manual input
                else if (string.Equals(b.Box.Text, lastSelected, StringComparison.OrdinalIgnoreCase))
                    b.Box.Text = ""; // deselected: clear a callsign that came from the selection (keep manual input)
            }
            lastSelected = cs;
        }

        // ------------------------------------------------------------ finding message boxes

        public Action<string> Log = _ => { };
        private bool loggedScan;

        private void Scan()
        {
            var c = cfg();
            if (c == null || !c.CallsignBox) return;
            try
            {
                var forms = AllForms();
                if (!loggedScan)
                {
                    loggedScan = true;
                    Log("Callsign box: scanned windows: " + string.Join(", ", forms.Select(f => f.GetType().Name).Distinct()));
                }
                foreach (Form f in forms)
                {
                    if (f.IsDisposed || !c.CallsignBoxWindows.Contains(f.GetType().Name)) continue;
                    foreach (var tb in FindMessageBoxes(f).ToArray())
                        if (!bindings.ContainsKey(tb))
                        {
                            Attach(tb);
                            Log($"Callsign box attached to {f.GetType().Name}.{tb.Name}");
                        }
                }
                // Fallback in case the selection event did not fire.
                ApplySelection();
            }
            catch (Exception ex)
            {
                Log("Callsign box error: " + ex);
            }
        }

        // Application.OpenForms loses forms whose handle was recreated, so enumerate this thread's top-level windows directly.
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc cb, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private static List<Form> AllForms()
        {
            var list = new List<Form>();
            foreach (Form f in Application.OpenForms)
                if (!list.Contains(f)) list.Add(f);
            EnumThreadWindows(GetCurrentThreadId(), (h, l) =>
            {
                if (Control.FromHandle(h) is Form f && !list.Contains(f)) list.Add(f);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static IEnumerable<TextBoxBase> FindMessageBoxes(Control root)
        {
            foreach (Control ch in root.Controls)
            {
                if (ch is TextBoxBase tb && MessageBoxNames.Contains(ch.Name) && !tb.ReadOnly)
                    yield return tb;
                foreach (var x in FindMessageBoxes(ch))
                    yield return x;
            }
        }

        private void Attach(TextBoxBase msg)
        {
            if (msg.Parent == null) return;
            var box = new TextField
            {
                Name = "aliasCallsignBox",
                CharacterCasing = CharacterCasing.Upper,
                Font = msg.Font,
                MaxLength = 10,
                TabStop = false,
                NumericCharOnly = false,
            };
            var b = new Binding(msg, box);
            bindings[msg] = b;
            callsignBoxes.Add(box);
            box.Text = SelectedCallsign() ?? "";

            msg.Parent.Controls.Add(box);
            box.BringToFront();
            b.Relayout();

            msg.LocationChanged += (s, e) => b.Relayout();
            msg.SizeChanged += (s, e) => b.Relayout();
            msg.VisibleChanged += (s, e) => box.Visible = msg.Visible;
            msg.ParentChanged += (s, e) => { if (msg.Parent != null && box.Parent != msg.Parent) { msg.Parent.Controls.Add(box); b.Relayout(); } };
            msg.Disposed += (s, e) =>
            {
                bindings.Remove(msg);
                callsignBoxes.Remove(box);
                try { box.Dispose(); } catch { }
            };
            box.Visible = msg.Visible;
        }

        /// <summary>Shifts the message box right and narrows it to make room for the callsign box on the left.</summary>
        private class Binding
        {
            public readonly TextBoxBase Msg;
            public readonly TextField Box;
            private bool adjusting;
            private int setLeft = int.MinValue;
            private int baseLeft;

            private const int Gap = 2;

            public Binding(TextBoxBase msg, TextField box)
            {
                Msg = msg;
                Box = box;
            }

            private int BoxWidth => TextRenderer.MeasureText("WWW0000", Msg.Font).Width + 8;

            public void Relayout()
            {
                if (adjusting || Msg.IsDisposed) return;
                adjusting = true;
                try
                {
                    int w = BoxWidth;
                    if (Msg.Left != setLeft)
                    {
                        // vatSys repositioned the message box itself: make room again based on its new position.
                        baseLeft = Msg.Left;
                        int newWidth = Math.Max(40, Msg.Width - w - Gap);
                        Msg.SetBounds(baseLeft + w + Gap, Msg.Top, newWidth, Msg.Height);
                        setLeft = Msg.Left;
                    }
                    // Width-only changes (window resize / anchoring) keep the left edge, nothing to move.

                    Box.Anchor = (Msg.Anchor & (AnchorStyles.Top | AnchorStyles.Bottom)) | AnchorStyles.Left;
                    Box.SetBounds(baseLeft, Msg.Top, w, Msg.Height);
                }
                finally
                {
                    adjusting = false;
                }
            }
        }
    }
}
