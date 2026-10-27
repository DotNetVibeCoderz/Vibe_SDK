"""Draws the NuGet package icon.

    python tools/package-icon.py

Writes assets/package-icon.png, which Directory.Build.targets packs into every library.

The mark is the Reachy face - two round lenses joined by a bridge, under two sprung antennas. It is
the one shape Reachy Mini and Reachy 2 share, and it survives being shrunk: NuGet shows the icon at
128px on a package page and around 32px in a search result.

Drawn rather than rendered. A photoreal render of the 3D model is the obvious alternative and is
worse at 32px - soft shading and a mid-grey silhouette lose all their edges, where flat shapes with
one accent colour stay readable.

The first version was flat shapes on a flat tile and read as a toy. This one keeps exactly the same
silhouette and adds three things that cost nothing at 32px but make it look made rather than
drawn: a diagonal gradient on the ground, a lit top edge on the head, and real glass in the lenses -
a bright highlight above, a dim bounce below. Depth cues survive downscaling; detail does not.

Colours are the apps' own: the graphite ground and instrument amber from
src/PollenRobotics.Net.Ui/Theme/Tokens.axaml, so a package page and the gallery look related.
"""

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

SIZE = 256
SCALE = 4           # supersample, then downscale - Pillow has no antialiased drawing
W = SIZE * SCALE

GROUND_TOP = (38, 43, 54)
GROUND_BOTTOM = (16, 18, 23)
SHELL_LIT = (250, 251, 253)
SHELL = (226, 229, 235)
SHELL_SHADE = (176, 181, 193)
LENS = (12, 14, 18)
LENS_RIM = (58, 64, 78)
AMBER = (245, 165, 36)
AMBER_DIM = (176, 112, 18)
GLINT = (236, 241, 250)
BOUNCE = (74, 88, 112)

OUT = Path(__file__).resolve().parent.parent / "assets" / "package-icon.png"


def s(*values):
    """Scales design-space coordinates up to the supersampled canvas."""
    return tuple(v * SCALE for v in values)


def gradient(size, top, bottom):
    """A diagonal two-stop gradient, which is what stops the tile reading as a flat swatch."""
    ramp = Image.new("RGB", (size, size))
    pixels = ramp.load()

    for y in range(size):
        for x in range(size):
            # Diagonal: light at the top-left, dark at the bottom-right.
            t = (x / size * 0.35) + (y / size * 0.65)
            pixels[x, y] = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3))

    return ramp


def build():
    image = Image.new("RGBA", (W, W), (0, 0, 0, 0))

    # The tile, with its corners rounded by a mask rather than drawn - a drawn rounded rectangle
    # cannot carry a gradient.
    mask = Image.new("L", (W, W), 0)
    ImageDraw.Draw(mask).rounded_rectangle(s(0, 0, SIZE - 1, SIZE - 1), radius=56 * SCALE, fill=255)
    image.paste(gradient(W, GROUND_TOP, GROUND_BOTTOM), (0, 0), mask)

    draw = ImageDraw.Draw(image)

    # A soft pool of light behind the head, so the subject sits in the tile instead of on it.
    glow = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse(s(28, 80, 228, 236), fill=(90, 104, 130, 70))
    image.alpha_composite(glow.filter(ImageFilter.GaussianBlur(26 * SCALE)))
    draw = ImageDraw.Draw(image)

    # Antennas, behind the head. Each is a wire with a sprung base and an amber tip - the spring is
    # the detail that says Reachy Mini rather than generic robot, and three coils is the most that
    # survives 32px.
    for x_base, x_tip, direction in ((100, 62, -1), (156, 194, 1)):
        # Three coils at the base. Any more and they merge into a grey smudge at 32px; any fewer
        # and it reads as a kink rather than a spring.
        for i in range(3):
            cy = 112 - i * 9
            draw.arc(s(x_base - 10, cy - 7, x_base + 10, cy + 7),
                     start=200, end=20, fill=SHELL_SHADE, width=4 * SCALE)

        draw.line(s(x_base + direction * 2, 92, x_tip, 44), fill=SHELL, width=7 * SCALE)
        draw.ellipse(s(x_tip - 11, 33, x_tip + 11, 55), fill=AMBER)
        draw.ellipse(s(x_tip - 11, 44, x_tip + 11, 55), fill=AMBER_DIM)
        draw.ellipse(s(x_tip - 7, 36, x_tip - 1, 42), fill=(255, 221, 150))

    # The head: a rounded slab. Drawn three times - a shaded base, the body, and a lit top edge -
    # which reads as a curved surface at any size a flat fill cannot.
    draw.rounded_rectangle(s(48, 98, 208, 212), radius=38 * SCALE, fill=SHELL_SHADE)
    draw.rounded_rectangle(s(48, 96, 208, 207), radius=38 * SCALE, fill=SHELL)
    draw.rounded_rectangle(s(56, 98, 200, 150), radius=30 * SCALE, fill=SHELL_LIT)
    draw.rounded_rectangle(s(48, 118, 208, 207), radius=38 * SCALE, fill=SHELL)

    # The bridge between the lenses. Drawn before them so they overlap it, as spectacles do.
    draw.rounded_rectangle(s(108, 148, 148, 161), radius=6 * SCALE, fill=LENS)

    # The lenses: a rim, the glass, a dim bounce below, and a specular arc along the upper-left
    # edge. The highlight has to be an arc hugging the rim, not a dot near the middle - a centred
    # dot reads as a pupil, and two pupils turn a pair of camera lenses into a cartoon face.
    for cx in (90, 166):
        draw.ellipse(s(cx - 31, 122, cx + 31, 184), fill=LENS_RIM)
        draw.ellipse(s(cx - 29, 124, cx + 29, 182), fill=LENS)
        draw.ellipse(s(cx - 21, 150, cx + 15, 178), fill=BOUNCE)
        draw.ellipse(s(cx - 26, 128, cx + 24, 172), fill=LENS)
        draw.arc(s(cx - 24, 129, cx + 24, 177), start=186, end=256, fill=GLINT, width=5 * SCALE)
        draw.arc(s(cx - 19, 134, cx + 19, 172), start=200, end=232, fill=(142, 156, 180), width=3 * SCALE)

    icon = image.resize((SIZE, SIZE), Image.LANCZOS)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    icon.save(OUT, "PNG", optimize=True)

    # The size NuGet lists it at. Worth printing: a mark that only works at 256 is not a package
    # icon, it is a logo.
    icon.resize((32, 32), Image.LANCZOS).save(OUT.with_name("package-icon-32.png"), "PNG")

    print(f"wrote {OUT.name} ({OUT.stat().st_size / 1024:.1f} KB, {SIZE}x{SIZE}) and a 32px proof")


build()
