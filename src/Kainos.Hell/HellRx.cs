/*  HellRx.cs

The Hell receiver: there's nothing to decode, only to draw. The 8 kHz audio is mixed down from the tone (FSK: from
between the two), filtered to the mode's width (decimated first, so Slow Hell's 35 Hz filter is cheap), and cut into
pixels at the mode's pixel rate. On-off modes: each pixel the strongest the tone was during it, against a peak that's
held and slowly let go (fldigi's way). FSK modes: which tone it was, from the frequency. Every 14 pixels make a column.
There's no timing to recover: the sender's clock and ours drift apart a little, which slants the text; Slant puts that
right.

This file is part of the Kainos Hell module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;

namespace Kainos.Hell
{
    internal sealed class HellRx
    {
        public readonly HellMode Mode;
        public double Frequency;
        public bool Reverse;                    // FSK on the other sideband: the tones swapped
        public double SlantPpm;                 // the pixel clock this much fast (+) or slow
        public double Level;                    // the signal's strength (0 - 1 of its peak), for the meter
        public event Action<byte[]> Column;     // ColumnLen pixels, bottom first, 0 white - 255 black

        private readonly int _decim;
        private readonly double _fs;            // after decimating
        private readonly CpxFir _f1, _f2;
        private int _decCount;
        private Cpx _osc = new Cpx(1, 0);
        private int _oscCount;
        private Cpx _prev;

        private double _pixClock, _peak, _hold = 1e-6, _fsum;
        private int _fn;
        private readonly byte[] _col = new byte[HellMode.ColumnLen];
        private int _row;

        public HellRx(HellMode mode, double frequency)
        {
            Mode = mode;
            Frequency = frequency;
            _decim = Math.Max(1, (int)(Resampler.OutRate / (4 * mode.FilterBw)));
            _fs = Resampler.OutRate / (double)_decim;
            _f1 = new CpxFir(Fir.LowPass(Math.Min(801, Math.Max(15, 8 * _decim + 1)), 0.4 / _decim));
            int taps2 = Math.Max(15, Math.Min(401, (int)(4 * _fs / (mode.FilterBw / 2)))) | 1;
            _f2 = new CpxFir(Fir.LowPass(taps2, mode.FilterBw / 2 / _fs));
        }

        public void Process(float[] a, int n)
        {
            for (int i = 0; i < n; i++)
            {
                _f1.Push(new Cpx(a[i] * _osc.Re, -a[i] * _osc.Im));
                _osc = _osc * Cpx.Polar(2 * Math.PI * Frequency / Resampler.OutRate);
                if (++_oscCount == 1024) { _oscCount = 0; _osc = _osc * (1 / _osc.Mag); }
                if (++_decCount < _decim) continue;
                _decCount = 0;
                _f2.Push(_f1.Output());
                sample(_f2.Output());
            }
        }

        private void sample(Cpx z)
        {
            double mag = z.Mag;
            if (Mode.Fsk)
            {
                // the frequency: the turn from the last sample, as Hz from the centre
                Cpx d = z * _prev.Conj;
                _fsum += d.Arg * _fs / (2 * Math.PI);
                _fn++;
                if (mag > _peak) _peak = mag;
            }
            else if (mag > _peak) _peak = mag;
            _prev = z;

            _pixClock += Mode.PixelRate * (1 + SlantPpm * 1e-6) / _fs;
            if (_pixClock < 1) return;
            _pixClock -= 1;

            double v;
            if (Mode.Fsk)
            {
                double hz = _fn > 0 ? _fsum / _fn : 0;
                // black is the lower tone (higher when reversed)
                double x = -hz / (Mode.Shift / 2) * (Reverse ? -1 : 1);
                v = Math.Max(0, Math.Min(1, 0.5 * (x + 1)));
                if (_peak > _hold) _hold = _peak; else _hold *= 1 - 0.02 / HellMode.ColumnLen;
                Level = Math.Min(1, _peak / _hold);
                if (Level < 0.15) v = 0;            // nothing there: white, not noise
                _fsum = 0; _fn = 0;
            }
            else
            {
                if (_peak > _hold) _hold = _peak; else _hold *= 1 - 0.02 / HellMode.ColumnLen;
                v = Math.Min(1, _peak / _hold);
                Level = v;
            }
            _peak = 0;
            _col[_row++] = (byte)(v * 255);
            if (_row == HellMode.ColumnLen)
            {
                _row = 0;
                Column?.Invoke((byte[])_col.Clone());
            }
        }
    }
}
