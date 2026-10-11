#!/usr/bin/env python3
"""把用户给的那张 logo（带棋盘格底纹的 jpg）做成 WPF 应用多尺寸图标 app.ico。

坑与做法（都踩过）：
  * 源图是 **jpg** ⇒ 所谓"透明"其实是**画进像素的棋盘格**，必须自己抠；且图被缩放过 ⇒ 棋盘周期不是整数，
    用"固定周期模型"会漂 ⇒ 改用**两色 + 连通域**判定：贴边的一律算背景；内部区域白灰都占一定比例 = 棋盘底；
    纯白/纯灰（logo 自己的高光）= 保留。
  * 三种裁切：≥48 用全图；32 用"红星+锤子"；≤24 **只留红星本体**（16px 才认得出）。
  * 生成后**必须程序化自检**：逐档重读确认 四角 α=0 / 中心 α=255，并统计残留小色块。
依赖 Pillow + numpy + scipy（venv: D:/work/tools-venv）。
"""

import numpy as np, os
from PIL import Image, ImageFilter, ImageDraw
from scipy import ndimage

SRC = r"d:\hermes agent\hermes_data_native\profiles\lean\cache\images\img_9b6cc2103431.jpg"
OUT_ICO = r"D:/work/ra3-trainer-refine/src/Ra3Trainer.App/Assets/app.ico"
ART = r"D:/work/ra3-trainer-refine/artifacts"
im = Image.open(SRC).convert("RGB"); arr = np.asarray(im).astype(np.int16); H, W, _ = arr.shape

corner = np.concatenate([arr[:64,:64].reshape(-1,3), arr[-64:,-64:].reshape(-1,3), arr[:64,-64:].reshape(-1,3), arr[-64:,:64].reshape(-1,3)])
uniq, counts = np.unique(corner.reshape(-1,3), axis=0, return_counts=True); order = np.argsort(-counts)
white = uniq[order[0]].astype(np.int16)
gray  = next(uniq[i].astype(np.int16) for i in order if np.abs(uniq[i].astype(np.int16)-white).max() > 25)
def near(c, tol=16): return (np.abs(arr - c).max(axis=2) <= tol)
mask = near(white, 16) | near(gray, 16)
labels, n = ndimage.label(mask)
border = set(np.unique(np.concatenate([labels[0,:], labels[-1,:], labels[:,0], labels[:,-1]]))) - {0}
bg = np.zeros_like(mask)
for lb in range(1, n+1):
    sel = labels == lb; size = int(sel.sum())
    if size < 8: continue                                  # ★ 放宽到 8：把残留小点也吃掉
    if lb in border: bg |= sel; continue
    fw = (sel & near(white,20)).sum()/size; fg = (sel & near(gray,20)).sum()/size
    if fw > 0.08 and fg > 0.08: bg |= sel                  # ★ 放宽比例：白灰都有一点就算棋盘底
print(f"棋盘底 {bg.mean()*100:.1f}%")

rgba = im.convert("RGBA")
rgba.putalpha(Image.fromarray(np.where(bg, 0, 255).astype(np.uint8)))
rgba.putalpha(rgba.split()[3].filter(ImageFilter.MinFilter(3)).filter(ImageFilter.GaussianBlur(0.6)))

# ★ 确定性清残：只保留"大块连通区域"（logo 本体），小碎块（棋盘残留）一律透明。
#   做过实验：只靠颜色/比例判定总会有漏网的小格；按连通块大小一刀切最干净，且 logo 各部件都是巨块。
a = np.asarray(rgba.split()[3])
solid = a > 128
lab, n = ndimage.label(solid)
sizes = ndimage.sum(np.ones_like(lab), lab, range(1, n + 1)) if n else np.array([])
keep = np.zeros_like(solid)
kept = 0
for i, sz in enumerate(sizes, start=1):
    if sz >= 500:
        keep |= (lab == i); kept += 1
print(f"连通块 {n} 个 ⇒ 保留 {kept} 个（≥500px），清掉 {n-kept} 个小碎块")
a = np.where(keep, a, 0).astype(np.uint8)
rgba.putalpha(Image.fromarray(a))
KEYED = os.path.join(ART, "icon-src-keyed.png"); rgba.save(KEYED)
# 残留白点自检：统计"完全不透明但接近纯白/纯灰"的孤立像素团
op = np.asarray(rgba.split()[3]) > 200
specks = (op & (near(white,10) | near(gray,10)))
lab2, n2 = ndimage.label(specks)
sizes = ndimage.sum(np.ones_like(lab2), lab2, range(1, n2+1)) if n2 else []
small = [s for s in sizes if s <= 64]
print(f"残留可疑小色块（≤64px 的白/灰孤立块）: {len(small)} 个，最大 {max(small) if small else 0}")

def square(img, size, pad=0.04):
    inner = int(size*(1-pad*2)); w,h = img.size
    sc = min(inner/w, inner/h); r = img.resize((max(1,int(w*sc)), max(1,int(h*sc))), Image.LANCZOS)
    cv = Image.new("RGBA",(size,size),(0,0,0,0)); cv.paste(r, ((size-r.width)//2,(size-r.height)//2), r); return cv

# ★ 2026-10-11 用户指出"被截断了" ★ —— 之前我给小尺寸做了裁切（32px 只剩红星、没有 RA3 TRAINER 牌），
#   而任务栏/标题栏正是取 32px 那档 ⇒ 看着就是"截断"。现在**所有尺寸都用完整 logo**，只按尺寸缩放。
# ★ 2026-10-11 用户："和桌面其他应用比，我们的图标看着小" ★
#   量出来：logo 只占画布 77.9%（四边各 ~11% 透明留白）⇒ 缩到任务栏尺寸自然比别人的小。
#   做法：**按 alpha 内容框裁紧**，再只留 2% 边距（+ 视觉上五角星四角本来就空，留白要更小才不显小）。
_a = np.asarray(rgba.split()[3]); _ys, _xs = np.where(_a > 8)
rgba = rgba.crop((int(_xs.min()), int(_ys.min()), int(_xs.max())+1, int(_ys.max())+1))
print(f"按内容裁紧：{W}x{H} → {rgba.size[0]}x{rgba.size[1]}")
sizes = [256,128,64,48,32,24,16]
imgs = [square(rgba, s, pad=0.0) for s in sizes]   # 顶格：星形四角自带留白，视觉大小才和别的应用一致
# ★ 2026-10-11 关键：**不能用 Pillow 默认的 ICO 写盘** ★
#  实测（pefile 挖 exe）：Pillow 会把**所有档都写成 PNG** ✗，而 Windows 只保证 256 档能吃 PNG，
#  16/24/32/48 必须是老的 BMP(DIB) 格式 —— 否则资源管理器/任务栏渲染异常（用户看到的就是"被截断" ✗）。
#  所以这里手写 ICO：小档 BMP(含 1bit 透明掩码) + 只有 256 用 PNG。
import struct, io as _io
def _bmp_entry(img):
    w, h = img.size
    a = np.array(img)
    xor = a[:, :, [2, 1, 0, 3]][::-1].tobytes()            # BGRA 自下而上
    mask_row = (w + 31) // 24 * 4
    mask = bytearray(mask_row * h)
    transp = (a[:, :, 3] < 128)
    for y in range(h):
        row = transp[h - 1 - y]
        for x in range(w):
            if row[x]:
                mask[y * mask_row + (x // 8)] |= (0x80 >> (x % 8))
    return struct.pack("<IiiHHIIiiII", 40, w, h*2, 1, 32, 0, len(xor)+len(mask), 0, 0, 0, 0) + xor + bytes(mask)

blobs = []
for s, img in zip(sizes, imgs):
    if s >= 256:
        buf = _io.BytesIO(); img.save(buf, format="PNG"); blobs.append(buf.getvalue())
    else:
        blobs.append(_bmp_entry(img))
offset = 6 + 16 * len(sizes)
out = bytearray(struct.pack("<HHH", 0, 1, len(sizes)))
for s, data in zip(sizes, blobs):
    out += struct.pack("<BBBBHHII", 0 if s >= 256 else s, 0 if s >= 256 else s, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
for data in blobs:
    out += data
open(OUT_ICO, "wb").write(bytes(out))
print("ico:", os.path.getsize(OUT_ICO), "字节")

# 自检：逐档解析 + 角透明/中心不透明
bad = []
for s in sizes:
    v = Image.open(OUT_ICO); v.size = (s,s); v = v.convert("RGBA")
    corner = v.getpixel((0,0))[3]; center = v.getpixel((s//2, s//2))[3]
    ok = corner <= 24 and center >= 200          # 软边羽化 ⇒ 用容差，别要求纯 0/255
    print(f"  {s:3d}px 角α={corner:3d} 中α={center:3d} {'✓' if ok else '✗'}")
    if not ok: bad.append(s)
print("逐档自检:", "全部通过 ✓" if not bad else f"问题档 {bad}")

sheet = Image.new("RGBA", (620, 340), (245,245,245,255))
sheet.paste(Image.new("RGBA",(620,170),(22,22,26,255)), (0,170))
t = rgba.resize((300,300), Image.LANCZOS)
sheet.paste(t,(10,10),t); sheet.paste(t,(320,10),t)
x=20
for s,img in zip(sizes[3:], imgs[3:]):
    sheet.paste(img,(x,190),img); sheet.paste(img,(310+(x-20),190),img); x += s+24
sheet.convert("RGB").save(os.path.join(ART,"icon-final-preview.png"))
print("预览: artifacts/icon-final-preview.png")
