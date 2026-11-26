"""Gruppiert Striche zu Formeln (neben- und untereinander) und bringt die Gruppen in Lesereihenfolge.

Regel: Zwei Striche gehoerigen zusammen, wenn ihre Umrandungen horizontal hoechstens KX*s und
vertikal hoechstens KY*s auseinander liegen (s = typische Zeichengroesse = Median der Strichgroessen).
Gruppen entstehen als zusammenhaengende Komponenten dieser Nachbarschaft.
Bekannte Grenzen: Matrizen mit grossem Zeilenabstand koennen in Zeilen zerfallen; zwei Formeln
muessen mindestens ca. 0,5 Zeichenhoehen vertikal bzw. 1,5 Zeichengroessen horizontal Abstand haben.
"""
from __future__ import annotations
from dataclasses import dataclass
from statistics import median
from typing import Sequence
from .models import Stroke

KX = 1.5      # max. horizontaler Abstand (in Zeichengroessen) innerhalb einer Formel
KY = 0.5      # max. vertikaler Abstand (in Zeichengroessen) innerhalb einer Formel
ROW_TOL = 0.9  # Tolerenz fuer "gleiche Zeile" beim Sortieren (in Zeichengroessen)
MIN_S = 12.0   # untere Grenze der Zeichengroesse (Pixel)


@dataclass(frozen=True)
class BBox:
    minx: float
    miny: float
    maxx: float
    maxy: float

    @property
    def cy(self) -> float:
        return (self.miny + self.maxy) / 2


@dataclass(frozen=True)
class Group:
    ids: tuple[int, ...]
    bbox: BBox


def stroke_bbox(stroke: Stroke) -> BBox:
    xs = [p.x for p in stroke.points]
    ys = [p.y for p in stroke.points]
    return BBox(min(xs), min(ys), max(xs), max(ys))


def _gap(a_min: float, a_max: float, b_min: float, b_max: float) -> float:
    return max(0.0, max(a_min, b_min) - min(a_max, b_max))


def group_strokes(items: Sequence[tuple[int, Stroke]]) -> list[Group]:
    items = [(i, s) for i, s in items if s.points]
    if not items:
        return []
    boxes = [stroke_bbox(s) for _, s in items]
    s = max(MIN_S, median(max(b.maxx - b.minx, b.maxy - b.miny) for b in boxes))
    n = len(items)
    parent = list(range(n))

    def find(x: int) -> int:
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for i in range(n):
        for j in range(i + 1, n):
            a, b = boxes[i], boxes[j]
            gx = _gap(a.minx, a.maxx, b.minx, b.maxx)
            gy = _gap(a.miny, a.maxy, b.miny, b.maxy)
            if gx <= KX * s and gy <= KY * s:
                parent[find(i)] = find(j)

    comps: dict[int, list[int]] = {}
    for i in range(n):
        comps.setdefault(find(i), []).append(i)
    groups = []
    for idxs in comps.values():
        bb = [boxes[i] for i in idxs]
        groups.append(Group(
            tuple(sorted(items[i][0] for i in idxs)),
            BBox(min(b.minx for b in bb), min(b.miny for b in bb),
                 max(b.maxx for b in bb), max(b.maxy for b in bb))))
    return reading_order(groups, s)


def reading_order(groups: list[Group], s: float) -> list[Group]:
    """Zeilenweise von oben nach unten, innerhalb einer Zeile von links nach rechts."""
    rows: list[list[Group]] = []
    for g in sorted(groups, key=lambda g: g.bbox.cy):
        if rows and abs(g.bbox.cy - sum(x.bbox.cy for x in rows[-1]) / len(rows[-1])) <= ROW_TOL * s:
            rows[-1].append(g)
        else:
            rows.append([g])
    return [g for row in rows for g in sorted(row, key=lambda g: g.bbox.minx)]
