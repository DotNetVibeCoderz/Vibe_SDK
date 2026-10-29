"""Menggambar icon.png paket NuGet DepthAI.Net.

Gaya mengikuti keluarga icon Vibe_SDK (latar gelap membulat, ".NET" putih di kiri atas)
dengan palet "Depth Field" dari Wizard: hangat = dekat, biru = jauh. Lensa warna kamera
OAK digambar sebagai cincin kontur kedalaman, dan pita di bawahnya adalah depth ribbon
yang juga menjadi elemen khas status bar Wizard.

Jalankan:  python tools/branding/make_icon.py  (butuh Pillow)
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

SIZE = 256
SCALE = 4  # digambar 4x lalu diperkecil supaya tepi halus
S = SIZE * SCALE

# Token warna dari src/DepthAI.Net.Wizard/Themes (mode gelap).
ABYSS = (14, 20, 23)
DEEP = (20, 28, 33)
MID = (27, 37, 43)
NEAR_SURFACE = (36, 49, 58)
RAISED = (44, 59, 69)
INK = (232, 241, 242)
ACCENT_NEAR = (242, 160, 61)
ACCENT_MID = (70, 194, 168)
ACCENT_FAR = (111, 180, 232)


def px(v: float) -> int:
    return round(v * SCALE)


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def depth_color(t: float):
    """0 = dekat (amber), 0.5 = teal, 1 = jauh (biru)."""
    return lerp(ACCENT_NEAR, ACCENT_MID, t * 2) if t < 0.5 else lerp(ACCENT_MID, ACCENT_FAR, (t - 0.5) * 2)


def font(size: int):
    for name in ("segoeuib.ttf", "arialbd.ttf", "DejaVuSans-Bold.ttf"):
        try:
            return ImageFont.truetype(name, px(size))
        except OSError:
            continue
    return ImageFont.load_default()


def main() -> None:
    # Latar: gradasi vertikal halus dalam bentuk kotak membulat.
    background = Image.new("RGB", (S, S))
    draw = ImageDraw.Draw(background)
    for y in range(S):
        draw.line([(0, y), (S, y)], fill=lerp(MID, ABYSS, y / S))

    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=px(52), fill=255)

    icon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    icon.paste(background, (0, 0), mask)
    draw = ImageDraw.Draw(icon)

    # Point cloud perspektif di kanan atas: baris yang lebih jauh lebih rapat, kecil, dan biru.
    # Garis horizontal ala icon saudaranya sempat dicoba, tapi terbaca seperti ikon menu.
    overlay = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    dots = ImageDraw.Draw(overlay)
    rows = [(88, 15, 3.4, 0.55, 210), (70, 11.5, 2.7, 0.78, 170), (54, 8.5, 2.1, 1.0, 130)]
    for y, step, r, depth, alpha in rows:
        x = 222.0
        while x > 150:
            dots.ellipse([px(x - r), px(y - r), px(x + r), px(y + r)], fill=(*depth_color(depth), alpha))
            x -= step
    icon.alpha_composite(overlay)

    draw.text((px(32), px(26)), ".NET", font=font(46), fill=INK)

    # Badan kamera OAK: pil horizontal dengan bayangan lembut di bawahnya.
    body = [px(30), px(112), px(226), px(180)]
    shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        [body[0], body[1] + px(6), body[2], body[3] + px(6)], radius=px(34), fill=(0, 0, 0, 140))
    icon.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(px(6))))
    draw.rounded_rectangle(body, radius=px(34), fill=NEAR_SURFACE, outline=RAISED, width=px(2.5))

    cy = 146

    # Lensa mono kiri dan kanan: gelap dengan cincin biru (jauh).
    for cx in (68, 188):
        r = 17
        draw.ellipse([px(cx - r), px(cy - r), px(cx + r), px(cy + r)], fill=DEEP, outline=ACCENT_FAR, width=px(3))
        draw.ellipse([px(cx - 7), px(cy - 7), px(cx + 7), px(cy + 7)], fill=ABYSS)
        draw.ellipse([px(cx - 7), px(cy - 9), px(cx - 1), px(cy - 3)], fill=(*INK, 150))

    # Lensa warna di tengah: cincin kontur kedalaman dari jauh (luar) ke dekat (dalam).
    cx = 128
    rings = 6
    outer = 29
    for i in range(rings):
        r = outer - i * 3.4
        draw.ellipse([px(cx - r), px(cy - r), px(cx + r), px(cy + r)], fill=depth_color(1 - i / (rings - 1)))
    draw.ellipse([px(cx - 9), px(cy - 9), px(cx + 9), px(cy + 9)], fill=ABYSS)
    draw.ellipse([px(cx - 9), px(cy - 11), px(cx - 2), px(cy - 4)], fill=(*INK, 200))

    # Depth ribbon: dekat (hangat) di kiri, jauh (biru) di kanan.
    left, right, top, bottom = 30, 226, 198, 210
    ribbon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ribbon_draw = ImageDraw.Draw(ribbon)
    for x in range(px(left), px(right)):
        ribbon_draw.line([(x, px(top)), (x, px(bottom))], fill=(*depth_color((x - px(left)) / px(right - left)), 255))
    ribbon_mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(ribbon_mask).rounded_rectangle([px(left), px(top), px(right), px(bottom)], radius=px(6), fill=255)
    icon.paste(ribbon, (0, 0), ribbon_mask)

    # Penanda titik ukur di atas pita, seperti kursor kedalaman.
    mark = 82
    draw.polygon([(px(mark - 6), px(top - 10)), (px(mark + 6), px(top - 10)), (px(mark), px(top - 3))], fill=INK)

    output = Path(__file__).resolve().parents[2] / "icon.png"
    icon.resize((SIZE, SIZE), Image.LANCZOS).save(output, optimize=True)
    print(f"Tersimpan: {output}")


if __name__ == "__main__":
    main()
