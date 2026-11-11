"""Deterministic, pixel-fitted application icon from the existing two-pane mark."""
from pathlib import Path
from io import BytesIO
import struct,json
from PIL import Image, ImageDraw, ImageFont
root=Path(__file__).resolve().parent
sizes=[16,20,24,28,32,40,48,56,64,72,80,96,128,192,256,384,512,1024]
# The SVG is the editable source; the renderer snaps small versions to pixels.
(root/'SyncSyncAV.svg').write_text('''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
<title>SyncSyncAV</title>
<rect x="6" y="6" width="244" height="244" rx="50" fill="#171a1e" stroke="#4b535c" stroke-width="4"/>
<rect x="50" y="70" width="56" height="116" rx="4" fill="#252a30" stroke="#e3e6e9" stroke-width="12"/>
<rect x="150" y="70" width="56" height="116" rx="4" fill="#252a30" stroke="#5db6c0" stroke-width="12"/>
<path d="M96 128H160" stroke="#171a1e" stroke-width="28"/>
<path d="M96 128H160" stroke="#e3e6e9" stroke-width="12" stroke-linecap="round"/>
</svg>''',encoding='utf-8')

def render(n):
 scale=8 if n<256 else 4
 im=Image.new('RGBA',(n*scale,n*scale),(0,0,0,0));d=ImageDraw.Draw(im)
 def snap(v):return round(v*n/256)
 def box(values):return tuple(v*scale for v in values)
 pad=max(.5,n/64);line=max(1,round(12*n/256));edge=max(.75,n/64)
 d.rounded_rectangle(box((pad,pad,n-pad,n-pad)),radius=n*.203125*scale,fill='#171a1e',outline='#4b535c',width=round(edge*scale))
 top=snap(64);bottom=n-top;left=snap(44);midleft=snap(112);right=n-left;midright=n-midleft
 for x0,x1,color in ((left,midleft,'#e3e6e9'),(midright,right,'#5db6c0')):
  d.rounded_rectangle(box((x0,top,x1,bottom)),radius=max(1,round(n*10/256))*scale,fill='#252a30',outline=color,width=line*scale)
 y=n/2;start=snap(96);end=n-start
 d.line(box((start,y,end,y)),fill='#171a1e',width=(line+max(1,round(n*16/256)))*scale)
 d.line(box((start,y,end,y)),fill='#e3e6e9',width=line*scale)
 rr=line*scale/2
 for x in (start,end):d.ellipse((x*scale-rr,y*scale-rr,x*scale+rr,y*scale+rr),fill='#e3e6e9')
 return im.resize((n,n),Image.Resampling.LANCZOS)

frames={n:render(n) for n in sizes}
for n,im in frames.items():im.save(root/'icons'/f'SyncSyncAV-{n}.png')
# ICO entries use exact supplied images (including the pixel-fitted small sizes).
entries=[];payload=[];offset=6+16*len([n for n in sizes if n<=256])
for n,im in frames.items():
 if n>256:continue
 buf=BytesIO();im.save(buf,format='PNG');data=buf.getvalue()
 entries.append(struct.pack('<BBBBHHII',n if n<256 else 0,n if n<256 else 0,0,0,1,32,len(data),offset));payload.append(data);offset+=len(data)
(root/'SyncSyncAV.ico').write_bytes(struct.pack('<HHH',0,1,len(entries))+b''.join(entries)+b''.join(payload))
# Inspection sheet shows each small size unscaled, and larger sizes separately.
preview=Image.new('RGB',(900,470),'#0f1215');d=ImageDraw.Draw(preview);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',14)
x=24
for n in [16,20,24,28,32,40,48,56,64,72,80,96]:
 preview.paste(frames[n],(x,36+(96-n)//2),frames[n]);d.text((x,145),str(n),font=font,fill='#a8b0b8');x+=n+22
for n,x in [(128,30),(192,220),(256,490)]:
 preview.paste(frames[n],(x,190),frames[n]);d.text((x,450),str(n),font=font,fill='#a8b0b8')
preview.save(root/'icon-sizes.png')
print('ICO',len(entries),'frames;',sizes)
