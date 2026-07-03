import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageChops, ImageOps

# Self-locating: run from anywhere; reads source_screenshot.png and writes
# thumbnail.png next to this script. Regenerates the mod thumbnail.
HERE=os.path.dirname(os.path.abspath(__file__))
SRC=os.path.join(HERE,"source_screenshot.png")
OUT=HERE
W,H=1920,1080
FT=r"C:\Windows\Fonts"
def f(n,s): return ImageFont.truetype(FT+"\\"+n,s)
BLACK="ariblk.ttf"; BOLD="segoeuib.ttf"
GOLD_T=(255,228,150); GOLD_B=(232,150,28)      # metallic gold gradient
WHT_T=(255,255,255); WHT_B=(198,205,216)        # silver-white gradient
GLOW_GOLD=(255,180,60); GLOW_WHITE=(255,250,235)
LIME=(150,255,178)

def base_bg(dimA):
    im=Image.open(SRC).convert("RGB")
    cw,ch=im.size; th=int(cw/(16/9))
    im=im.crop((0,0,cw,th)).resize((W,H),Image.LANCZOS).convert("RGBA")
    return Image.alpha_composite(im,Image.new("RGBA",(W,H),(0,0,0,dimA)))

def vignette(im, strength=0.5):
    m=Image.new("L",(W,H),0)
    ImageDraw.Draw(m).ellipse((int(-W*0.28),int(-H*0.30),int(W*1.28),int(H*1.30)),fill=255)
    m=m.filter(ImageFilter.GaussianBlur(220))
    dark=Image.new("RGBA",(W,H),(0,0,0,0)); dark.putalpha(ImageOps.invert(m).point(lambda p:int(p*strength)))
    return Image.alpha_composite(im,dark)

def vgrad_col(y0,y1,ct,cb):
    col=Image.new("RGB",(1,H))
    for y in range(H):
        t=min(1.0,max(0.0,(y-y0)/max(1,(y1-y0))))
        col.putpixel((0,y),tuple(int(ct[i]+(cb[i]-ct[i])*t) for i in range(3)))
    return col.resize((W,H))

def premium_text(bg, pos, text, font, ct, cb, glow, ow=4, gblur=20, shoff=9):
    d0=ImageDraw.Draw(bg)
    x0,y0,x1,y1=d0.textbbox(pos,text,font=font)
    # glyph mask
    mask=Image.new("L",(W,H),0); ImageDraw.Draw(mask).text(pos,text,font=font,fill=255)
    # outline (ring) mask
    outl=Image.new("L",(W,H),0); od=ImageDraw.Draw(outl)
    for dx in range(-ow,ow+1):
        for dy in range(-ow,ow+1):
            if dx*dx+dy*dy<=ow*ow: od.text((pos[0]+dx,pos[1]+dy),text,font=font,fill=255)
    # 1) drop shadow (blurred outline, offset down)
    sh=outl.filter(ImageFilter.GaussianBlur(11))
    sha=Image.new("RGBA",(W,H),(0,0,0,0)); shl=Image.new("RGBA",(W,H),(0,0,0,190)); shl.putalpha(sh)
    shl=ImageChops.offset(shl,4,shoff)
    bg=Image.alpha_composite(bg,shl)
    # 2) bloom halo (dramatic: tight bright screened x2 + wide soft halo)
    for blur,times in [(gblur,2),(int(gblur*2.6),1)]:
        bl=Image.new("RGBA",(W,H),(0,0,0,0)); bl.paste(glow+(255,),(0,0),mask)
        bl=bl.filter(ImageFilter.GaussianBlur(blur)); blr=bl.convert("RGB")
        for _ in range(times):
            bg=ImageChops.screen(bg.convert("RGB"),blr).convert("RGBA")
    # 3) dark outline
    ol=Image.new("RGBA",(W,H),(0,0,0,0)); ol.paste((18,13,4,255),(0,0),outl)
    bg=Image.alpha_composite(bg,ol)
    # 4) gradient-filled glyphs + soft top highlight
    grad=vgrad_col(y0,y1,ct,cb); tl=Image.new("RGBA",(W,H),(0,0,0,0)); tl.paste(grad,(0,0),mask)
    bg=Image.alpha_composite(bg,tl)
    return bg

def meter_glow(bg, box):
    x0,y0,x1,y1=box
    g=Image.new("RGBA",(W,H),(0,0,0,0))
    ImageDraw.Draw(g).rounded_rectangle((x0-6,y0-6,x1+6,y1+6),radius=32,outline=LIME+(255,),width=20)
    g=g.filter(ImageFilter.GaussianBlur(30)); gr=g.convert("RGB")
    bg=ImageChops.screen(bg.convert("RGB"),gr).convert("RGBA")
    return ImageChops.screen(bg.convert("RGB"),gr).convert("RGBA")

def real_meter_zoom(scale=9, darken=90):
    im=Image.open(SRC).convert("RGB")
    c=im.crop((2000,1456,2181,1502))
    z=c.resize((c.width*scale,c.height*scale),Image.LANCZOS).convert("RGBA")
    z=Image.alpha_composite(z,Image.new("RGBA",z.size,(0,10,6,darken)))
    return z.filter(ImageFilter.UnsharpMask(radius=2,percent=150,threshold=2))

def make():
    bg=base_bg(158)
    bg=vignette(bg,0.78)
    # headline with bloom (dramatic)
    bg=premium_text(bg,(60,70),"PERFORMANCE",f(BLACK,146),GOLD_T,GOLD_B,GLOW_GOLD,ow=4,gblur=34)
    bg=premium_text(bg,(66,252),"IMPROVED",f(BLACK,112),WHT_T,WHT_B,GLOW_WHITE,ow=4,gblur=24)
    xx=66+ImageDraw.Draw(bg).textlength("IMPROVED  ",font=f(BLACK,112))
    bg=premium_text(bg,(int(xx),246),"x1.7",f(BLACK,150),GOLD_T,GOLD_B,GLOW_GOLD,ow=4,gblur=34)
    # meter panel (real capture, 9x) with soft lime glow
    z=real_meter_zoom(); pad=34
    pw,ph=z.width+pad*2,z.height+pad*2; px=W//2-pw//2; py=560
    bg=meter_glow(bg,(px,py,px+pw,py+ph))
    d=ImageDraw.Draw(bg)
    d.rounded_rectangle((px,py,px+pw,py+ph),radius=26,fill=(6,10,8,235),outline=LIME+(255,),width=5)
    for dx in range(-4,5):
        for dy in range(-4,5):
            if dx*dx+dy*dy<=16: d.text((px+18+dx,py-52+dy),"LIVE  actual in-game speed meter",font=f(BLACK,32),fill=(0,0,0))
    d.text((px+18,py-52),"LIVE  actual in-game speed meter",font=f(BLACK,32),fill=(255,196,60))
    bg.alpha_composite(z,(px+pad,py+pad))
    bg.convert("RGB").save(os.path.join(OUT,"thumbnail.png"))

make(); print("done: thumbnail.png")
