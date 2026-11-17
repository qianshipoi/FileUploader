"""生成 FileUploader 的应用图标：蓝色圆角方块 + 白色上传箭头（chevron + 竖杆）+ 底部横线。

输出：
  src/FileUploader.App/Assets/app.ico      多尺寸图标（16~256），给 exe 用
  %TEMP%/fu-icon-preview.png               512 预览图
  %TEMP%/fu-icon-sizes.png                 16/32/48/64 实际像素对照
"""

import os

from PIL import Image, ImageDraw

S = 4096
RADIUS = int(S * 0.215)

TOP = (0x4A, 0x9C, 0xFF)
BOTTOM = (0x0F, 0x4C, 0xC6)


def gradient(size: int) -> Image.Image:
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(0, size, 4):
            t = min(1.0, (x / size) * 0.40 + (y / size) * 0.60)
            c = tuple(int(TOP[i] + (BOTTOM[i] - TOP[i]) * t) for i in range(3))
            for dx in range(4):
                if x + dx < size:
                    px[x + dx, y] = c
    return img


def build() -> Image.Image:
    bg = gradient(S)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, S - 1, S - 1), radius=RADIUS, fill=255)

    icon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    icon.paste(bg, (0, 0), mask)

    white = (255, 255, 255, 255)
    stroke = int(round(S * 0.098))
    cx = S / 2

    d = ImageDraw.Draw(icon)

    # 箭头：chevron（圆角折线）+ 竖杆
    apex_y = S * 0.300
    arm_hw = S * 0.185
    arm_dy = S * 0.150
    d.line(
        [(cx - arm_hw, apex_y + arm_dy), (cx, apex_y), (cx + arm_hw, apex_y + arm_dy)],
        fill=white,
        width=stroke,
        joint="curve",
    )
    # 折线两端补半圆头
    for px in (cx - arm_hw, cx + arm_hw):
        d.ellipse((px - stroke / 2, apex_y + arm_dy - stroke / 2, px + stroke / 2, apex_y + arm_dy + stroke / 2), fill=white)

    # 竖杆（顶端埋进 chevron 里，形成一体）
    stem_top = apex_y + stroke * 0.30
    stem_bottom = S * 0.735
    d.rounded_rectangle((cx - stroke / 2, stem_top, cx + stroke / 2, stem_bottom), radius=stroke / 2, fill=white)

    # 底部横线：上传的「落点」
    bar_hw = S * 0.175
    bar_top = S * 0.795
    d.rounded_rectangle((cx - bar_hw, bar_top, cx + bar_hw, bar_top + stroke), radius=stroke / 2, fill=white)
    return icon


def main() -> None:
    big = build()
    out_dir = os.path.join("src", "FileUploader.App", "Assets")
    os.makedirs(out_dir, exist_ok=True)

    sizes = [256, 128, 96, 64, 48, 40, 32, 24, 20, 16]
    ico_path = os.path.join(out_dir, "app.ico")
    big.resize((256, 256), Image.LANCZOS).save(ico_path, format="ICO", sizes=[(s, s) for s in sizes])
    print("ico:", ico_path, os.path.getsize(ico_path), "bytes")

    preview = os.path.join(os.environ["TEMP"], "fu-icon-preview.png")
    big.resize((512, 512), Image.LANCZOS).save(preview)
    print("preview:", preview)

    strip = Image.new("RGBA", (400, 96), (243, 243, 243, 255))
    x = 14
    for s in (16, 32, 48, 64):
        small = big.resize((s, s), Image.LANCZOS)
        strip.paste(small, (x, (96 - s) // 2), small)
        x += s + 28
    strip_path = os.path.join(os.environ["TEMP"], "fu-icon-sizes.png")
    strip.save(strip_path)
    print("sizes:", strip_path)


if __name__ == "__main__":
    main()
