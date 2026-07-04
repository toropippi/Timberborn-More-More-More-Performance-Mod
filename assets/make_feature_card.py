# -*- coding: utf-8 -*-
# Gallery card: "what the mod does" in 3 rows, player language only.
# Same dark theme / palette as the approved graphs.
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
W, H = 1920, 1080
BG = (16, 20, 24)        # #101418
FG = (232, 228, 216)     # #e8e4d8
MUTED = (154, 160, 168)  # #9aa0a8
GOLD = (255, 210, 77)    # #ffd24d
GREEN = (77, 208, 106)   # #4dd06a
GREY = (138, 143, 152)   # #8a8f98
DARKTXT = (12, 18, 16)

FT = r"C:\Windows\Fonts"
def f(n, s): return ImageFont.truetype(FT + "\\" + n, s)
BLACK = "ariblk.ttf"; REG = "segoeui.ttf"; SB = "seguisb.ttf"

im = Image.new("RGB", (W, H), BG)
d = ImageDraw.Draw(im)

# title
d.text((80, 56), "What this mod does", font=f(BLACK, 78), fill=FG)
d.text((84, 172), "T3MP  -  More More More Performance!", font=f(SB, 40), fill=MUTED)

rows = [
    (GREEN, "1", "Faster game, always on",
     "The simulation runs up to ~1.5x faster from the moment your save loads.\nNo hotkeys, no setup, no changes to gameplay."),
    (GOLD, "2", "Speed buttons give FULL speed",
     "As your colony grows, vanilla secretly slows the speed buttons down\n(max button: only x3.4 instead of x7 at 200+ beavers). T3MP removes that."),
    (GREY, "3", "Optional turbo: Shift+P",
     "Skips animations while you fast-forward through the night or a long wait.\nUp to ~2.4x vanilla, measured. Press again to go back to normal."),
]

y = 270
row_h = 250
for color, num, head, sub in rows:
    # number badge
    bx, by, br = 150, y + 78, 62
    d.ellipse((bx - br, by - br, bx + br, by + br), fill=color)
    nb = d.textbbox((0, 0), num, font=f(BLACK, 78))
    d.text((bx - (nb[2] - nb[0]) / 2 - nb[0], by - (nb[3] - nb[1]) / 2 - nb[1]),
           num, font=f(BLACK, 78), fill=DARKTXT)
    # heading + sub
    d.text((270, y), head, font=f(BLACK, 58), fill=color)
    d.multiline_text((272, y + 92), sub, font=f(REG, 36), fill=FG, spacing=14)
    y += row_h

# speed-button triangles decoration next to row 2 heading
hb = d.textbbox((270, 270 + row_h), rows[1][2], font=f(BLACK, 58))
tx = hb[2] + 40; ty = 270 + row_h + 16
for i in range(3):
    x0 = tx + i * 42
    d.polygon([(x0, ty), (x0, ty + 44), (x0 + 34, ty + 22)], fill=GOLD)

d.text((W - 80, H - 60), "numbers measured in-game on a large late-game colony",
       font=f(REG, 28), fill=MUTED, anchor="rs")

im.save(os.path.join(HERE, "feature_card.png"))
print("done: feature_card.png")
