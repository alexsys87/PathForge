"""Builds the small test fonts used by the unit tests (requires fontTools: pip install fonttools).

pf-test-cff.otf  - CFF outlines: lines, cubic curves, a hole, local and global subroutines, hint masks,
                   flex, GPOS kerning with a glyph pair (format 1) and a class pair (format 2).
pf-test-kern.ttf - TrueType outlines with the older 'kern' table.

All glyphs are simple shapes drawn here; the fonts contain nothing from other fonts.
"""
from fontTools.fontBuilder import FontBuilder
from fontTools.misc.psCharStrings import T2CharString
from fontTools.pens.t2CharStringPen import T2CharStringPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.feaLib.builder import addOpenTypeFeaturesFromString
from fontTools.ttLib.tables._k_e_r_n import KernTable_format_0
from fontTools.ttLib import newTable

ORDER = [".notdef", "space", "H", "O", "A", "V", "T", "o"]
CMAP = {0x20: "space", ord("H"): "H", ord("O"): "O", ord("A"): "A", ord("V"): "V", ord("T"): "T", ord("o"): "o"}
ADVANCES = {".notdef": 500, "space": 250, "H": 700, "O": 800, "A": 700, "V": 700, "T": 600, "o": 500}


def rect(pen, x0, y0, x1, y1):
    pen.moveTo((x0, y0))
    pen.lineTo((x1, y0))
    pen.lineTo((x1, y1))
    pen.lineTo((x0, y1))
    pen.closePath()


def ring(pen, cx, cy, r, clockwise):
    k = 0.5523 * r
    pts = [(cx + r, cy), (cx, cy + r), (cx - r, cy), (cx, cy - r)]
    ctrl = [((cx + r, cy + k), (cx + k, cy + r)), ((cx - k, cy + r), (cx - r, cy + k)),
            ((cx - r, cy - k), (cx - k, cy - r)), ((cx + k, cy - r), (cx + r, cy - k))]
    if clockwise:
        pts = [pts[0], pts[3], pts[2], pts[1]]
        ctrl = [(c[1], c[0]) for c in reversed(ctrl)]
    pen.moveTo(pts[0])
    for i in range(4):
        pen.curveTo(ctrl[i][0], ctrl[i][1], pts[(i + 1) % 4])
    pen.closePath()


def draw(name, pen):
    if name == "H":
        rect(pen, 0, 0, 600, 700)
    elif name == "O":
        ring(pen, 400, 350, 350, False)
        ring(pen, 400, 350, 150, True)
    elif name == "V":
        pen.moveTo((0, 700)); pen.lineTo((350, 0)); pen.lineTo((700, 700)); pen.closePath()
    elif name == "T":
        pen.moveTo((0, 700)); pen.lineTo((0, 600)); pen.lineTo((250, 600)); pen.lineTo((250, 0))
        pen.lineTo((350, 0)); pen.lineTo((350, 600)); pen.lineTo((600, 600)); pen.lineTo((600, 700)); pen.closePath()
    elif name == "o":
        ring(pen, 250, 250, 250, False)
    elif name == "A":
        pen.moveTo((0, 0)); pen.lineTo((350, 700)); pen.lineTo((700, 0)); pen.closePath()


def build_cff(path):
    fb = FontBuilder(1000, isTTF=False)
    fb.setupGlyphOrder(ORDER)
    fb.setupCharacterMap(CMAP)
    charstrings = {}
    for name in ORDER:
        pen = T2CharStringPen(ADVANCES[name], None)
        draw(name, pen)
        charstrings[name] = pen.getCharString()
    # 'A' by hand: width, stem hints with a hint mask, a local and a global subroutine (biased indices -107),
    # and a flex. Local subr 0: "rlineto 350 700"; global subr 0: "rlineto 350 -700 return".
    charstrings["A"] = T2CharString(program=[
        700, 0, 50, 650, 50, "hstemhm", 100, 50, "hintmask", bytes([0xE0]),
        0, 0, "rmoveto", -107, "callsubr", -107, "callgsubr",
        -100, 0, -100, 0, -100, 0, -100, 0, -100, 0, -100, 0, 50, "flex",
        "endchar"])
    fb.setupCFF("PFTestCff", {"FullName": "PF Test Cff"}, charstrings, {})
    cff = fb.font["CFF "].cff
    top = cff.topDictIndex[0]
    from fontTools.cffLib import SubrsIndex, GlobalSubrsIndex
    local = SubrsIndex()
    local.append(T2CharString(program=[350, 700, "rlineto", "return"]))
    top.Private.Subrs = local
    gsubrs = cff.GlobalSubrs
    gsubrs.append(T2CharString(program=[350, -700, "rlineto", "return"]))
    for name in ORDER:
        top.CharStrings[name].private = top.Private
        top.CharStrings[name].globalSubrs = gsubrs
    fb.setupHorizontalMetrics({n: (ADVANCES[n], 0) for n in ORDER})
    fb.setupHorizontalHeader(ascent=900, descent=-200)
    fb.setupNameTable({"familyName": "PF Test Cff", "styleName": "Regular"})
    fb.setupOS2(sTypoAscender=900, sTypoDescender=-200, usWinAscent=900, usWinDescent=200, sCapHeight=700, version=2)
    fb.setupPost()
    addOpenTypeFeaturesFromString(fb.font, """
        @LEFT = [T];
        @RIGHT = [o O];
        feature kern {
            pos A V -80;
            pos @LEFT @RIGHT -60;
        } kern;
    """)
    fb.save(path)


def build_kern_ttf(path):
    fb = FontBuilder(1000, isTTF=True)
    fb.setupGlyphOrder(ORDER)
    fb.setupCharacterMap(CMAP)
    glyphs = {}
    for name in ORDER:
        pen = TTGlyphPen(None)
        if name in ("H", "A", "V", "T"):
            draw(name, pen)
        else:
            rect(pen, 0, 0, 10, 10) if name == ".notdef" else None
        glyphs[name] = pen.glyph()
    fb.setupGlyf(glyphs)
    fb.setupHorizontalMetrics({n: (ADVANCES[n], 0) for n in ORDER})
    fb.setupHorizontalHeader(ascent=900, descent=-200)
    fb.setupNameTable({"familyName": "PF Test Kern", "styleName": "Regular"})
    fb.setupOS2(sTypoAscender=900, sTypoDescender=-200, usWinAscent=900, usWinDescent=200, sCapHeight=700, version=2)
    fb.setupPost()
    kern = newTable("kern")
    kern.version = 0
    sub = KernTable_format_0()
    sub.version = 0
    sub.coverage = 1
    sub.format = 0
    sub.kernTable = {("A", "V"): -100, ("T", "A"): -40}
    kern.kernTables = [sub]
    fb.font["kern"] = kern
    fb.save(path)


if __name__ == "__main__":
    import os
    here = os.path.dirname(os.path.abspath(__file__))
    build_cff(os.path.join(here, "pf-test-cff.otf"))
    build_kern_ttf(os.path.join(here, "pf-test-kern.ttf"))
