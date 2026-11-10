"""生成应用图标：绿色渐变圆角方块 + 两个叠加的对话气泡（多开）。

纯 Pillow 绘制（4x 超采样后缩小，边缘干净），不依赖外部图片素材。
输出：
  assets/icon/icon-1024.png        源图（README / 商店图用）
  src/WeChatBoot.App/AppIcon.ico   多尺寸 ico（16/24/32/48/64/128/256）
用法：python assets/icon/make_icon.py
"""
from __future__ import annotations

import os

from PIL import Image, ImageDraw, ImageFilter

S = 1024          # 最终尺寸
SS = 4            # 超采样倍数
N = S * SS

OUT_ICO = os.path.join("src", "WeChatBoot.App", "AppIcon.ico")
OUT_PNG = os.path.join("assets", "icon", "icon-1024.png")

TOP = (26, 212, 128)      # #1AD480
BOTTOM = (6, 164, 94)     # #06A45E


def px(v: float) -> float:
    """设计坐标（1024 网格）→ 超采样像素。"""
    return v * SS


def gradient(n: int) -> Image.Image:
    """对角渐变：左上偏亮，右下偏深。"""
    img = Image.new("RGB", (n, n))
    px_ = img.load()
    for y in range(n):
        for x in range(n):
            t = (x + y) / (2 * n)
            px_[x, y] = (
                round(TOP[0] + (BOTTOM[0] - TOP[0]) * t),
                round(TOP[1] + (BOTTOM[1] - TOP[1]) * t),
                round(TOP[2] + (BOTTOM[2] - TOP[2]) * t),
            )
    return img


def rounded_mask(radius: float, margin: float) -> Image.Image:
    m = Image.new("L", (N, N), 0)
    ImageDraw.Draw(m).rounded_rectangle(
        [margin, margin, N - margin - 1, N - margin - 1],
        radius=radius,
        fill=255,
    )
    return m


def bubble(x0: float, y0: float, x1: float, y1: float, radius: float, tail=None) -> Image.Image:
    """白色气泡形状（圆角矩形 + 可选尾巴），返回 L 通道遮罩。"""
    layer = Image.new("L", (N, N), 0)
    d = ImageDraw.Draw(layer)
    d.rounded_rectangle([px(x0), px(y0), px(x1), px(y1)], radius=px(radius), fill=255)
    if tail:
        d.polygon([(px(x), px(y)) for x, y in tail], fill=255)
    return layer


def tint(mask: Image.Image, alpha: float = 1.0) -> Image.Image:
    """把遮罩变成白色图层。"""
    layer = Image.new("RGBA", (N, N), (255, 255, 255, 0))
    layer.putalpha(mask.point(lambda v: int(v * alpha)))
    return layer


def main() -> None:
    canvas = Image.new("RGBA", (N, N), (0, 0, 0, 0))

    # 1) 圆角方块底色
    mask = rounded_mask(px(232), px(24))
    canvas.paste(gradient(N).convert("RGBA"), (0, 0), mask)

    # 2) 顶部柔光，避免大面积纯色显得平
    glow = Image.new("L", (N, N), 0)
    ImageDraw.Draw(glow).ellipse([-px(250), -px(760), px(1274), px(360)], fill=80)
    glow = glow.filter(ImageFilter.GaussianBlur(px(52)))
    canvas = Image.alpha_composite(canvas, Image.composite(tint(glow), canvas, mask))

    # 3) 后面的气泡（半透明，暗示"不止一个"）
    back = bubble(520, 196, 872, 452, 92)
    canvas = Image.alpha_composite(canvas, tint(back, 0.62))

    # 4) 前面的气泡：柔和投影 + 实心白
    front = bubble(196, 330, 690, 726, 126,
                   tail=[(300, 706), (270, 848), (422, 726)])
    # 绿色系投影（纯黑投影压在绿底上会发灰发脏）
    shadow_shape = Image.new("RGBA", (N, N), (0, 74, 45, 0))
    shadow_shape.putalpha(front.point(lambda v: int(v * 0.32)))
    shadow_shape = shadow_shape.filter(ImageFilter.GaussianBlur(px(16)))
    shadow = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    shadow.alpha_composite(shadow_shape, (0, int(px(12))))
    canvas = Image.alpha_composite(canvas, shadow)

    canvas = Image.alpha_composite(canvas, tint(front))

    # 5) 气泡内三个点
    dots = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    dd = ImageDraw.Draw(dots)
    cy, r = 528, 42
    for cx in (316, 444, 572):
        dd.ellipse([px(cx - r), px(cy - r), px(cx + r), px(cy + r)], fill=(7, 178, 106, 255))
    canvas = Image.alpha_composite(canvas, dots)

    big = canvas.resize((S, S), Image.LANCZOS)
    os.makedirs(os.path.dirname(OUT_PNG), exist_ok=True)
    big.save(OUT_PNG)

    sizes = [256, 128, 64, 48, 32, 24, 16]
    frames = [big.resize((s, s), Image.LANCZOS) for s in sizes]
    os.makedirs(os.path.dirname(OUT_ICO), exist_ok=True)
    frames[0].save(OUT_ICO, format="ICO", sizes=[(s, s) for s in sizes])
    print("wrote", OUT_PNG, "and", OUT_ICO)


if __name__ == "__main__":
    main()
