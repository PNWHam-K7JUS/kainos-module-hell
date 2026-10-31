/*  HellModes.cs

The Hellschreiber modes, timed as fldigi times them so they work with it: each character a run of columns of 14
pixels, sent bottom to top. Feld Hell, Slow Hell, Hell x5 and Hell x9 key a single tone on and off; FSK-Hell 245,
FSK-Hell 105 and Hell 80 shift between two tones (black the lower).

This file is part of the Kainos Hell module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

namespace Kainos.Hell
{
    internal sealed class HellMode
    {
        public readonly string Name;
        public readonly string Submode;         // ADIF's (MODE HELL); none for Feld Hell
        public readonly double ColumnRate;      // columns a second
        public readonly bool Fsk;
        public readonly double Shift;           // FSK: the two tones' spacing (Hz)
        public readonly double FilterBw;        // the receive filter's width (Hz)
        public readonly bool Shaped;            // raised-cosine edges on the pixels (Feld and Slow Hell)

        public const int ColumnLen = 14;

        private HellMode(string name, string submode, double columnRate, bool fsk, double shift, double filterBw, bool shaped)
        {
            Name = name; Submode = submode; ColumnRate = columnRate; Fsk = fsk; Shift = shift; FilterBw = filterBw; Shaped = shaped;
        }

        public double PixelRate { get { return ColumnLen * ColumnRate; } }

        // how wide the signal is (Hz), for the waterfall's marker
        public double Bandwidth { get { return Fsk ? Shift + PixelRate : PixelRate; } }

        public static readonly HellMode[] All =
        {
            new HellMode("Feld Hell", "", 17.5, false, 0, 295, true),
            new HellMode("Slow Hell", "SLOWHELL", 2.1875, false, 0, 35, true),
            new HellMode("Hell x5", "HELLX5", 87.5, false, 0, 1470, false),
            new HellMode("Hell x9", "HELLX9", 157.5, false, 0, 2645, false),
            new HellMode("FSK-Hell 245", "FSKH245", 17.5, true, 122.5, 490, false),
            new HellMode("FSK-Hell 105", "FSKH105", 17.5, true, 55, 220, false),
            new HellMode("Hell 80", "HELL80", 35, true, 300, 1200, false),
        };

        public static HellMode ByName(string name)
        {
            foreach (HellMode m in All) if (string.Equals(m.Name, name, System.StringComparison.OrdinalIgnoreCase)) return m;
            return All[0];
        }

        public override string ToString() { return Name; }
    }
}
