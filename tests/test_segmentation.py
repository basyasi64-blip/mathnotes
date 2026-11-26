from mathnotes.models import Stroke, Point
from mathnotes.segmentation import group_strokes


def box(x, y, w, h):
    return Stroke((Point(x, y), Point(x + w, y + h)))


def ids(groups):
    return [list(g.ids) for g in groups]


def test_leer_und_einzelstrich():
    assert group_strokes([]) == []
    assert ids(group_strokes([(1, box(0, 0, 30, 40))])) == [[1]]


def test_f_von_x_bleibt_eine_formel():
    # f ( x ) mit kleinen Luecken (ca. 0,3 Zeichengroessen)
    items = [(1, box(0, 0, 20, 40)), (2, box(28, 0, 12, 40)), (3, box(48, 10, 24, 30)), (4, box(80, 0, 12, 40))]
    assert ids(group_strokes(items)) == [[1, 2, 3, 4]]


def test_bruch_bleibt_eine_formel():
    items = [(1, box(0, 0, 30, 40)), (2, box(-10, 50, 60, 2)), (3, box(0, 62, 30, 40))]
    assert ids(group_strokes(items)) == [[1, 2, 3]]


def test_zwei_formeln_nebeneinander_links_nach_rechts():
    a = [(1, box(0, 0, 40, 40)), (2, box(48, 0, 40, 40))]
    b = [(3, box(250, 0, 40, 40)), (4, box(298, 0, 40, 40))]      # Luecke ca. 4 Zeichengroessen
    assert ids(group_strokes(b + a)) == [[1, 2], [3, 4]]


def test_zwei_formeln_untereinander_oben_nach_unten():
    top = [(1, box(0, 0, 40, 40)), (2, box(48, 0, 40, 40))]
    bot = [(3, box(0, 100, 40, 40)), (4, box(48, 100, 40, 40))]    # vertikale Luecke 60 = 1,5 s
    assert ids(group_strokes(bot + top)) == [[1, 2], [3, 4]]


def test_raster_2x2_lesereihenfolge():
    items = [
        (1, box(0, 0, 40, 40)), (2, box(300, 4, 40, 40)),            # obere Zeile (leicht versetzt)
        (3, box(0, 120, 40, 40)), (4, box(300, 116, 40, 40)),        # untere Zeile
    ]
    assert ids(group_strokes(list(reversed(items)))) == [[1], [2], [3], [4]]


def test_hoch_und_tiefgestellt_bleiben_zusammen():
    items = [(1, box(0, 0, 30, 40)), (2, box(34, -10, 15, 20)), (3, box(34, 35, 15, 20))]
    assert ids(group_strokes(items)) == [[1, 2, 3]]


def test_zeichengroesse_skaliert_mit_schrift():
    # doppelt so grosse Schrift: Luecke 100 ist hier noch innerhalb einer Formel (< 1,5 * 80)
    items = [(1, box(0, 0, 80, 80)), (2, box(180, 0, 80, 80))]
    assert ids(group_strokes(items)) == [[1, 2]]
    # kleine Schrift: dieselbe Luecke trennt
    items = [(1, box(0, 0, 20, 20)), (2, box(120, 0, 20, 20))]
    assert ids(group_strokes(items)) == [[1], [2]]
