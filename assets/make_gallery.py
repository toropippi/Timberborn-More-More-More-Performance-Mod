import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageChops

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "source_screenshot.png")
OUT = HERE
W, H = 1920, 1080
FT = r"C:\Windows\Fonts"
def f(n, s): return ImageFont.truetype(FT + "\\" + n, s)
BLACK="ariblk.ttf"; BOLD="segoeuib.ttf"; SB="seguisb.ttf"
GOLD=(255,196,60); LIME=(120,235,150); GREY=(150,155,150); WHITE=(248,248,248); DGREY=(120,125,120)

def out(d,xy,s,font,fill,ow=6,oc=(0,0,0),anchor="la"):
    x,y=xy
    for dx in range(-ow,ow+1):
        for dy in range(-ow,ow+1):
            if dx*dx+dy*dy<=ow*ow: d.text((x+dx,y+dy),s,font=font,fill=oc,anchor=anchor)
    d.text((x,y),s,font=font,fill=fill,anchor=anchor)

def base_bg(dimA):
    im=Image.open(SRC).convert("RGB")
    cw,ch=im.size; th=int(cw/(16/9))
    im=im.crop((0,0,cw,th)).resize((W,H),Image.LANCZOS).convert("RGBA")
    return Image.alpha_composite(im,Image.new("RGBA",(W,H),(0,0,0,dimA)))

def bar(d, x, y, w, h, fill, label, value, valcol):
    d.rounded_rectangle((x,y,x+w,y+h), radius=h//2, fill=fill)
    # label (left of bar)
    out(d,(x-30, y+h//2), label, f(BLACK,54), WHITE, ow=5, anchor="rm")
    # value (right end, inside/after)
    out(d,(x+w+28, y+h//2), value, f(BLACK,58), valcol, ow=5, anchor="lm")

def make():
    bg=base_bg(165); d=ImageDraw.Draw(bg)
    # headline
    out(d,(W//2,70),"SIMULATION  ~1.5x  FASTER", f(BLACK,110), GOLD, ow=8, anchor="ma")
    out(d,(W//2,215),"on big late-game colonies", f(SB,52), WHITE, ow=4, anchor="ma")

    # bars: same scale, length = speed. vanilla 18, T3MP 27 ticks/s.
    x0=560; maxw=1000; scale=maxw/27.0
    bh=110
    bar(d, x0, 360, int(18*scale), bh, DGREY+(255,), "Vanilla",  "18 ticks/s", GREY)
    bar(d, x0, 560, int(27*scale), bh, LIME+(255,),  "T3MP",     "27 ticks/s", LIME)

    # honest footer
    out(d,(W//2,H-150),"Measured per in-game day on a large colony (high speed).",
        f(SB,46), WHITE, ow=4, anchor="ma")
    out(d,(W//2,H-88),"You get the exact same colony as vanilla - nothing skipped.",
        f(SB,42), GREY, ow=3, anchor="ma")
    bg.convert("RGB").save(os.path.join(OUT,"gallery_faster.png"))
    print("done: assets/gallery_faster.png")

make()
