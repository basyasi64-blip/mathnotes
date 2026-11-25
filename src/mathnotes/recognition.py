"""Austauschbare Erkennungs-Schnittstelle.

Ehrlichkeitsprinzip: Ohne echtes Modell wird NICHT erkannt. Der NullRecognizer liefert
bewusst ein leeres Ergebnis, damit nie eine Erkennung vorgetaeuscht wird.
"""
from __future__ import annotations
from typing import Protocol, Sequence
from .models import Stroke, RecognitionResult


class Recognizer(Protocol):
    name: str
    version: str

    def recognize(self, strokes: Sequence[Stroke]) -> RecognitionResult: ...


class NullRecognizer:
    """Kennzeichnete Ersatzloesung: keine Erkennung, Nutzer tippt/korrigiert LaTeX selbst."""
    name = "null"
    version = "1"

    def recognize(self, strokes: Sequence[Stroke]) -> RecognitionResult:
        return RecognitionResult(self.name, self.version, ())


_REGISTRY: dict[str, Recognizer] = {"null": NullRecognizer()}


def register(recognizer: Recognizer) -> None:
    _REGISTRY[recognizer.name] = recognizer


def get(name: str) -> Recognizer:
    return _REGISTRY[name]


def available() -> list[str]:
    return sorted(_REGISTRY)
