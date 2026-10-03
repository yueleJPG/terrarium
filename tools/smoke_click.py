"""自动化冒烟测试：找到生态箱里的一只生物，点它，验证详情/规则面板是否正确填充。

为什么需要它：布局和渲染问题看截图能发现，但"点击选中 -> 面板联动"这条链路
只有真的点下去才知道通不通。盲猜坐标点不中（生物一直在动），
所以这里先用图像分析找出生物所在的像素，再精确点击。
"""
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

# 必须在任何窗口操作之前声明本进程为 DPI 感知，否则截屏和鼠标坐标都会被虚拟化
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PROCESS_PER_MONITOR_DPI_AWARE
except Exception:
    ctypes.windll.user32.SetProcessDPIAware()

from PIL import ImageGrab  # noqa: E402

user32 = ctypes.windll.user32

DOCS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "docs")
PID_FILE = os.path.join(DOCS, "_pid.txt")
OUT = os.path.join(DOCS, "screenshot-06.png")


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def find_window(pid: int):
    result = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(hwnd, lparam):
        wpid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
        if wpid.value == pid and user32.IsWindowVisible(hwnd):
            length = user32.GetWindowTextLengthW(hwnd)
            if length > 0:
                buf = ctypes.create_unicode_buffer(length + 1)
                user32.GetWindowTextW(hwnd, buf, length + 1)
                if "生态箱" in buf.value:
                    result.append((hwnd, buf.value))
        return True

    user32.EnumWindows(cb, 0)
    return result[0] if result else (None, None)


def get_rect(hwnd):
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r


def click(x, y):
    user32.SetCursorPos(int(x), int(y))
    time.sleep(0.2)
    user32.mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
    time.sleep(0.06)
    user32.mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP


def press_space():
    """暂停 / 继续。

    关键一步：不暂停就去点生物是点不中的 —— 截图到点击之间隔了几百毫秒，
    2x 速度下生物已经跑出十几个像素，远超点击判定半径。
    """
    user32.keybd_event(0x20, 0, 0, 0)
    time.sleep(0.05)
    user32.keybd_event(0x20, 0, 0x0002, 0)


def find_creature_pixel(img, box):
    """找一块"生物绿"像素。生物身体是高饱和的绿，地形绿要暗得多。"""
    px = img.load()
    x0, y0, x1, y1 = box
    # 从世界视图中心向外扩散扫描，避免点到边缘
    cx, cy = (x0 + x1) // 2, (y0 + y1) // 2
    best = None
    for radius in range(0, max(x1 - x0, y1 - y0) // 2, 3):
        for dx in range(-radius, radius + 1, 3):
            for dy in (-radius, radius):
                for (x, y) in ((cx + dx, cy + dy), (cx + dy, cy + dx)):
                    if not (x0 <= x < x1 and y0 <= y < y1):
                        continue
                    r, g, b = px[x, y][:3]
                    if g > 200 and g - r > 55 and g - b > 55:
                        return x, y
        if radius > 260:
            break
    return best


def main():
    pid = int(open(PID_FILE).read().strip())
    hwnd, title = find_window(pid)
    if not hwnd:
        print("找不到窗口")
        return 1
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.5)

    r = get_rect(hwnd)
    user32.ShowWindow(hwnd, 9)  # SW_RESTORE
    time.sleep(0.6)
    r = get_rect(hwnd)
    w, h = r.right - r.left, r.bottom - r.top
    print(f"窗口 {w}x{h} @ ({r.left},{r.top})  「{title}」")

    img = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom))
    img.save(OUT)
    print(f"截屏 {img.size} -> {OUT}")

    press_space()          # 暂停，让画面定格
    time.sleep(0.8)
    img = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom))
    img.save(OUT.replace("-06", "-06p"))

    # 世界视图大致在窗口左上区域
    box = (r.left + 40, r.top + 100, r.left + int(w * 0.62), r.top + int(h * 0.65))
    local = find_creature_pixel(img, (box[0] - r.left, box[1] - r.top,
                                      box[2] - r.left, box[3] - r.top))
    if not local:
        print("没找到生物像素")
        press_space()
        return 2

    sx, sy = r.left + local[0], r.top + local[1]
    print(f"命中生物像素 窗口内({local[0]},{local[1]})  屏幕({sx},{sy})")
    click(sx, sy)
    time.sleep(1.2)

    img2 = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom))
    out2 = OUT.replace("-06", "-06b")
    img2.save(out2)
    print(f"点击后截图 -> {out2}")

    # 顺带切到「规则编辑」标签页再截一张
    click(r.left + int(w * 0.725), r.top + 94)
    time.sleep(1.2)
    img3 = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom))
    out3 = OUT.replace("-06", "-06c")
    img3.save(out3)
    print(f"规则编辑页截图 -> {out3}")

    # 「物种」页
    click(r.left + int(w * 0.760), r.top + 94)
    time.sleep(1.2)
    img4 = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom))
    img4.save(OUT.replace("-06", "-06d"))
    print("物种页截图完成")

    # 「环境」页
    click(r.left + int(w * 0.795), r.top + 94)
    time.sleep(1.2)
    img5 = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom))
    img5.save(OUT.replace("-06", "-06e"))
    print("环境页截图完成")

    press_space()          # 恢复运行
    return 0


if __name__ == "__main__":
    sys.exit(main())
