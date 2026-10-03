import ctypes, ctypes.wintypes as wt, os, time, sys
try: ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception: ctypes.windll.user32.SetProcessDPIAware()
from PIL import Image, ImageGrab
u=ctypes.windll.user32
DOCS=os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),'docs')
PID=int(open(os.path.join(DOCS,'_pid.txt')).read().strip())
res=[]
@ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
def cb(h,l):
    p=wt.DWORD(); u.GetWindowThreadProcessId(h,ctypes.byref(p))
    if p.value==PID and u.IsWindowVisible(h):
        n=u.GetWindowTextLengthW(h)
        if n>0:
            b=ctypes.create_unicode_buffer(n+1); u.GetWindowTextW(h,b,n+1)
            if '生态箱' in b.value: res.append(h)
    return True
u.EnumWindows(cb,0); hwnd=res[0]
class R(ctypes.Structure): _fields_=[('l',ctypes.c_long),('t',ctypes.c_long),('r',ctypes.c_long),('b',ctypes.c_long)]
r=R(); u.GetWindowRect(hwnd,ctypes.byref(r))
L,T=r.l,r.t
u.SetForegroundWindow(hwnd); time.sleep(0.6)
def click(x,y,p=0.5):
    u.SetCursorPos(L+x,T+y); time.sleep(0.15)
    u.mouse_event(0x0002,0,0,0,0); time.sleep(0.05); u.mouse_event(0x0004,0,0,0,0); time.sleep(p)
def drag(pts,p=0.05):
    u.SetCursorPos(L+pts[0][0],T+pts[0][1]); time.sleep(0.15)
    u.mouse_event(0x0002,0,0,0,0); time.sleep(0.1)
    for x,y in pts[1:]:
        u.SetCursorPos(L+x,T+y); time.sleep(p)
    time.sleep(0.1); u.mouse_event(0x0004,0,0,0,0); time.sleep(0.5)
def grab(name):
    img=ImageGrab.grab(bbox=(L,T,r.r,r.b)); img.save(os.path.join(DOCS,f'{name}.png')); return img
def avg(img,box):
    c=img.crop(box).convert('RGB'); px=list(c.getdata()); n=len(px)
    return tuple(sum(p[i] for p in px)//n for i in range(3))
click(1186,94,1.0)               # 地形 标签
before=grab('_terr_before')
click(1200,190,0.7)              # 打开地形编辑
click(1200,359,0.5)              # 沃土 笔刷
drag([(120+i*38,300) for i in range(16)])
after=grab('_terr_after')
b=avg(before,(130,292,660,308)); a=avg(after,(130,292,660,308))
print('绘制区平均色  前=%s  后=%s' % (b,a))
delta=(a[1]-b[1])
print('绿通道变化 = %+d' % delta)
ok1 = delta>8
print('==> 地形确实被改动了' if ok1 else '==> 地形没有变化')
click(1042,226,0.7)              # 撤销
undo=grab('_terr_undo')
c=avg(undo,(130,292,660,308))
print('撤销后平均色 = %s   (绘制前 %s)' % (c,b))
ok2 = abs(c[1]-b[1])<=6
print('==> 撤销成功，已还原' if ok2 else '==> 撤销没有还原')
sys.exit(0 if (ok1 and ok2) else 2)
