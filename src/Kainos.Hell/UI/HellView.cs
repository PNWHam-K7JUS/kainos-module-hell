/*  HellView.cs

What Hell is read from: the received columns drawn side by side in strips, a strip at a time down the window like
lines of text. Each column is drawn twice, stacked (the column before under the one now, as fldigi and the old
machines print it): the sender's clock and ours never quite agree, so the text slides up or down, and one of the two
copies is always whole. What's sent is drawn too, in red.

This file is part of the Kainos Hell module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Hell.UI
{
    internal class HellView : Control
    {
        private readonly IKainosTheme _t;
        private const int H = 2 * HellMode.ColumnLen;        // a strip's height in pixels: two copies
        private const int MaxStrips = 60;
        private readonly List<Bitmap> _strips = new List<Bitmap>();
        private int _x;                                     // columns used in the newest strip
        private byte[] _prev = new byte[HellMode.ColumnLen];
        private bool _prevTx;
        private int _scroll;                                // strips scrolled back from the newest

        public int ColumnWidth = 2;                         // screen pixels a column (design px)
        public int PixelHeight = 2;                         // screen pixels a pixel (design px)

        public HellView(IKainosTheme t)
        {
            _t = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Panel;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { foreach (Bitmap b in _strips) b.Dispose(); _strips.Clear(); }
            base.Dispose(disposing);
        }

        private int colW { get { return Math.Max(1, _t.S(ColumnWidth)); } }
        private int pixH { get { return Math.Max(1, _t.S(PixelHeight)); } }
        private int stripH { get { return H * pixH + _t.S(6); } }
        private int stripCols { get { return Math.Max(20, (Width - _t.S(8)) / colW); } }

        public void Clear()
        {
            foreach (Bitmap b in _strips) b.Dispose();
            _strips.Clear();
            _x = 0;
            Invalidate();
        }

        // a column: ColumnLen pixels, bottom first, 0 white - 255 black
        public void Add(byte[] px, bool tx)
        {
            if (_strips.Count == 0 || _x >= _strips[_strips.Count - 1].Width)
            {
                Bitmap b = new Bitmap(stripCols, H, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (Graphics g = Graphics.FromImage(b)) g.Clear(_t.Panel);
                _strips.Add(b);
                _x = 0;
                while (_strips.Count > MaxStrips) { _strips[0].Dispose(); _strips.RemoveAt(0); }
            }
            Bitmap s = _strips[_strips.Count - 1];
            // the strip's column, top to bottom: this column over the one before
            for (int i = 0; i < HellMode.ColumnLen; i++)
            {
                s.SetPixel(_x, H - 1 - (HellMode.ColumnLen + i), colour(px[i], tx));
                s.SetPixel(_x, H - 1 - i, colour(_prev[i], _prevTx));
            }
            _x++;
            _prev = px;
            _prevTx = tx;
        }

        private Color colour(byte v, bool tx)
        {
            Color ink = tx ? _t.Tx : _t.Text, bg = _t.Panel;
            float f = v / 255f;
            f = f * f * (3 - 2 * f);            // a little more contrast
            return Color.FromArgb((int)(bg.R + (ink.R - bg.R) * f), (int)(bg.G + (ink.G - bg.G) * f), (int)(bg.B + (ink.B - bg.B) * f));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_t.Panel);
            if (_strips.Count == 0)
            {
                using (Font f = _t.Font(_t.S(12), false))
                using (Brush b = new SolidBrush(_t.Faint))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("Click a Hell signal on the waterfall: its text is drawn here, twice, one copy over the other.\nIf it leans, straighten it with the Slant buttons.", f, b, ClientRectangle, sf);
                return;
            }
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            int sh = stripH, y = Height - sh, last = _strips.Count - 1 - _scroll;
            for (int i = last; i >= 0 && y > -sh; i--, y -= sh)
            {
                Bitmap b = _strips[i];
                int cols = i == _strips.Count - 1 ? _x : b.Width;
                if (cols <= 0) continue;
                g.DrawImage(b, new Rectangle(_t.S(4), y + _t.S(3), cols * colW, H * pixH), new Rectangle(0, 0, cols, H), GraphicsUnit.Pixel);
            }
            if (_scroll > 0)
                using (Font f = _t.Font(_t.S(10), true))
                using (Brush b = new SolidBrush(_t.Gold))
                    g.DrawString("scrolled back: wheel down to return", f, b, _t.S(6), _t.S(4));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scroll = Math.Max(0, Math.Min(_strips.Count - 1, _scroll + (e.Delta > 0 ? 1 : -1)));
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }
    }
}
