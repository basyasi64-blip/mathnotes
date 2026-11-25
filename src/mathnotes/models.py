from __future__ import annotations
from dataclasses import dataclass, field


@dataclass(frozen=True)
class Point:
    x: float
    y: float
    pressure: float = 0.5
    t_ms: int = 0


@dataclass(frozen=True)
class Stroke:
    points: tuple[Point, ...]
    color: str = "#000000"
    width: float = 2.0


@dataclass(frozen=True)
class Candidate:
    latex: str
    confidence: float  # 0..1, vom Erkenner gemeldet


@dataclass(frozen=True)
class RecognitionResult:
    engine: str
    engine_version: str
    candidates: tuple[Candidate, ...]  # leer = nicht erkannt

    @property
    def recognized(self) -> bool:
        return len(self.candidates) > 0


@dataclass
class Formula:
    id: int
    page_id: int
    stroke_ids: list[int]
    recognized_latex: str | None
    edited_latex: str | None
    confidence: float | None
    alternatives: list[Candidate] = field(default_factory=list)
    engine: str | None = None

    @property
    def current_latex(self) -> str | None:
        """Vom Nutzer bestaetigter Text hat Vorrang vor dem Erkenner-Ergebnis."""
        return self.edited_latex if self.edited_latex is not None else self.recognized_latex
