/*  Program.cs (self-test)

Checks the Hell modem without a radio. Hell isn't decoded, it's drawn, so a mode passes when the pixels received
(lined up with the ones sent: there's no timing to recover) mostly match: clean, with noise, at 44.1 kHz, FSK on the
other sideband. Then the transmit path through the engine, and the window drawn with a signal playing.

  Kainos.Hell.SelfTest.exe [--ui]

This file is part of the Kainos Hell module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Kainos.Hell.SelfTest
{
    internal static class Program
    {
        private static int _fail;
        private static readonly Random Rnd = new Random(42);

        private static void check(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok) _fail++;
        }

        internal static double Gauss()
        {
            double u1 = 1.0 - Rnd.NextDouble(), u2 = Rnd.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        private const string Msg = "CQ CQ de K7JUS K7JUS pse K";

        [STAThread]
        private static int Main(string[] args)
        {
            SetProcessDPIAware();
            font();
            foreach (HellMode m in HellMode.All)
            {
                string text = m.Name == "Slow Hell" ? "CQ K7JUS" : Msg;
                double clean = loop(m, text, 1000, 1000, 99, 48000, false, false);
                check(clean > (m.Name == "Slow Hell" ? 0.93 : 0.97), m.Name + " clean: " + pct(clean) + " of pixels right");
                // noise in 2.5 kHz, set for the same signal-to-noise in the mode's own filter (10 dB)
                double snr = 10 - 10 * Math.Log10(2500 / m.FilterBw);
                double noisy = loop(m, text, 1500, 1500, snr, 48000, false, false);
                check(noisy > 0.85, m.Name + " at " + snr.ToString("0") + " dB SNR in 2.5 kHz: " + pct(noisy) + " right");
            }
            double r44 = loop(HellMode.ByName("Feld Hell"), Msg, 900, 900, 99, 44100, false, false);
            check(r44 > 0.97, "Feld Hell at 44.1 kHz: " + pct(r44));
            double rev = loop(HellMode.ByName("FSK-Hell 245"), Msg, 1000, 1000, 99, 48000, true, true);
            check(rev > 0.97, "FSK-Hell 245 reversed both ends: " + pct(rev));
            double wrong = loop(HellMode.ByName("FSK-Hell 245"), Msg, 1000, 1000, 99, 48000, true, false);
            check(wrong < 0.6, "FSK-Hell 245 reversed one end only: " + pct(wrong) + " (a negative)");
            double off = loop(HellMode.ByName("Feld Hell"), Msg, 1030, 1000, 99, 48000, false, false);
            check(off > 0.9, "Feld Hell 30 Hz off tune: " + pct(off));
            bandwidth();
            transmit("Feld Hell");
            transmit("FSK-Hell 245");
            if (args.Contains("--ui")) ui();
            Console.WriteLine(_fail == 0 ? "All passed" : _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        private static string pct(double v) { return (100 * v).ToString("0.0") + " %"; }

        private static void font()
        {
            bool ok = true;
            for (char c = '!'; c <= '~'; c++)
            {
                List<ushort> cols = HellFont.Columns(c);
                if (cols.Count < 3 || cols.Count > 7 || cols[0] != 0 || cols[cols.Count - 1] != 0 || cols[1] == 0 || cols[cols.Count - 2] == 0) { ok = false; Console.WriteLine("  font: " + c + " " + cols.Count); }
            }
            check(ok, "font: ! - ~ each 1 - 5 columns (trimmed) with a blank each side");
            check(HellFont.Columns(' ').Count == 5 && HellFont.Columns(' ').All(x => x == 0), "font: a space is 5 blank columns");
            List<ushort> t = HellFont.Columns('T');
            check(t.Count == 7 && t[3] == 0x3fff && t[1] == 0x3000, "font: T (its top bar, its stem the full height)");
        }

        // the pixels sent (from the columns as they go), and the audio
        internal static float[] TxAudio(HellMode m, string text, double hz, int rate, bool rev, List<int> pixels)
        {
            HellTx tx = new HellTx(m, hz, rev, rate);
            if (pixels != null) tx.ColumnSent += c => { for (int i = 0; i < HellMode.ColumnLen; i++) pixels.Add((c >> i) & 1); };
            tx.Add(text);
            tx.Finish();
            List<float> a = new List<float>(new float[rate / 2]);
            float[] buf = new float[4096];
            int n;
            while ((n = tx.Generate(buf, buf.Length)) > 0) for (int i = 0; i < n; i++) a.Add(buf[i] * 0.5f);
            a.AddRange(new float[rate / 2]);
            return a.ToArray();
        }

        // sends text and receives it; the share of pixels right, at the best lining up
        private static double loop(HellMode m, string text, double txHz, double rxHz, double snrDb, int rate, bool txRev, bool rxRev)
        {
            List<int> sent = new List<int>();
            float[] a = TxAudio(m, text, txHz, rate, txRev, sent);
            if (snrDb < 90)
            {
                // the signal's power: a tone at 0.5, on about a third of the time (on-off) or all the time (FSK)
                double ps = 0.125 * (m.Fsk ? 1 : sent.Average());
                double sigma = Math.Sqrt(ps / Math.Pow(10, snrDb / 10) * (rate / 2.0) / 2500);
                for (int i = 0; i < a.Length; i++) a[i] += (float)(Gauss() * sigma);
            }
            Resampler rs = new Resampler();
            HellRx rx = new HellRx(m, rxHz) { Reverse = rxRev };
            List<byte> got = new List<byte>();
            rx.Column += px => got.AddRange(px);
            float[] o = new float[1200];
            for (int p = 0; p < a.Length; p += 4800)
            {
                int len = Math.Min(4800, a.Length - p);
                float[] part = new float[len];
                Array.Copy(a, p, part, 0, len);
                rx.Process(o, rs.Process(part, len, rate, o));
            }
            // the received stream lined up with the sent one: the best of every lag within 4 columns of the start
            int lead = (int)(0.5 * m.PixelRate);        // the half second of silence first
            double best = 0;
            for (int lag = lead - 4 * HellMode.ColumnLen; lag <= lead + 4 * HellMode.ColumnLen; lag++)
            {
                int right = 0, n = 0;
                for (int i = 0; i < sent.Count; i++)
                {
                    int j = i + lag;
                    if (j < 0 || j >= got.Count) continue;
                    n++;
                    if ((got[j] >= 128 ? 1 : 0) == sent[i]) right++;
                }
                if (n > sent.Count * 0.9) best = Math.Max(best, right / (double)n);
            }
            return best;
        }

        // Feld Hell's spectrum: nearly all its power within +-250 Hz (the shaped edges keep it narrow)
        private static void bandwidth()
        {
            HellMode m = HellMode.ByName("Feld Hell");
            HellTx tx = new HellTx(m, 1000, false, 8000);
            tx.Add(Msg + " " + Msg);
            float[] a = new float[32768];
            tx.Generate(a, a.Length);
            double[] re = new double[32768], im = new double[32768];
            for (int i = 0; i < a.Length; i++) re[i] = a[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (a.Length - 1)));
            Fft.Transform(re, im);
            double inside = 0, all = 0;
            for (int b = 0; b < 16384; b++)
            {
                double p = re[b] * re[b] + im[b] * im[b], hz = b * 8000.0 / 32768;
                all += p;
                if (Math.Abs(hz - 1000) <= 250) inside += p;
            }
            check(inside / all > 0.99, "Feld Hell's power within +-250 Hz: " + pct(inside / all));
        }

        // sending through the engine: MOX on, the audio fed, typed text added while sending, MOX off after; the audio
        // sent is received right, and what's sent is shown (as columns marked sent)
        private static void transmit(string modeName)
        {
            string dir = Path.Combine(Path.GetTempPath(), "kainos-hell-selftest");
            FakeHost h = new FakeHost(dir, Path.Combine(dir, "data-tx")) { ModeName = "DIGU", Speed = 4 };
            HellEngine e = new HellEngine(h) { TxLock = true, TxFreq = 1234, LevelDb = -6 };
            e.Mode = HellMode.ByName(modeName);
            e.Start();
            string why = e.StartTx("CQ de K7JUS ");
            e.AddText("typed");
            e.Finish();
            bool moxOn = false;
            DateTime until = DateTime.UtcNow.AddSeconds(60);
            Thread.Sleep(200);
            while (e.Sending && DateTime.UtcNow < until) { if (h.Mox) moxOn = true; Thread.Sleep(50); }
            Thread.Sleep(300);
            int txCols = e.TakeColumns().Count(c => c.Tx);
            e.Stop();
            float[] a;
            lock (h.TxAudio) a = h.TxAudio.ToArray();
            float peak = a.Length == 0 ? 0 : a.Max(v => Math.Abs(v));
            // the columns expected: 3 dots, the text, 3 dots and a space
            int expect = 0;
            foreach (char c in "...CQ de K7JUS typed... ") expect += HellFont.Columns(c).Count;
            check(why == null && moxOn && !h.Mox, modeName + " TX: MOX on while sending, off after" + (why != null ? " (" + why + ")" : ""));
            check(txCols == expect, modeName + " TX: " + txCols + " columns shown as sent (" + expect + " expected)");
            double secs = a.Length / 48000.0, want = expect / e.Mode.ColumnRate + 0.15;
            check(Math.Abs(secs - want) < 0.2, modeName + " TX: " + secs.ToString("0.00") + " s of audio (" + want.ToString("0.00") + " expected)");
            check(Math.Abs(peak - 0.501) < 0.02, modeName + " TX: peak " + peak.ToString("0.000") + " at -6 dB");
        }

        // the window with the module running and a Feld Hell signal playing; a screenshot saved
        private static void ui()
        {
            string dir = Path.Combine(Path.GetTempPath(), "kainos-hell-selftest");
            Form f = new Form { Text = "Kainos Hell self-test", ClientSize = new Size(1100, 420), StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            FakeHost h = new FakeHost(dir, Path.Combine(dir, "data-ui")) { Window = f, ModeName = "DIGU", Speed = 1 };
            h.Settings["open"] = "1";
            h.Settings["rxfreq"] = "1000";
            HellModule mod = new HellModule();
            f.Shown += (s, e) =>
            {
                mod.Start(h);
                h.Play(TxAudio(HellMode.ByName("Feld Hell"), "CQ CQ de K7JUS K7JUS pse K  The quick brown fox 0123456789", 1003, 48000, false, null));
            };
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 26000 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                Rectangle r = f.RectangleToScreen(f.ClientRectangle);
                using (Bitmap b = new Bitmap(r.Width, r.Height))
                {
                    using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    string png = Path.Combine(dir, "ui.png");
                    b.Save(png);
                    Console.WriteLine("screenshot: " + png);
                }
                mod.Stop();
                f.Close();
            };
            t.Start();
            Application.Run(f);
            mod.Dispose();
        }
    }
}
