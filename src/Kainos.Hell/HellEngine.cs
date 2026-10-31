/*  HellEngine.cs

A thread that takes the receiver's audio from Kainos (IKainosAudio), brings it to 8000 a second and feeds the
waterfall and the Hell receiver at the clicked frequency, and feeds the transmitter's audio to Kainos while sending.

This file is part of the Kainos Hell module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Threading;
using Kainos.Modules;

namespace Kainos.Hell
{
    internal sealed class HellEngine
    {
        private readonly IKainosHost _host;
        private Thread _thread;
        private volatile bool _run;
        private volatile bool _tapOk, _lost;
        private int _tapRx = -1;

        public volatile int Rx;
        public event Action<string> Status;
        public event Action RxLost;

        // what the UI sets (picked up by the engine's thread)
        public volatile HellMode Mode = HellMode.All[0];
        public double RxFreq = 1000, TxFreq = 1000;
        public volatile bool TxLock;
        public volatile bool UserReverse;
        public double SlantPpm;

        private HellRx _rx;
        private readonly Resampler _rs = new Resampler();
        private readonly float[] _buf = new float[16384], _a8 = new float[4096];

        public HellEngine(IKainosHost host) { _host = host; }

        // FSK's tones swap on the lower sideband
        public bool Reverse
        {
            get
            {
                string m = (_host.Radio.Mode(Rx) ?? "").ToUpperInvariant();
                bool lsb = m == "LSB" || m == "DIGL" || m == "CWL";
                return lsb ^ UserReverse;
            }
        }

        public void Start()
        {
            if (_thread != null) return;
            _run = true;
            _thread = new Thread(work) { IsBackground = true, Name = "Kainos Hell" };
            _thread.Start();
        }

        public void Stop()
        {
            _run = false;
            if (_thread != null && !_thread.Join(1500)) _host.Log("engine thread didn't stop in time");
            _thread = null;
            endTx();
            _host.Audio.ReleaseRx();
            _tapOk = false;
        }

        public void Listen() { _lost = false; _tapOk = false; }
        public bool Lost { get { return _lost; } }

        public void TuneTo(double hz) { _tune = hz; }
        private double _tune = -1;

        private bool ensureTap()
        {
            int rx = Rx;
            if (_tapOk && _tapRx == rx) return true;
            if (_lost) return false;
            string why;
            if (!_host.Audio.AcquireRx(rx, () => { _tapOk = false; _lost = true; RxLost?.Invoke(); }, out why))
            {
                Status?.Invoke("Can't listen to RX" + (rx + 1) + ": " + why);
                _lost = true;
                return false;
            }
            _tapOk = true;
            _tapRx = rx;
            return true;
        }

        private void work()
        {
            while (_run)
            {
                try
                {
                    if (Sending || _pendingTx != null) feedTx();
                    if (ensureTap())
                    {
                        int rate = _host.Audio.RxRate, n;
                        while (rate > 0 && (n = _host.Audio.ReadRx(_buf, Math.Min(_buf.Length, rate / 10 * 4))) > 0)
                        {
                            int m = _rs.Process(_buf, n, rate, _a8);
                            if (m > 0) receive(_a8, m);
                        }
                    }
                }
                catch (Exception ex) { _host.Log("engine: " + ex); Thread.Sleep(200); }
                Thread.Sleep(Sending ? 10 : 20);
            }
        }

        // ---- receive: columns for the display (received, and sent) ----

        public sealed class Col { public byte[] Pixels; public bool Tx; }
        private readonly Queue<Col> _cols = new Queue<Col>();
        public Col[] TakeColumns() { lock (_cols) { Col[] a = _cols.ToArray(); _cols.Clear(); return a; } }
        private void put(byte[] px, bool tx) { lock (_cols) { _cols.Enqueue(new Col { Pixels = px, Tx = tx }); while (_cols.Count > 4000) _cols.Dequeue(); } }

        public double Level { get { HellRx r = _rx; return r == null ? 0 : r.Level; } }

        private void receive(float[] a, int n)
        {
            spectrum(a, n);
            if (Sending || DateTime.UtcNow < _rxMuteUntil) return;
            HellMode mode = Mode;
            double t = _tune;
            if (_rx == null || _rx.Mode != mode || t >= 0)
            {
                if (t >= 0) { _tune = -1; RxFreq = t; if (!TxLock) TxFreq = t; }
                _rx = new HellRx(mode, RxFreq);
                _rx.Column += px => put(px, false);
            }
            _rx.Reverse = Reverse;
            _rx.SlantPpm = SlantPpm;
            _rx.Process(a, n);
        }

        // ---- the waterfall: an 8192-point FFT (about 1 Hz a bin), a line every 100 ms ----

        public const int FftN = 8192;
        public const double BinHz = Resampler.OutRate / (double)FftN;
        public const double SpecHi = 3500;
        public static readonly int SpecBins = (int)(SpecHi / BinHz);
        private readonly float[] _ring = new float[FftN];
        private int _ringPos, _since;
        private readonly double[] _win = hann(FftN);
        private readonly Queue<float[]> _specLines = new Queue<float[]>();

        private static double[] hann(int n) { double[] w = new double[n]; for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)); return w; }

        public float[][] TakeLines() { lock (_specLines) { float[][] l = _specLines.ToArray(); _specLines.Clear(); return l; } }

        private void spectrum(float[] a, int n)
        {
            for (int i = 0; i < n; i++) { _ring[_ringPos] = a[i]; if (++_ringPos == FftN) _ringPos = 0; }
            _since += n;
            if (_since < Resampler.OutRate / 10) return;
            _since = 0;
            double[] re = new double[FftN], im = new double[FftN];
            for (int i = 0; i < FftN; i++) re[i] = _ring[(_ringPos + i) % FftN] * _win[i];
            Fft.Transform(re, im);
            float[] line = new float[SpecBins];
            for (int b = 0; b < SpecBins; b++) line[b] = (float)(10 * Math.Log10((re[b] * re[b] + im[b] * im[b]) / ((double)FftN * FftN) + 1e-16));
            lock (_specLines) { _specLines.Enqueue(line); while (_specLines.Count > 50) _specLines.Dequeue(); }
        }

        // ---- transmit ----
        // As the PSK and SSTV modules do it: the feed started, then MOX; about 0.25 s kept queued; stopped by Abort, by
        // the radio being un-keyed elsewhere, or if MOX never comes; then the feed stopped and MOX off.

        public double LevelDb = -10;
        public volatile bool Sending;
        public volatile bool Tuning;
        public event Action TxEnded;

        private volatile HellTx _pendingTx;
        private HellTx _tx;
        private float[] _tune30;
        private int _tunePos;
        private volatile bool _abort;
        private bool _moxSeen;
        private DateTime _moxAskedAt, _rxMuteUntil;
        private int _txRate, _lead;

        // starts sending (the text so far, and whatever's added); returns why not, or null
        public string StartTx(string text)
        {
            if (!_host.Radio.PowerOn) return "The radio is off";
            if (_thread == null) return "Hell isn't open";
            int rate = _host.Audio.TxRate;
            if (rate <= 0) return "No transmit audio stream";
            HellTx tx = _tx;
            if (Sending && tx != null && !Tuning)
            {
                if (tx.Finishing) return "Finishing the last transmission";
                tx.Add(text);
                return null;
            }
            if (Sending) return "Already sending";
            tx = new HellTx(Mode, TxFreq, Reverse, rate);
            tx.ColumnSent += c =>
            {
                byte[] px = new byte[HellMode.ColumnLen];
                for (int i = 0; i < px.Length; i++) px[i] = (byte)(((c >> i) & 1) != 0 ? 255 : 0);
                put(px, true);
            };
            tx.Sent += c => SentChar?.Invoke(c);
            tx.Add(text);
            _pendingTx = tx;
            return null;
        }

        public event Action<char> SentChar;
        public void AddText(string s) { HellTx tx = _tx ?? _pendingTx; if (tx != null && !tx.Finishing) tx.Add(s); }
        public void Finish() { HellTx tx = _tx ?? _pendingTx; if (tx != null) tx.Finish(); if (Tuning) _abort = true; }
        public bool Finishing { get { HellTx tx = _tx; return tx != null && tx.Finishing; } }
        public int TxQueuedChars { get { HellTx tx = _tx ?? _pendingTx; return tx == null ? 0 : tx.Queued; } }
        public void Abort() { _abort = true; _pendingTx = null; }

        // a steady tone at the transmit frequency (up to 30 s) to set the drive (FSK: the black tone)
        public string Tune()
        {
            if (Sending) return "Already sending";
            if (!_host.Radio.PowerOn) return "The radio is off";
            if (_thread == null) return "Hell isn't open";
            int rate = _host.Audio.TxRate;
            if (rate <= 0) return "No transmit audio stream";
            HellMode m = Mode;
            double f = TxFreq - (m.Fsk ? (Reverse ? -1 : 1) * m.Shift / 2 : 0);
            float[] t = new float[rate * 30];
            double step = 2 * Math.PI * f / rate;
            for (int i = 0; i < t.Length; i++)
            {
                double env = Math.Min(1, Math.Min(i, t.Length - i) / (rate * 0.005));
                t[i] = (float)(Math.Sin(step * i) * env);
            }
            _tune30 = t;
            Tuning = true;
            _pendingTx = new HellTx(m, TxFreq, false, rate);       // a stand-in: the tone is sent instead of its audio
            return null;
        }

        private void feedTx()
        {
            HellTx pend = _pendingTx;
            if (pend != null && !Sending)
            {
                _pendingTx = null;
                if (!_host.Radio.PowerOn) { Status?.Invoke("The radio is off"); Tuning = false; return; }
                _tx = pend;
                _tunePos = 0;
                _txRate = _host.Audio.TxRate;
                _abort = false;
                _moxSeen = false;
                _moxAskedAt = DateTime.UtcNow;
                _lead = (int)(0.15 * _txRate);
                Sending = true;
                _host.Audio.TxStart();
                _host.BeginInvoke(() => _host.Radio.SetMox(true));
                _host.Log((Tuning ? "tune" : "TX " + pend.Mode.Name) + " at " + pend.Frequency.ToString("0") + " Hz, " + LevelDb.ToString("0") + " dB");
            }
            if (!Sending || _tx == null) return;
            bool keyed = _host.Radio.Transmitting;
            if (keyed) _moxSeen = true;
            if (_abort || (_moxSeen && !keyed) || (!_moxSeen && (DateTime.UtcNow - _moxAskedAt).TotalSeconds > 2))
            {
                if (!_moxSeen && !_abort) Status?.Invoke("The radio didn't go into transmit");
                else if (!_abort) Status?.Invoke("Transmit stopped: the radio was un-keyed");
                if (_abort) _host.Audio.TxClear();
                endTx();
                return;
            }
            float amp = (float)Math.Pow(10, LevelDb / 20.0);
            int ahead = Math.Max(2048, _txRate / 4);
            float[] part = new float[1024];
            bool done = false;
            while (_host.Audio.TxQueued < ahead)
            {
                int n;
                if (_lead > 0) { n = Math.Min(part.Length, _lead); Array.Clear(part, 0, n); _lead -= n; }
                else if (Tuning)
                {
                    n = Math.Min(part.Length, _tune30.Length - _tunePos);
                    for (int i = 0; i < n; i++) part[i] = _tune30[_tunePos + i] * amp;
                    _tunePos += n;
                    if (n == 0) { done = true; break; }
                }
                else
                {
                    n = _tx.Generate(part, part.Length);
                    for (int i = 0; i < n; i++) part[i] *= amp;
                    if (n == 0) { done = true; break; }
                }
                int w = _host.Audio.TxWrite(part, n);
                if (w < n) break;
            }
            if (done && _host.Audio.TxQueued == 0) endTx();
        }

        private void endTx()
        {
            if (!Sending) return;
            Sending = false;
            Tuning = false;
            _tx = null;
            _host.Audio.TxStop();
            _host.BeginInvoke(() => _host.Radio.SetMox(false));
            _rxMuteUntil = DateTime.UtcNow.AddSeconds(0.5);
            TxEnded?.Invoke();
        }
    }
}
