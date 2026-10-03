#!/usr/bin/env python3
"""Add D2Coding's complete Hangul syllables to the KDAOC composition font.

Latin, punctuation, component glyphs, advances and U+0020 come from
KDAOC. Only U+AC00..U+D7A3 are added from D2Coding so ordinary DAoC chat keeps
its compact spacing while the render hook can draw recomposed Hangul.
"""

from __future__ import annotations

import argparse
from pathlib import Path

from fontTools.pens.recordingPen import DecomposingRecordingPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont


HANGUL_START = 0xAC00
HANGUL_END = 0xD7A3
HANGUL_ADVANCE = 823
HANGUL_X_SCALE = HANGUL_ADVANCE / 1000


def copy_hangul_glyph(
    output_font: TTFont,
    source_font: TTFont,
    source_name: str,
    codepoint: int,
) -> str:
    recording = DecomposingRecordingPen(source_font.getGlyphSet())
    source_font.getGlyphSet()[source_name].draw(recording)
    pen = TTGlyphPen(None)
    recording.replay(TransformPen(pen, (HANGUL_X_SCALE, 0, 0, 1, 0, 0)))

    target_name = f"completeHangul{codepoint:04X}"
    output_font["glyf"][target_name] = pen.glyph()
    output_font["hmtx"].metrics[target_name] = (HANGUL_ADVANCE, 0)
    return target_name


def set_names(font: TTFont, source_font: TTFont) -> None:
    family = "DAoC KDAOC Complete Hangul"
    for name_id, value in (
        (1, family),
        (2, "Bold"),
        (4, f"{family} Bold"),
        (5, "Version 18.0"),
        (6, "DAoCKDAOC-CompleteHangul-Bold"),
    ):
        font["name"].setName(value, name_id, 3, 1, 0x409)
        font["name"].setName(value, name_id, 1, 0, 0)
    font["head"].fontRevision = 18.0

    source_names = source_font["name"]
    copyright_text = source_names.getDebugName(0)
    license_text = source_names.getDebugName(13)
    license_url = source_names.getDebugName(14)
    if copyright_text:
        font["name"].setName(
            f"{copyright_text} Hangul syllable outlines added to the KDAOC font.",
            0,
            3,
            1,
            0x409,
        )
    if license_text:
        font["name"].setName(license_text, 13, 3, 1, 0x409)
    if license_url:
        font["name"].setName(license_url, 14, 3, 1, 0x409)


def validate(font: TTFont, original_space_name: str, original_space_advance: int) -> None:
    cmap = font.getBestCmap()
    missing = [
        codepoint
        for codepoint in range(HANGUL_START, HANGUL_END + 1)
        if codepoint not in cmap
    ]
    if missing:
        raise RuntimeError(f"serialized font is missing {len(missing)} Hangul syllables")

    if cmap.get(0x20) != original_space_name:
        raise RuntimeError("U+0020 space code point changed")
    if font["hmtx"].metrics[original_space_name][0] != original_space_advance:
        raise RuntimeError("U+0020 space advance changed")
    if font["glyf"][original_space_name].numberOfContours != 0:
        raise RuntimeError("U+0020 must remain an empty space glyph")

    for codepoint in (HANGUL_START, 0xC548, 0xD55C, HANGUL_END):
        glyph_name = cmap[codepoint]
        glyph = font["glyf"][glyph_name]
        glyph.recalcBounds(font["glyf"])
        if glyph.numberOfContours == 0:
            raise RuntimeError(f"empty Hangul glyph U+{codepoint:04X}")
        if font["hmtx"].metrics[glyph_name][0] != HANGUL_ADVANCE:
            raise RuntimeError(f"bad Hangul advance U+{codepoint:04X}")


def build(template_path: Path, source_path: Path, output_path: Path) -> None:
    template_font = TTFont(template_path, recalcTimestamp=False)
    source_font = TTFont(source_path, recalcTimestamp=False)
    output_font = TTFont(template_path, recalcBBoxes=True, recalcTimestamp=False)
    source_cmap = source_font.getBestCmap()

    if template_font["head"].unitsPerEm != source_font["head"].unitsPerEm:
        raise RuntimeError("KDAOC and D2Coding units-per-em do not match")

    original_cmap = template_font.getBestCmap()
    original_space_name = original_cmap[0x20]
    original_space_advance = template_font["hmtx"].metrics[original_space_name][0]
    added: dict[int, str] = {}
    for codepoint in range(HANGUL_START, HANGUL_END + 1):
        source_name = source_cmap.get(codepoint)
        if source_name is None:
            raise RuntimeError(f"D2Coding source is missing U+{codepoint:04X}")
        target_name = copy_hangul_glyph(
            output_font,
            source_font,
            source_name,
            codepoint,
        )
        added[codepoint] = target_name
    output_font.setGlyphOrder(list(output_font["glyf"].glyphs))

    unicode_tables = [table for table in output_font["cmap"].tables if table.isUnicode()]
    if not unicode_tables:
        raise RuntimeError("KDAOC template has no Unicode cmap")
    for table in unicode_tables:
        table.cmap.update(added)

    output_font["post"].formatType = 3.0
    if "OS/2" in output_font:
        output_font["OS/2"].usLastCharIndex = HANGUL_END
    set_names(output_font, source_font)
    validate(output_font, original_space_name, original_space_advance)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_font.save(output_path, reorderTables=True)
    written = TTFont(output_path, recalcBBoxes=True, recalcTimestamp=False)
    validate(written, original_space_name, original_space_advance)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--template", required=True, type=Path)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    build(args.template, args.source, args.output)


if __name__ == "__main__":
    main()
