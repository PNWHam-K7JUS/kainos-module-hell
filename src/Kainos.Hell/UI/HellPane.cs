/*  HellPane.cs

The Hell window: a bar (mode, receiver, slant, width, reverse, pop out, options), the waterfall (click a signal to
read it), the Hell strips (what's received, and what's sent in red), the box you type into (what's typed while
sending goes straight out), and the F1 - F8 messages with the QSO's callsign, name and report, and Log QSO.

Keys while the window has focus: F1 - F8 the messages, Ctrl+T transmit, Ctrl+R back to receive when what's typed has
gone, Esc stop now.

This file is part of the Kainos Hell module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Hell.UI
{
    internal class HellPane : Panel, IKainosPaneHeight
    {
        private readonly IKainosHost _host;
        private readonly IKainosTheme _t;
        private readonly HellEngine _e;

        private readonly KButton _mode, _rxSel, _slantDown, _slantUp, _width, _rev, _clear, _pop, _options;
        private readonly KButton _txBtn, _rxBtn, _tuneBtn, _logBtn;
        private readonly KButton[] _macroBtn = new KButton[Macros.Count];
        private readonly Label _status, _callL, _nameL, _rstL;
        private readonly TextBox _call, _name, _rst, _txText;
        private readonly Waterfall _wf;
        private readonly HellView _view;
        private readonly Timer _timer;
        private bool _built;

        private string _statusText = "";
        private DateTime _statusAt;
        private string _markersFor = "";
        private int _sentUpTo;
        private DateTime _qsoStart = DateTime.UtcNow;
        private bool _logAfterTx;

        public event Action PopToggled;

        public int PreferredHeight { get { return 340; } }
        public int MinimumHeight { get { return 220; } }

        private static readonly int[] Widths = { 1, 2, 3 };

        public HellPane(IKainosHost host, HellEngine engine)
        {
            _host = host;
            _t = host.Theme;
            _e = engine;
            BackColor = _t.Bg;
            DoubleBuffered = true;

            Func<string, string, KButton> btn = (text, tip) =>
            {
                KButton b = new KButton(_t, text);
                if (tip != null) new ToolTip().SetToolTip(b, tip);
                Controls.Add(b);
                return b;
            };
            _mode = btn("Feld Hell", "The mode: Feld Hell, Slow Hell, Hell x5, Hell x9, FSK-Hell 245, FSK-Hell 105 or Hell 80");
            _rxSel = btn("RX1", "The receiver listened to: RX1 or RX2");
            _slantDown = btn("◀ Slant", "The text leans: straighten it (its clock and ours differ a little). Click whichever way makes it upright; right-click to reset.");
            _slantUp = btn("Slant ▶", "The text leans: straighten it (its clock and ours differ a little). Click whichever way makes it upright; right-click to reset.");
            _width = btn("Width 2", "How wide the received text is drawn (click to change)");
            _rev = btn("Rev", "Reverse: swap the FSK tones, for a station on the other sideband. Lower sideband (LSB / DIGL) is allowed for already.");
            _clear = btn("Clear", "Clear the received text");
            _pop = btn("Pop out", "Docked under the panadapter, or in its own window");
            _options = btn("Options", "Station, transmit level, display and logging");

            _txBtn = btn("TX", "Transmit what's in the box, then what you type (Ctrl+T)");
            _txBtn.OnColor = _t.Tx;
            _rxBtn = btn("RX", "Back to receive once what's typed has gone (Ctrl+R). Esc stops at once.");
            _tuneBtn = btn("Tune", "A steady tone at the transmit frequency for up to 30 s, to set the drive (click again to stop)");
            _tuneBtn.OnColor = _t.Tx;
            _logBtn = btn("Log QSO", "Log this QSO (their callsign, name and report from the boxes)");
            for (int i = 0; i < Macros.Count; i++)
            {
                int ii = i;
                _macroBtn[i] = btn("", "F" + (i + 1) + ": send this message. Right-click to change it.");
                _macroBtn[i].FontPx = 10.5f;
                _macroBtn[i].MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) editMacro(ii); else if (e.Button == MouseButtons.Left) sendMacro(ii); };
            }

            Func<string, Label> label = text =>
            {
                Label l = new Label { Text = text, ForeColor = _t.Dim, BackColor = _t.Bg, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Font = _t.Font(_t.S(10), true) };
                Controls.Add(l);
                return l;
            };
            _callL = label("CALL"); _nameL = label("NAME"); _rstL = label("RST");
            Func<TextBox> box = () =>
            {
                TextBox tb = new TextBox { BackColor = _t.Panel, ForeColor = _t.Text, BorderStyle = BorderStyle.FixedSingle, Font = _t.Font(_t.S(12), true) };
                Controls.Add(tb);
                return tb;
            };
            _call = box(); _call.CharacterCasing = CharacterCasing.Upper;
            _name = box();
            _rst = box(); _rst.Text = "599";

            _status = new Label { ForeColor = _t.Dim, BackColor = _t.Bg, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = _t.Font(_t.S(11), false) };
            Controls.Add(_status);

            _wf = new Waterfall(_t) { HiHz = 3500 };
            Controls.Add(_wf);
            _view = new HellView(_t);
            Controls.Add(_view);
            _txText = new TextBox
            {
                Multiline = true, AcceptsReturn = true, BorderStyle = BorderStyle.FixedSingle, BackColor = _t.Surface, ForeColor = _t.Text,
                Font = _t.Font(_t.S(13), false), ScrollBars = ScrollBars.None,
            };
            Controls.Add(_txText);

            wire();
            applySettings();
            refreshMacros();

            _timer = new Timer { Interval = 100 };
            _timer.Tick += (s, e) => tick();
            _timer.Start();
            _built = true;
            PerformLayout();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Stop(); _timer.Dispose(); }
            base.Dispose(disposing);
        }

        // ---- layout ----

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (!_built) return;
            int pad = _t.S(4), bh = _t.S(24), x = pad, y = pad;
            Action<Control, int> place = (c, w) => { c.SetBounds(x, y, _t.S(w), bh); x += _t.S(w) + pad; };
            place(_mode, 96); place(_rxSel, 44); x += pad;
            place(_slantDown, 64); place(_slantUp, 64); place(_width, 64); if (_rev.Visible) place(_rev, 44); place(_clear, 50);
            int right = Width - pad;
            _options.SetBounds(right - _t.S(64), y, _t.S(64), bh); right -= _t.S(64) + pad;
            _pop.SetBounds(right - _t.S(64), y, _t.S(64), bh); right -= _t.S(64) + pad;
            _status.SetBounds(x + pad, y, Math.Max(0, right - x - pad), bh);

            y += bh + pad;
            int bottomRows = 2 * bh + pad, txH = _t.S(46);
            int rest = Height - y - bottomRows - txH - 3 * pad;
            int wfH = Math.Max(_t.S(46), Math.Min(_t.S(110), rest * 25 / 100));
            _wf.SetBounds(pad, y, Width - 2 * pad, wfH);
            y += wfH + pad;
            int vh = Math.Max(_t.S(30), rest - wfH);
            _view.SetBounds(pad, y, Width - 2 * pad, vh);
            y += vh + pad;

            int bw2 = _t.S(56);
            _txText.SetBounds(pad, y, Width - 2 * pad - 3 * (bw2 + pad), txH);
            int bx = Width - pad - 3 * bw2 - 2 * pad;
            _txBtn.SetBounds(bx, y, bw2, txH); bx += bw2 + pad;
            _rxBtn.SetBounds(bx, y, bw2, txH); bx += bw2 + pad;
            _tuneBtn.SetBounds(bx, y, bw2, txH);
            y += txH + pad;

            int mw = (Width - pad - Macros.Count * pad) / Macros.Count;
            for (int i = 0; i < Macros.Count; i++) _macroBtn[i].SetBounds(pad + i * (mw + pad), y, mw, bh);
            y += bh + pad;
            x = pad;
            place(_callL, 34); _call.SetBounds(x, y + 1, _t.S(96), bh - 2); x += _t.S(96) + pad * 2;
            place(_nameL, 40); _name.SetBounds(x, y + 1, _t.S(96), bh - 2); x += _t.S(96) + pad * 2;
            place(_rstL, 28); _rst.SetBounds(x, y + 1, _t.S(44), bh - 2); x += _t.S(44) + pad * 2;
            _logBtn.SetBounds(x, y, _t.S(72), bh);
        }

        // ---- wiring ----

        private void wire()
        {
            _mode.Click += (s, e) => modeMenu().Show(_mode, new Point(0, _mode.Height));
            _rxSel.Click += (s, e) =>
            {
                _host.SetSetting("rx", _e.Rx == 1 ? "0" : "1");
                _host.SetMarkers(_e.Rx, null);
                applySettings();
                _e.Listen();
                _markersFor = "";
            };
            Action<double> slant = d =>
            {
                double v = d == 0 ? 0 : _e.SlantPpm + d;
                _host.SetSetting("slant", v.ToString("0", CultureInfo.InvariantCulture));
                applySettings();
                status("Slant " + (v > 0 ? "+" : "") + v.ToString("0") + " ppm");
            };
            _slantDown.MouseUp += (s, e) => slant(e.Button == MouseButtons.Right ? 0 : -50);
            _slantUp.MouseUp += (s, e) => slant(e.Button == MouseButtons.Right ? 0 : 50);
            _width.Click += (s, e) =>
            {
                int i = Array.IndexOf(Widths, _view.ColumnWidth);
                _host.SetSetting("width", Widths[(i + 1) % Widths.Length].ToString(CultureInfo.InvariantCulture));
                applySettings();
            };
            _rev.Click += (s, e) => { _host.SetSetting("rev", _e.UserReverse ? "0" : "1"); applySettings(); };
            _clear.Click += (s, e) => _view.Clear();
            _pop.Click += (s, e) => PopToggled?.Invoke();
            _options.Click += (s, e) => optionsMenu().Show(_options, new Point(0, _options.Height));

            _wf.Clicked += (hz, b) =>
            {
                if (b == MouseButtons.Right)
                {
                    _e.TxLock = true;
                    _e.TxFreq = Math.Round(hz);
                    _host.SetSetting("txlock", "1");
                    status("Transmitting on " + _e.TxFreq.ToString("0") + " Hz (locked: Options to unlock)");
                }
                else if (b == MouseButtons.Left) tuneTo(Math.Round(hz));
            };
            _wf.Nudged += d => tuneTo(_e.RxFreq + d);

            _txBtn.Click += (s, e) => startTx();
            _rxBtn.Click += (s, e) => { if (_e.Sending) _e.Finish(); };
            _tuneBtn.Click += (s, e) =>
            {
                if (_e.Tuning) { _e.Abort(); return; }
                string why = _e.Tune();
                status(why ?? "Tune: a steady tone. Set the drive for full power with the ALC barely moving.");
            };
            _logBtn.Click += (s, e) => logQso();

            _txText.TextChanged += (s, e) => typed();
            _txText.KeyDown += (s, e) => { if (keys(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; } };
            _view.KeyDown += (s, e) => keys(e.KeyData);
            _call.TextChanged += (s, e) => { if (_call.Text.Trim().Length > 0 && _call.Focused) _qsoStart = DateTime.UtcNow; };

            _e.Status += s => _host.BeginInvoke(() => status(s));
            _e.TxEnded += () => _host.BeginInvoke(txEnded);
        }

        private void tuneTo(double hz)
        {
            _e.TuneTo(hz);
            _e.RxFreq = hz;
            if (!_e.TxLock) _e.TxFreq = hz;
            _host.SetSetting("rxfreq", hz.ToString("0", CultureInfo.InvariantCulture));
        }

        private void applySettings()
        {
            _e.Mode = HellMode.ByName(_host.GetSetting("mode", "Feld Hell"));
            _e.Rx = _host.GetSetting("rx", "0") == "1" ? 1 : 0;
            _e.UserReverse = _host.GetSetting("rev", "0") == "1";
            _e.TxLock = _host.GetSetting("txlock", "0") == "1";
            double v;
            if (double.TryParse(_host.GetSetting("txlevel", "-10"), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) _e.LevelDb = v;
            _e.SlantPpm = double.TryParse(_host.GetSetting("slant", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
            int w;
            _view.ColumnWidth = int.TryParse(_host.GetSetting("width", "2"), out w) && w >= 1 && w <= 3 ? w : 2;
            _mode.Text = _e.Mode.Name;
            _rxSel.Text = "RX" + (_e.Rx + 1);
            _width.Text = "Width " + _view.ColumnWidth;
            _rev.On = _e.UserReverse;
            bool revVis = _e.Mode.Fsk;
            if (_rev.Visible != revVis) { _rev.Visible = revVis; PerformLayout(); }
            _slantDown.On = _e.SlantPpm < 0;
            _slantUp.On = _e.SlantPpm > 0;
            _wf.Width_Hz = _e.Mode.Bandwidth;
        }

        public void Restore()
        {
            double hz;
            if (double.TryParse(_host.GetSetting("rxfreq", "1000"), NumberStyles.Float, CultureInfo.InvariantCulture, out hz) && hz > 100 && hz < 3500) tuneTo(hz);
            if (double.TryParse(_host.GetSetting("txfreq", "1000"), NumberStyles.Float, CultureInfo.InvariantCulture, out hz) && hz > 100 && hz < 3500 && _e.TxLock) _e.TxFreq = hz;
        }

        private void refreshMacros()
        {
            for (int i = 0; i < Macros.Count; i++) _macroBtn[i].Text = "F" + (i + 1) + " " + Macros.Get(_host, i).Label;
        }

        // ---- the timer ----

        // false while the pane is closed: its timer keeps running, and without this it put the mode's marker back on
        // the panadapter after closing had cleared it, so the marker stayed in every mode (GitHub #86)
        public bool Showing;

        private void tick()
        {
            if (!Showing) return;
            foreach (float[] line in _e.TakeLines()) _wf.AddLine(line, HellEngine.BinHz);
            _wf.RxHz = _e.RxFreq;
            _wf.TxHz = _e.TxFreq;
            _wf.TxLocked = _e.TxLock;
            _wf.Invalidate();
            HellEngine.Col[] cols = _e.TakeColumns();
            foreach (HellEngine.Col c in cols) _view.Add(c.Pixels, c.Tx);
            if (cols.Length > 0) _view.Invalidate();

            bool sending = _e.Sending;
            _txBtn.On = sending && !_e.Tuning;
            _tuneBtn.On = _e.Tuning;
            _txBtn.Text = sending && !_e.Tuning ? (_e.Finishing ? "Ending" : "TX on") : "TX";
            _mode.Enabled = !sending;

            markers();
            string m = _host.Radio.Mode(_e.Rx) ?? "";
            if (_statusText.Length > 0 && (DateTime.UtcNow - _statusAt).TotalSeconds < 8) _status.Text = _statusText;
            else if (_e.Lost) _status.Text = "Another mode has the receiver's audio: click RX" + (_e.Rx + 1) + " to listen again";
            else
            {
                string warn = m.StartsWith("DIG") || m == "USB" || m == "LSB" ? "" : "   (the radio is in " + m + ": Hell wants DIGU or USB)";
                _status.Text = (sending ? (_e.Tuning ? "Tune  " : "Sending  ") + _e.TxFreq.ToString("0") + " Hz" + (_e.TxQueuedChars > 0 ? ", " + _e.TxQueuedChars + " to go" : "")
                    : "RX " + _e.RxFreq.ToString("0") + " Hz" + (Math.Abs(_e.SlantPpm) > 0 ? "   slant " + _e.SlantPpm.ToString("+0;-0") + " ppm" : "")) + warn;
            }
        }

        private void markers()
        {
            if (_host.GetSetting("markers", "1") != "1") { if (_markersFor != "off") { _host.SetMarkers(_e.Rx, null); _markersFor = "off"; } return; }
            string mode = _host.Radio.Mode(_e.Rx) ?? "";
            double sgn = mode == "LSB" || mode.EndsWith("L") ? -1 : 1, rx = Math.Round(_e.RxFreq), tx = Math.Round(_e.TxFreq);
            string key = _e.Rx + ":" + sgn + ":" + rx + ":" + tx + ":" + _e.TxLock + ":" + _e.Mode.Name;
            if (key == _markersFor) return;
            _markersFor = key;
            List<KainosMarker> l = new List<KainosMarker> { new KainosMarker(sgn * rx, "Hell", _t.Gold) };
            if (_e.TxLock && Math.Abs(tx - rx) > 2) l.Add(new KainosMarker(sgn * tx, "TX", _t.Tx));
            _host.SetMarkers(_e.Rx, l.ToArray());
        }

        public void ClearMarkers() { _host.SetMarkers(0, null); _host.SetMarkers(1, null); _markersFor = ""; }

        private void status(string s) { _statusText = s ?? ""; _statusAt = DateTime.UtcNow; _status.Text = _statusText; }

        // ---- sending ----

        private bool keys(Keys k)
        {
            if (k >= Keys.F1 && k <= Keys.F8) { sendMacro(k - Keys.F1); return true; }
            if (k == (Keys.Control | Keys.T)) { startTx(); return true; }
            if (k == (Keys.Control | Keys.R)) { if (_e.Sending) _e.Finish(); return true; }
            if (k == Keys.Escape && _e.Sending) { _e.Abort(); status("Stopped"); return true; }
            return false;
        }

        private MacroFields fields()
        {
            return new MacroFields
            {
                MyCall = _host.Callsign ?? "", MyGrid = (_host.Grid ?? "").Length >= 4 ? _host.Grid.Substring(0, 4) : _host.Grid ?? "",
                MyName = _host.GetSetting("myname", ""), Call = _call.Text.Trim(), Name = _name.Text.Trim(), Rst = _rst.Text.Trim(), Mode = _e.Mode.Name,
            };
        }

        private bool checkStation()
        {
            if (string.IsNullOrWhiteSpace(_host.Callsign)) { status("Your callsign isn't set: Kainos Setup > DSP > FreeDV (RADE) holds it"); return false; }
            return true;
        }

        private void startTx()
        {
            if (_e.Sending && !_e.Tuning) return;
            if (!checkStation()) return;
            string why = _e.StartTx(_txText.Text.Replace("\r\n", " ").Replace("\n", " "));
            if (why != null) { status(why); return; }
            _sentUpTo = _txText.TextLength;
            _txText.Focus();
            _txText.Select(_txText.TextLength, 0);
        }

        private void sendMacro(int i)
        {
            if (!checkStation()) return;
            Macro m = Macros.Get(_host, i);
            bool rx;
            string text = Macros.Expand(m.Text, fields(), out rx).Replace("\n", " ");
            if (m.Text.IndexOf("<CALL>", StringComparison.OrdinalIgnoreCase) >= 0 && _call.Text.Trim().Length == 0) { status("Their callsign first (type it in CALL)"); return; }
            if (_e.Sending && !_e.Tuning)
            {
                if (_e.Finishing) { status("Finishing the last transmission"); return; }
                _e.AddText(text);
            }
            else
            {
                string why = _e.StartTx(text);
                if (why != null) { status(why); return; }
                _sentUpTo = _txText.TextLength;
            }
            if (rx) _e.Finish();
            if (m.Label.Trim() == "73" && _host.GetSetting("log73", "1") == "1" && _call.Text.Trim().Length > 0) _logAfterTx = true;
            _txText.Focus();
            _txText.Select(_txText.TextLength, 0);
        }

        // typing while sending: what's new at the end goes out (a backspace leaves a gap: Hell can't take a column back)
        private void typed()
        {
            if (!_e.Sending || _e.Tuning || _e.Finishing) return;
            string t = _txText.Text;
            if (t.Length > _sentUpTo) _e.AddText(t.Substring(_sentUpTo).Replace("\r\n", " ").Replace("\r", "").Replace("\n", " "));
            else if (t.Length < _sentUpTo) _e.AddText(new string('\b', _sentUpTo - t.Length));
            _sentUpTo = t.Length;
        }

        private void txEnded()
        {
            _txText.Clear();
            _sentUpTo = 0;
            if (_logAfterTx) { _logAfterTx = false; logQso(); }
        }

        // ---- logging ----

        private void logQso()
        {
            HellQso q = new HellQso
            {
                Call = _call.Text.Trim(), Name = _name.Text.Trim(), RstSent = _rst.Text.Trim(), Submode = _e.Mode.Submode,
                On = _qsoStart, Off = DateTime.UtcNow, DialHz = _host.Radio.VfoHz(_e.Rx), Power = _host.GetSetting("power", ""),
            };
            using (LogQsoForm f = new LogQsoForm(_host, q))
            {
                if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
                HellQso r = f.Qso;
                try
                {
                    string rec = HellLog.Append(HellLog.FileOf(_host.DataFolder), r, _host.Callsign, _host.Grid);
                    string why = _host.GetSetting("udp.on", "1") == "1" ? HellLog.TellLoggers(_host.GetSetting("udp", "127.0.0.1:2237"), rec) : null;
                    status("Logged " + r.Call + (why != null ? " (logging programs not told: " + why + ")" : ""));
                    _host.Log("logged " + r.Call + " " + _e.Mode.Name);
                    _call.Text = ""; _name.Text = ""; _rst.Text = "599";
                }
                catch (Exception ex) { status("Couldn't log it: " + ex.Message); }
            }
        }

        // ---- menus ----

        private ContextMenuStrip modeMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            foreach (HellMode mode in HellMode.All)
            {
                HellMode mm = mode;
                ToolStripMenuItem it = new ToolStripMenuItem(mode.Name + "   " + mode.ColumnRate.ToString("0.####", CultureInfo.InvariantCulture) + " columns/s" + (mode.Fsk ? ", FSK " + mode.Shift.ToString("0.#", CultureInfo.InvariantCulture) + " Hz" : "")) { Checked = mode == _e.Mode };
                it.Click += (s, e) => { _host.SetSetting("mode", mm.Name); applySettings(); status(mm.Name); };
                m.Items.Add(it);
            }
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        private ContextMenuStrip optionsMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            Func<string, string, string, string, ToolStripMenuItem> check = (text, key, def, tip) =>
            {
                ToolStripMenuItem it = new ToolStripMenuItem(text) { Checked = _host.GetSetting(key, def) == "1", ToolTipText = tip };
                it.Click += (s, e) => { _host.SetSetting(key, it.Checked ? "0" : "1"); _markersFor = ""; applySettings(); };
                m.Items.Add(it);
                return it;
            };
            ToolStripMenuItem lockTx = new ToolStripMenuItem("Lock the transmit frequency") { Checked = _e.TxLock, ToolTipText = "Transmit where it is now, wherever you listen (a right click on the waterfall sets it). Off: transmit where you listen." };
            lockTx.Click += (s, e) => { _host.SetSetting("txlock", _e.TxLock ? "0" : "1"); applySettings(); if (!_e.TxLock) _e.TxFreq = _e.RxFreq; };
            m.Items.Add(lockTx);
            check("Show the frequencies on the panadapter", "markers", "1", "A line where you're listening (and transmitting, when locked)");
            check("Log window after sending the 73 message", "log73", "1", "Open Log QSO, filled in, when the F-key message labelled 73 has gone out");
            check("Send logged QSOs to logging programs", "udp.on", "1", "As WSJT-X does (UDP \"Logged ADIF\"), for Log4OM, N1MM+, HRD, DXKeeper, GridTracker and the Kainos Logbook");
            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem lv = new ToolStripMenuItem("Transmit level") { ToolTipText = "Set with Tune: full power with the ALC barely moving" };
            foreach (int db in new[] { 0, -3, -6, -10, -15, -20, -25, -30 })
            {
                int d = db;
                ToolStripMenuItem it = new ToolStripMenuItem(db + " dB") { Checked = Math.Abs(_e.LevelDb - db) < 0.5 };
                it.Click += (s, e) => { _e.LevelDb = d; _host.SetSetting("txlevel", d.ToString(CultureInfo.InvariantCulture)); status("Transmit level " + d + " dB"); };
                lv.DropDownItems.Add(it);
            }
            m.Items.Add(lv);
            ToolStripMenuItem name = new ToolStripMenuItem("Your name (for <MYNAME>)...");
            name.Click += (s, e) =>
            {
                string v = Microsoft.VisualBasic.Interaction.InputBox("Your name, as the messages send it (<MYNAME>).", "Hell: your name", _host.GetSetting("myname", ""));
                if (v != null && v.Trim().Length > 0) { _host.SetSetting("myname", v.Trim()); status("Name: " + v.Trim()); }
            };
            m.Items.Add(name);
            ToolStripMenuItem pwr = new ToolStripMenuItem("Power for the log...");
            pwr.Click += (s, e) =>
            {
                string v = Microsoft.VisualBasic.Interaction.InputBox("The power to log (watts), or blank.", "Hell: power", _host.GetSetting("power", ""));
                if (v != null) _host.SetSetting("power", v.Trim());
            };
            m.Items.Add(pwr);
            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem addr = new ToolStripMenuItem("Logging programs address...");
            addr.Click += (s, e) =>
            {
                string cur = _host.GetSetting("udp", "127.0.0.1:2237");
                string v = Microsoft.VisualBasic.Interaction.InputBox("Where logged QSOs are sent, as WSJT-X sends them (UDP host:port). Log4OM, N1MM+, HRD, DXKeeper (via JTAlert), GridTracker and the Kainos Logbook listen on 127.0.0.1:2237 unless set otherwise.", "Hell: logging programs", cur);
                if (string.IsNullOrWhiteSpace(v) || v == cur) return;
                if (!HellLog.ValidAddress(v)) { status("Not changed: the address should be host:port, e.g. 127.0.0.1:2237"); return; }
                _host.SetSetting("udp", v.Trim());
                status("Logged QSOs go to " + v.Trim());
            };
            m.Items.Add(addr);
            ToolStripMenuItem logf = new ToolStripMenuItem("Open the log folder");
            logf.Click += (s, e) => { Directory.CreateDirectory(_host.DataFolder); Process.Start("explorer.exe", "\"" + _host.DataFolder + "\""); };
            m.Items.Add(logf);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        private void editMacro(int i)
        {
            using (MacroForm f = new MacroForm(_host, i, Macros.Get(_host, i)))
            {
                if (f.ShowDialog(FindForm()) != DialogResult.OK || f.Macro == null) return;
                Macros.Set(_host, i, f.Macro);
                refreshMacros();
            }
        }
    }
}
