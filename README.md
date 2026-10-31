# Kainos Hell

A Kainos module for Hellschreiber: **Feld Hell**, **Slow Hell**, **Hell x5**, **Hell x9**, **FSK-Hell 245**,
**FSK-Hell 105** and **Hell 80**, timed as fldigi does so it works with fldigi.

Install it in Kainos from **Setup > Modules**, then open it from the **Modes** menu (**Hell**).

## Receiving

Hell isn't decoded: the signal is drawn as it arrives, and you read it.

- **Click a signal on the waterfall.** Its text is drawn in strips, twice, one copy over the other. The sender's
  clock and yours never quite agree, so the text slides up or down, but one copy is always whole.
- **Slant**: if the text leans, click the side that makes it upright (50 ppm a click; right-click to reset).
- **Width** draws the text narrower or wider. The mouse wheel over the text scrolls back.
- **Rev** (FSK modes) swaps the tones for a station on the other sideband. LSB / DIGL is allowed for already.

## Sending

- Type in the box and press **TX** (Ctrl+T): the box goes out, then whatever you type, as you type it. **RX**
  (Ctrl+R) goes back to receive once everything typed has gone; **Esc** stops at once. A new line goes as a space.
- **F1 – F8** send the messages under them (right-click one to change it). They fill in `<MYCALL>`, `<MYNAME>`,
  `<MYGRID>`, `<CALL>`, `<NAME>`, `<RST>` and `<MODE>`; `<RX>` goes back to receive when it's reached.
- What you send is drawn in the strips too, in red.
- **Tune** sends a steady tone for up to 30 s. Set the transmit level (Options) and drive for full power with the
  ALC barely moving.

The font is Kainos's own 5 by 7 font, each row doubled to fill Hell's 14-pixel column.

## Logging

**Log QSO** writes an ADIF log (MODE HELL, with SUBMODE for the modes ADIF names: SLOWHELL, HELLX5, HELLX9, FSKH245,
FSKH105, HELL80) in the module's data folder, and tells logging programs as WSJT-X does (UDP "Logged ADIF" to
127.0.0.1:2237 by default).

## Building

`.\build.ps1` builds it, runs the self-test (every mode sent and received, clean and with noise, compared pixel by
pixel; the transmit path) and packages `dist\kainos-hell-<version>.zip`.

GPLv3 or later. Copyright (C) 2026 Justin Cron K7JUS.
