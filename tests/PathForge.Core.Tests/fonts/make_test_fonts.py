"""Builds the small test fonts used by the unit tests (requires fontTools: pip install fonttools).

pf-test-cff.otf  - CFF outlines: lines, cubic curves, a hole, local and global subroutines, hint masks,
                   flex, GPOS kerning with a glyph pair (format 1) and a class pair (format 2).
pf-test-kern.ttf - TrueType outlines with the older 'kern' table.
pf-test-var.ttf  - variable TrueType font (fvar, avar, gvar, HVAR): weight axis 100…900 with the default at 100,
                   avar maps 500 to the design value 300; 'H' grows from 600 to 1000 units wide, 'O' changes its
                   off-curve ring and hole (IUP-inferred deltas), 'Q' is a composite of 'O' moving by 100 units.
pf-test-var-nohvar.ttf - the same without HVAR: advance widths come from the gvar phantom points.
pf-test-var-cff2.otf - variable CFF2 font with the same weight axis (blend operators) and HVAR.

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


VAR_ORDER = [".notdef", "space", "H", "O", "Q"]
VAR_CMAP = {0x20: "space", ord("H"): "H", ord("O"): "O", ord("Q"): "Q"}


def var_master(path, bold, cff):
    """Light (default) or Bold master of the variable test fonts."""
    w = 1000 if bold else 600
    advances = {".notdef": 500, "space": 250, "H": w + 100, "O": 800 if not bold else 900, "Q": 800 if not bold else 900}
    fb = FontBuilder(1000, isTTF=not cff)
    fb.setupGlyphOrder(VAR_ORDER)
    fb.setupCharacterMap(VAR_CMAP)
    if cff:
        charstrings = {}
        for name in VAR_ORDER:
            pen = T2CharStringPen(advances[name], None)
            if name == "H":
                rect(pen, 0, 0, w, 700)
            elif name in ("O", "Q"):
                ring(pen, 400, 350, 350 if not bold else 400, False)
                ring(pen, 400, 350, 150 if not bold else 100, True)
            elif name == ".notdef":
                rect(pen, 0, 0, 10, 10)
            charstrings[name] = pen.getCharString()
        fb.setupCFF("PFTestVar", {"FullName": "PF Test Var"}, charstrings, {})
    else:
        glyphs = {}
        for name in VAR_ORDER:
            pen = TTGlyphPen(glyphs)
            if name == "H":
                rect(pen, 0, 0, w, 700)
            elif name == "O":
                a, b = (100, 700) if not bold else (50, 750)
                pen.qCurveTo((b, b), (a, b), (a, a), (b, a), None)
                pen.closePath()
                h0, h1 = (300, 500) if not bold else (350, 450)
                pen.moveTo((h0, h0)); pen.lineTo((h0, h1)); pen.lineTo((h1, h1)); pen.lineTo((h1, h0)); pen.closePath()
            elif name == ".notdef":
                rect(pen, 0, 0, 10, 10)
            if name == "Q":
                pen.addComponent("O", (1, 0, 0, 1, 100 if bold else 0, 0))
            glyphs[name] = pen.glyph()
        fb.setupGlyf(glyphs)
    fb.setupHorizontalMetrics({n: (advances[n], 0) for n in VAR_ORDER})
    fb.setupHorizontalHeader(ascent=900, descent=-200)
    fb.setupNameTable({"familyName": "PF Test Var", "styleName": "Bold" if bold else "Light"})
    fb.setupOS2(sTypoAscender=900, sTypoDescender=-200, usWinAscent=900, usWinDescent=200, sCapHeight=700, version=2)
    fb.setupPost()
    fb.save(path)


def build_variable(path, cff, keep_hvar=True):
    import os
    import tempfile
    from fontTools.designspaceLib import DesignSpaceDocument, AxisDescriptor, SourceDescriptor, InstanceDescriptor
    from fontTools import varLib
    from fontTools.ttLib import TTFont
    tmp = tempfile.mkdtemp()
    ext = ".otf" if cff else ".ttf"
    light, bold = os.path.join(tmp, "light" + ext), os.path.join(tmp, "bold" + ext)
    var_master(light, False, cff)
    var_master(bold, True, cff)
    doc = DesignSpaceDocument()
    axis = AxisDescriptor()
    axis.tag, axis.name, axis.minimum, axis.default, axis.maximum = "wght", "Weight", 100, 100, 900
    axis.map = [(100, 100), (500, 300), (900, 900)]
    doc.addAxis(axis)
    for file, value in ((light, 100), (bold, 900)):
        source = SourceDescriptor()
        source.path, source.location = file, {"Weight": value}
        doc.addSource(source)
    for style, value in (("Light", 100), ("Regular", 400), ("Bold", 900)):
        instance = InstanceDescriptor()
        instance.familyName, instance.styleName, instance.location = "PF Test Var", style, {"Weight": value}
        doc.addInstance(instance)
    font, _, _ = varLib.build(doc)
    if not keep_hvar and "HVAR" in font:
        del font["HVAR"]
    font.save(path)


if __name__ == "__main__":
    import os
    here = os.path.dirname(os.path.abspath(__file__))
    build_cff(os.path.join(here, "pf-test-cff.otf"))
    build_kern_ttf(os.path.join(here, "pf-test-kern.ttf"))
    build_variable(os.path.join(here, "pf-test-var.ttf"), cff=False)
    build_variable(os.path.join(here, "pf-test-var-nohvar.ttf"), cff=False, keep_hvar=False)
    build_variable(os.path.join(here, "pf-test-var-cff2.otf"), cff=True)
