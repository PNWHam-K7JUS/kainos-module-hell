/*  HellTx.cs

The Hell transmitter, made a little at a time so text typed while sending goes straight out: three dots first, each
character's columns sent pixel by pixel from the bottom, blank columns while there's nothing to send, and when asked
to finish, three dots and a space. On-off modes key the tone, with 4 ms raised-cosine edges for Feld and Slow Hell (as
fldigi does: a pixel at Feld Hell's speed is hardly longer); FSK modes shift between two tones, black the lower,
without a break in phase.

This file is part of the Kainos Hell module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;

namespace Kainos.Hell
{
    internal sealed class HellTx
    {
        public readonly HellMode Mode;
        public readonly double Frequency;
        public readonly bool Reverse;
        private readonly int _rate;
        private readonly double _pixLen;            // samples a pixel
        private readonly double _edge;              // the raised-cosine edge (samples)
        private double _phase;

        private readonly Queue<char> _text = new Queue<char>();
        private readonly Queue<ushort> _cols = new Queue<ushort>();
        private int _preamble = 3, _postamble = -1;
        private bool _finishing;

        // the pixels: the one being sent, and its neighbours (for the edges)
        private int _prevPix, _curPix, _nextPix;
        private ushort _col;
        private int _row = HellMode.ColumnLen;       // the next pixel's row in _col
        private double _pixPos;
        private bool _started;

        public bool Done { get; private set; }
        public event Action<char> Sent;             // a character as its first column starts
        public event Action<ushort> ColumnSent;     // each column as it starts (for showing what's sent)
        private readonly Queue<object> _marks = new Queue<object>();     // a char before the columns it made

        public HellTx(HellMode mode, double frequency, bool reverse, int rate)
        {
            Mode = mode;
            Frequency = frequency;
            Reverse = reverse;
            _rate = rate;
            _pixLen = rate / mode.PixelRate;
            _edge = mode.Shaped ? 0.004 * rate : 0;
        }

        public void Add(string s) { lock (_text) foreach (char c in s) _text.Enqueue(c); }
        public int Queued { get { lock (_text) return _text.Count; } }
        public void Finish() { _finishing = true; }
        public bool Finishing { get { return _finishing; } }

        public int Generate(float[] dst, int n)
        {
            if (!_started) { _started = true; _curPix = nextPixel(); _nextPix = nextPixel(); }
            int o = 0;
            while (o < n && !Done)
            {
                if (_pixPos >= _pixLen)
                {
                    _pixPos -= _pixLen;
                    _prevPix = _curPix; _curPix = _nextPix;
                    if (_curPix < 0) { Done = true; break; }
                    _nextPix = nextPixel();
                }
                double f = Frequency, amp;
                if (Mode.Fsk)
                {
                    amp = _curPix < 0 ? 0 : 1;
                    f += (Reverse ? -1 : 1) * (_curPix == 1 ? -1 : 1) * Mode.Shift / 2;
                }
                else
                {
                    amp = _curPix == 1 ? 1 : 0;
                    if (amp > 0 && _edge > 0)
                    {
                        double t = _pixPos;
                        if (_prevPix != 1 && t < _edge) amp *= 0.5 * (1 - Math.Cos(Math.PI * t / _edge));
                        double tl = _pixLen - _pixPos;
                        if (_nextPix != 1 && tl < _edge) amp *= 0.5 * (1 - Math.Cos(Math.PI * tl / _edge));
                    }
                }
                dst[o++] = (float)(amp * Math.Sin(_phase));
                _phase += 2 * Math.PI * f / _rate;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                _pixPos += 1;
            }
            return o;
        }

        // the next pixel (1 black, 0 white), or -1 when the transmission's over
        private int nextPixel()
        {
            if (_row >= HellMode.ColumnLen)
            {
                if (!nextColumn()) return -1;
                _row = 0;
            }
            return (_col >> _row++) & 1;
        }

        private bool nextColumn()
        {
            while (_cols.Count == 0)
            {
                if (_preamble > 0) { _preamble--; addChar('.'); continue; }
                char c;
                bool got = false;
                lock (_text) { c = _text.Count > 0 ? _text.Dequeue() : '\0'; got = c != '\0'; }
                if (got) { addChar(c); continue; }
                if (_finishing)
                {
                    if (_postamble < 0) _postamble = 4;
                    if (_postamble == 0) return false;
                    _postamble--;
                    addChar(_postamble == 0 ? ' ' : '.');
                    continue;
                }
                _cols.Enqueue(0); _cols.Enqueue(0);         // idle
                _marks.Enqueue(null); _marks.Enqueue(null);
            }
            _col = _cols.Dequeue();
            object mark = _marks.Dequeue();
            if (mark is char) Sent?.Invoke((char)mark);
            ColumnSent?.Invoke(_col);
            return true;
        }

        private void addChar(char c)
        {
            List<ushort> cols = HellFont.Columns(c);
            if (c == '\b') { cols.Clear(); cols.Add(0); }      // can't take a column back: a gap instead
            for (int i = 0; i < cols.Count; i++) { _cols.Enqueue(cols[i]); _marks.Enqueue(i == 0 ? (object)c : null); }
        }
    }
}
