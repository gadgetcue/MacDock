"""MacDock Instagram carousel generator (1080x1350 PNGs).
Usage: python make_carousel.py [docs_dir] [fonts_dir] [out_dir]  (defaults assume repo layout)"""
import sys, os
from PIL import Image, ImageDraw, ImageFont, ImageFilter
here = os.path.dirname(os.path.abspath(__file__))
DOCS = os.path.join(here,'shots')
FONTS = os.path.join(here,'..','mac-dock','fonts')
OUT = here
W, H = 1080, 1350
BG=FG=MUTED=A1=A2=B1=B2=MIX=CHIP=LINE=SH=None
THEMES = {
 'dark': dict(BG=(10,10,14), FG=(245,245,247), MUTED=(160,160,172), A1=(244,187,63), A2=(200,50,35),
              B1=(110,55,20), B2=(95,25,22), MIX=0.55, CHIP=(30,30,38), LINE=(70,70,82), SH=200),
 'light': dict(BG=(247,245,241), FG=(20,20,26), MUTED=(98,98,110), A1=(236,140,20), A2=(196,45,35),
              B1=(255,214,160), B2=(255,185,170), MIX=0.75, CHIP=(255,255,255), LINE=(205,203,198), SH=70),
}
set_theme = None
def _st(n):
    global BG,FG,MUTED,A1,A2,B1,B2,MIX,CHIP,LINE,SH
    t=THEMES[n]; BG,FG,MUTED,A1,A2,B1,B2,MIX,CHIP,LINE,SH=(t[k] for k in 'BG FG MUTED A1 A2 B1 B2 MIX CHIP LINE SH'.split())
def F(w, s): return ImageFont.truetype(os.path.join(FONTS, f'Inter-{w}.ttf'), s)
def img(n): return Image.open(os.path.join(DOCS, n)).convert('RGB')

def base(i, n):
    im = Image.new('RGB', (W, H), BG)
    g = Image.new('RGB', (W, H), BG); d = ImageDraw.Draw(g)
    d.ellipse((-300, -400, 800, 500), fill=B1)
    d.ellipse((500, 900, 1500, 1700), fill=B2)
    g = g.filter(ImageFilter.GaussianBlur(220))
    im = Image.blend(im, g, MIX)
    d = ImageDraw.Draw(im)
    d.text((72, 70), "MacDock", font=F('Bold', 30), fill=FG)
    d.text((72+int(d.textlength("MacDock", font=F("Bold", 30)))+18, 74), "@gadgetcue", font=F("Medium", 26), fill=MUTED)
    d.text((W - 72, 74), f"{i}/{n}", font=F('Medium', 26), fill=MUTED, anchor='ra')
    return im, d

def grad_text(im, xy, text, font):
    m = Image.new('L', im.size, 0); ImageDraw.Draw(m).text(xy, text, font=font, fill=255)
    bb = m.getbbox() or (0, 0, 1, 1)
    gr = Image.new('RGB', im.size); gd = ImageDraw.Draw(gr)
    for x in range(bb[0], bb[2] + 1):
        t = (x - bb[0]) / max(1, bb[2] - bb[0])
        gd.line([(x, 0), (x, im.size[1])], fill=tuple(int(A1[k] + (A2[k] - A1[k]) * t) for k in range(3)))
    im.paste(gr, mask=m)

def title(im, d, y, lines, size=76, grad_last=True):
    f = F('Bold', size)
    for k, l in enumerate(lines):
        if grad_last and k == len(lines) - 1: grad_text(im, (72, y), l, f)
        else: d.text((72, y), l, font=f, fill=FG)
        y += int(size * 1.15)
    return y

def frame(im, shot, x, y, w, r=28):
    h = int(shot.height * w / shot.width)
    s = shot.resize((w, h), Image.LANCZOS)
    sh = Image.new('RGBA', im.size, (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle((x, y + 22, x + w, y + h + 22), r, fill=(0, 0, 0, SH))
    sh = sh.filter(ImageFilter.GaussianBlur(30))
    im.paste(sh, (0, 0), sh)
    m = Image.new('L', (w, h), 0); ImageDraw.Draw(m).rounded_rectangle((0, 0, w - 1, h - 1), r, fill=255)
    im.paste(s, (x, y), m)
    ImageDraw.Draw(im).rounded_rectangle((x, y, x + w - 1, y + h - 1), r, outline=LINE, width=2)
    return y + h

def sub(d, y, text, size=34, fill=None, x=72, maxw=936):
    fill = fill or MUTED
    f = F('Regular', size); line = ''
    for wd in text.split():
        t = (line + ' ' + wd).strip()
        if d.textlength(t, font=f) > maxw: d.text((x, y), line, font=f, fill=fill); y += int(size * 1.4); line = wd
        else: line = t
    d.text((x, y), line, font=f, fill=fill); return y + int(size * 1.4)

def chips(d, x, y, items, size=28):
    f = F('SemiBold', size); cx = x
    for t in items:
        w = int(d.textlength(t, font=f)) + 44
        if cx + w > W - 72: cx = x; y += 70
        d.rounded_rectangle((cx, y, cx + w, y + 54), 27, fill=CHIP, outline=LINE, width=2)
        d.text((cx + 22, y + 27), t, font=f, fill=FG, anchor='lm'); cx += w + 14
    return y + 70

def featlist(im, d, y, items, size=50):
    for head, desc in items:
        d.ellipse((72, y + 12, 96, y + 36), fill=A1)
        d.text((120, y), head, font=F('Bold', size), fill=FG)
        y = sub(d, y + int(size * 1.3), desc, 34, MUTED, 120, 880) + 50
    return y

set_theme = _st


def monitor(im,shot,x,y,w):
    bz=16; sw=w-2*bz; sh_=int(shot.height*sw/shot.width); h=sh_+2*bz
    sh=Image.new('RGBA',im.size,(0,0,0,0)); ImageDraw.Draw(sh).rounded_rectangle((x,y+24,x+w,y+h+24),30,fill=(0,0,0,SH))
    sh=sh.filter(ImageFilter.GaussianBlur(30)); im.paste(sh,(0,0),sh)
    d=ImageDraw.Draw(im)
    d.rounded_rectangle((x,y,x+w,y+h),30,fill=(24,24,28),outline=LINE,width=2)
    s=shot.resize((sw,sh_),Image.LANCZOS); m=Image.new('L',(sw,sh_),0); ImageDraw.Draw(m).rounded_rectangle((0,0,sw-1,sh_-1),16,fill=255)
    im.paste(s,(x+bz,y+bz),m)
    cx=x+w//2
    d.polygon([(cx-60,y+h),(cx+60,y+h),(cx+90,y+h+46),(cx-90,y+h+46)],fill=(58,58,64))
    d.rounded_rectangle((cx-170,y+h+44,cx+170,y+h+58),7,fill=(74,74,82))
    return y+h+58
def winframe(im,shot,x,y,w,r=26):
    bar=44; h=int(shot.height*w/shot.width)
    sh=Image.new('RGBA',im.size,(0,0,0,0)); ImageDraw.Draw(sh).rounded_rectangle((x,y+22,x+w,y+h+bar+22),r,fill=(0,0,0,SH))
    sh=sh.filter(ImageFilter.GaussianBlur(30)); im.paste(sh,(0,0),sh)
    d=ImageDraw.Draw(im); d.rounded_rectangle((x,y,x+w,y+h+bar),r,fill=(30,30,36),outline=LINE,width=2)
    for i,c in enumerate([(255,95,87),(254,188,46),(40,200,64)]): d.ellipse((x+22+i*30,y+14,x+38+i*30,y+30),fill=c)
    s=shot.resize((w-4,h),Image.LANCZOS); m=Image.new('L',(w-4,h),0)
    ImageDraw.Draw(m).rounded_rectangle((0,-r,w-5,h-1),r-2,fill=255)
    im.paste(s,(x+2,y+bar),m); return y+h+bar
def settings_img():
    return img('settings-icons.png').crop((318,30,1294,590))
def build(theme):
    set_theme(theme); N=7; slides=[]
    im,d=base(1,N)
    y=title(im,d,150,["Windows 11","But make it","Mac"],112)
    sub(d,y+10,"MacDock v2.1: a macOS-style desktop shell for Windows. Free & open source.",34)
    monitor(im,img('desktop.png'),70,y+140,940)
    slides.append(im)
    im,d=base(2,N)
    y=title(im,d,150,["A dock that","feels right."],84)
    y=sub(d,y+24,"Smooth magnification, labels, running indicators and a working Recycle Bin.",32)
    y2=frame(im,img('dock-magnify.png').crop((480,925,1440,1078)),60,y+50,960)
    chips(d,72,y2+60,["Magnification","Genie minimize","Drag & drop","Recycle Bin"])
    slides.append(im)
    im,d=base(3,N)
    y=title(im,d,150,["Launchpad,","all your apps."],84)
    y=sub(d,y+24,"Category pills, folders and every app in one place. Drag any app onto the dock.",32)
    winframe(im,img('launchpad.png').crop((400,70,1520,950)),60,y+30,960)
    slides.append(im)
    im,d=base(4,N)
    y=title(im,d,150,["Folder fans,","right on top."],84)
    y=sub(d,y+24,"Click a folder to fan it out. Menus now open above the fan, and the folder-click freeze is gone.",32)
    winframe(im,img('folder-fan.png').crop((1000,320,1700,1075)),(W-700)//2,y+30,700)
    slides.append(im)
    im,d=base(5,N)
    y=title(im,d,150,["Control Center,","with LocalSend."],84)
    y=sub(d,y+24,"Send with LocalSend from Explorer or the dock, rename tiles, and keep every control one click away.",32)
    cc=img('control-center.png').crop((1495,30,1850,665)); cw=440
    winframe(im,cc,(W-cw)//2,y+30,cw)
    slides.append(im)
    im,d=base(6,N)
    y=title(im,d,150,["Make it","yours."],84)
    y=sub(d,y+24,"New in 2.1: an icon master switch and your own icon packs, from a folder or a .zip.",32)
    sw=900
    winframe(im,settings_img(),(W-sw)//2,y+20,sw)
    slides.append(im)
    im,d=base(7,N)
    y=title(im,d,260,["Get MacDock","v2.1."],120)
    y=sub(d,y+10,"Free and open source. Update from the Apple menu.",36,MUTED)
    y+=30
    d.rounded_rectangle((72,y,W-72,y+120),60,fill=CHIP,outline=LINE,width=2)
    d.text((W//2,y+60),"github.com/gadgetcue/MacDock",font=F('Bold',42),fill=FG,anchor='mm')
    sub(d,y+170,"Star the repo and follow for more.",34,A1)
    d.rounded_rectangle((72,y+260,W-72,y+420),40,fill=A1)
    d.text((W//2,y+310),"Want the link?",font=F('Medium',34),fill=(20,20,26),anchor='mm')
    d.text((W//2,y+368),"Comment “Mac” and I'll DM it to you",font=F('Bold',46),fill=(20,20,26),anchor='mm')
    slides.append(im)
    od=os.path.join(OUT,theme); os.makedirs(od,exist_ok=True)
    for i,s in enumerate(slides,1): s.save(os.path.join(od,f'slide-{i}.png'))
    print(theme,len(slides))
for t in THEMES: build(t)
