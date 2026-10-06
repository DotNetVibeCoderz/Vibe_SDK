"""Draws the NuGet package icon.

    python tools/package-icon.py

Writes assets/package-icon.png, which Directory.Build.targets packs into every library.

The mark is the Reachy face - two round lenses joined by a bar, under two antennas. It is the one
shape both Reachy Mini and Reachy 2 share, it is what this SDK spent its modelling effort on, and
it survives being shrunk: NuGet shows the icon at 128px on a package page and around 32px in a
search result, so anything with more detail than this turns to mud.

Drawn rather than rendered. A photoreal render of the 3D model was the obvious alternative and is
worse at 32px - soft shading and a mid-grey silhouette lose all their edges, where flat shapes with
one accent colour stay readable.

Colours are the apps' own: the graphite ground and instrument amber from
src/PollenRobotics.Net.Ui/Theme/Tokens.axaml, so a package page and the gallery look related.
"""

from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 256
SCALE = 4           # supersample, then downscale - Pillow has no antialiased drawing
W = SIZE * SCALE

GROUND = (26, 29, 35, 255)        # warm graphite
SHELL = (232, 234, 238, 255)
LENS = (16, 18, 23, 255)
AMBER = (245, 165, 36, 255)
GLINT = (92, 99, 112, 255)

OUT = Path(__file__).resolve().parent.parent / "assets" / "package-icon.png"


def s(*values):
    """Scales design-space coordinates up to the supersampled canvas."""
    return tuple(v * SCALE for v in values)


def build():
    image = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # The tile. Rounded rather than square so it sits well on both a light and a dark page, with
    # transparent corners rather than white ones - a white corner is invisible on nuget.org and
    # glaring in a dark IDE.
    draw.rounded_rectangle(s(0, 0, SIZE - 1, SIZE - 1), radius=52 * SCALE, fill=GROUND)

    # Antennas, drawn before the head so they emerge from behind it.
    for x_base, x_tip in ((100, 64), (156, 192)):
        draw.line(s(x_base, 112, x_tip, 46), fill=SHELL, width=7 * SCALE)
        draw.ellipse(s(x_tip - 10, 36, x_tip + 10, 56), fill=AMBER)

    # The head: a rounded slab, wider than it is tall.
    draw.rounded_rectangle(s(50, 96, 206, 208), radius=36 * SCALE, fill=SHELL)

    # Two lenses joined by a bridge. The bridge is what makes it read as a face rather than as two
    # unrelated holes.
    draw.rounded_rectangle(s(110, 147, 146, 159), radius=6 * SCALE, fill=LENS)

    # The lenses are set apart far enough for the bridge to show between them. Drawn closer - which
    # the first pass did - they merge into one dark mass and the mark stops reading as a face.
    for cx in (90, 166):
        draw.ellipse(s(cx - 29, 124, cx + 29, 182), fill=LENS)
        # One small off-centre glint per lens, which is what stops them reading as flat discs.
        draw.ellipse(s(cx - 17, 134, cx - 7, 144), fill=GLINT)

    icon = image.resize((SIZE, SIZE), Image.LANCZOS)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    icon.save(OUT, "PNG", optimize=True)

    print(f"wrote {OUT.relative_to(OUT.parents[1])} ({OUT.stat().st_size / 1024:.1f} KB, {SIZE}x{SIZE})")


build()
