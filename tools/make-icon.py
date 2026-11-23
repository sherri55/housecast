# Draws src/Housecast/Housecast.ico and docs/icon.png: the Housecast mark (a house with sound waves rising
# inside it) in white on a blue rounded square. The geometry matches the tray icon in
# App.xaml.cs and the header mark in FlyoutWindow.xaml, on the same 32-unit grid.
# Run: python tools/make-icon.py   (needs Pillow)
from pathlib import Path
from PIL import Image, ImageDraw

SIZE = 1024
UNIT = SIZE / 40           # the 32-unit mark sits inside a 40-unit tile, 4 units of margin
BLUE = (0, 103, 192, 255)  # Windows 11's default accent
WHITE = (255, 255, 255, 255)


def at(x, y):
    return ((x + 4) * UNIT, (y + 4) * UNIT)


def stroke(draw, points, width):
    scaled = [at(x, y) for x, y in points]
    draw.line(scaled, fill=WHITE, width=round(width * UNIT), joint="curve")
    r = width * UNIT / 2
    for x, y in (scaled[0], scaled[-1]):
        draw.ellipse((x - r, y - r, x + r, y + r), fill=WHITE)


def arc(draw, cx, cy, radius, width):
    # Pillow draws an arc's stroke inside its box, so widen the box to centre it on the radius
    outer = radius + width / 2
    (x0, y0), (x1, y1) = at(cx - outer, cy - outer), at(cx + outer, cy + outer)
    draw.arc((x0, y0, x1, y1), 225, 315, fill=WHITE, width=round(width * UNIT))
    r = width * UNIT / 2
    for dx in (-radius * 0.7071, radius * 0.7071):
        x, y = at(cx + dx, cy - radius * 0.7071)
        draw.ellipse((x - r, y - r, x + r, y + r), fill=WHITE)


image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((0, 0, SIZE - 1, SIZE - 1), radius=SIZE * 0.22, fill=BLUE)
stroke(draw, [(5, 15), (16, 5), (27, 15)], 2.5)
stroke(draw, [(8, 12.5), (8, 27), (24, 27), (24, 12.5)], 2.5)
arc(draw, 16, 24, 4.5, 2)
arc(draw, 16, 24, 8, 2)
dot = 1.8 * UNIT
cx, cy = at(16, 24)
draw.ellipse((cx - dot, cy - dot, cx + dot, cy + dot), fill=WHITE)

root = Path(__file__).resolve().parent.parent
image.save(root / "src" / "Housecast" / "Housecast.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
# For the README
(root / "docs").mkdir(exist_ok=True)
image.resize((128, 128), Image.LANCZOS).save(root / "docs" / "icon.png")
