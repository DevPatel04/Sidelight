"""Renders the Sidelight icon (same design as the Android launcher icon) as a multi-size .ico and a 512 px PNG.

    python3 tools/make-icon.py   ->  agent/Sidelight.Agent/sidelight.ico, docs/sidelight-icon.png

Needs Pillow (pip install pillow). The design is drawn on a 96 x 96 grid: a monitor with a phone standing beside it.
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
NAVY, BLUE_LINE, DESK = '#042C53', '#85B7EB', '#378ADD'
AMBER, AMBER_DARK, AMBER_MID = '#FAC775', '#633806', '#BA7517'


def render(size: int) -> Image.Image:
    # draw large and shrink, so the small sizes stay smooth
    k = 8 * size / 96
    img = Image.new('RGBA', (8 * size, 8 * size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = lambda *v: [round(x * k) for x in v]
    w = max(1, round(3 * k))
    d.rounded_rectangle(s(0, 0, 96, 96), radius=round(22 * k), fill=NAVY)
    d.rounded_rectangle(s(12, 24, 56, 56), radius=round(4 * k), outline=BLUE_LINE, width=w)
    d.line(s(34, 56, 34, 64), fill=BLUE_LINE, width=w)
    d.line(s(28, 64, 40, 64), fill=BLUE_LINE, width=w)
    for line in [(71, 19, 71, 26), (81, 30, 86, 25), (61, 30, 56, 25)]:
        d.line(s(*line), fill=AMBER, width=w)
    d.rounded_rectangle(s(60, 34, 82, 72), radius=round(5 * k), fill=AMBER)
    d.rectangle(s(64, 40, 78, 46), fill=AMBER_DARK)
    d.rectangle(s(64, 49, 70, 55), fill=AMBER_DARK)
    d.rectangle(s(72, 49, 78, 55), fill=AMBER_MID)
    d.rectangle(s(64, 58, 78, 66), fill=AMBER_MID)
    d.line(s(8, 76, 88, 76), fill=DESK, width=w)
    return img.resize((size, size), Image.LANCZOS)


big = render(256)
big.save(ROOT / 'agent' / 'Sidelight.Agent' / 'sidelight.ico', sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
render(512).save(ROOT / 'docs' / 'sidelight-icon.png')
print('wrote agent/Sidelight.Agent/sidelight.ico and docs/sidelight-icon.png')
