#!/usr/bin/env python3
"""Build the D2Coding-based extended legacy Hangul font.

DAoC's legacy renderer composes a Hangul syllable from separate choseong,
jungseong and jongseong glyphs. The original KDAOC font remains the source
of Latin glyphs, component positioning boxes and advances. Extended mode
replaces every Hangul component outline with the matching D2CodingLigature
compatibility-jamo outline and assigns separate IDs to no-final choseong.

U+0020 is a protocol invariant: it must stay an empty, ordinary space glyph.
"""

from __future__ import annotations

import argparse
from pathlib import Path

from fontTools.pens.recordingPen import DecomposingRecordingPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont


INITIAL_CODES = [
    1, 2, 4, 7, 8, 9, 17, 18, 19, 21,
    22, 23, 24, 25, 26, 27, 28, 29, 30,
]
INITIAL_JAMO = [
    0x3131, 0x3132, 0x3134, 0x3137, 0x3138, 0x3139, 0x3141,
    0x3142, 0x3143, 0x3145, 0x3146, 0x3147, 0x3148, 0x3149,
    0x314A, 0x314B, 0x314C, 0x314D, 0x314E,
]
MEDIAL_CODES = list(range(31, 52))
MEDIAL_JAMO = list(range(0x314F, 0x3164))
FINAL_CODES = [
    1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15,
    16, 17, 18, 20, 21, 22, 23, 24, 26, 27, 28, 29, 30,
]
FINAL_JAMO = [
    0x3131, 0x3132, 0x3133, 0x3134, 0x3135, 0x3136, 0x3137,
    0x3139, 0x313A, 0x313B, 0x313C, 0x313D, 0x313E, 0x313F,
    0x3140, 0x3141, 0x3142, 0x3144, 0x3145, 0x3146, 0x3147,
    0x3148, 0x314A, 0x314B, 0x314C, 0x314D, 0x314E,
]

# Unicode characters that round-trip through the printable slots of Windows-
# 1252 bytes 80, 82-8C, 8E and 91-96. Raw U+0080..U+009F controls are not emitted.
NO_FINAL_INITIAL_CODEPOINTS = [
    0x20AC, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021,
    0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0x017D, 0x2018,
    0x2019, 0x201C, 0x201D, 0x2022, 0x2013,
]
NO_FINAL_INITIAL_RAW_ALIASES = [
    0x80, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
    0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8E, 0x91,
    0x92, 0x93, 0x94, 0x95, 0x96,
]

# No-final choseong returns to the proven KDAOC shape: keep its horizontal
# geometry unchanged and stretch it only into the final-free vertical area.
# This remains more legible at the game's 11 px size than enlarging D2Coding
# compatibility jamo in both dimensions.
NO_FINAL_BOTTOM = 366
NO_FINAL_TOP = 833


def encode_initial(code: int) -> int:
    adjustment = (1 if code > 2 else 0) + (2 if code > 4 else 0)
    adjustment += (7 if code > 9 else 0) + (1 if code > 19 else 0)
    return 0xAF + code - adjustment


def encode_medial(code: int, has_final: bool) -> int:
    encoded = 0xAF + code - 11
    if not has_final:
        encoded = encoded + 33 if encoded >= 0xD3 else encoded - 35
    return encoded


def encode_final(code: int) -> int:
    adjustment = (1 if code > 7 else 0) + (1 if code > 18 else 0)
    adjustment += (1 if code > 24 else 0) - 40
    return 0xAF + code - adjustment


def component_map() -> dict[int, int]:
    result = {
        encode_initial(code): jamo
        for code, jamo in zip(INITIAL_CODES, INITIAL_JAMO, strict=True)
    }
    for code, jamo in zip(MEDIAL_CODES, MEDIAL_JAMO, strict=True):
        result[encode_medial(code, has_final=False)] = jamo
        result[encode_medial(code, has_final=True)] = jamo
    result.update({
        encode_final(code): jamo
        for code, jamo in zip(FINAL_CODES, FINAL_JAMO, strict=True)
    })
    return result


def glyph_bounds(font: TTFont, glyph_name: str) -> tuple[int, int, int, int]:
    glyph = font["glyf"][glyph_name]
    glyph.recalcBounds(font["glyf"])
    return glyph.xMin, glyph.yMin, glyph.xMax, glyph.yMax


def transformed_glyph(
    source_font: TTFont,
    source_name: str,
    target_bounds: tuple[int, int, int, int],
):
    tx_min, ty_min, tx_max, ty_max = target_bounds
    sx_min, sy_min, sx_max, sy_max = glyph_bounds(source_font, source_name)
    if sx_max <= sx_min or sy_max <= sy_min:
        raise RuntimeError(f"empty D2Coding source glyph {source_name}")

    scale_x = (tx_max - tx_min) / (sx_max - sx_min)
    scale_y = (ty_max - ty_min) / (sy_max - sy_min)
    offset_x = tx_min - sx_min * scale_x
    offset_y = ty_min - sy_min * scale_y

    recording = DecomposingRecordingPen(source_font.getGlyphSet())
    source_font.getGlyphSet()[source_name].draw(recording)
    pen = TTGlyphPen(None)
    recording.replay(TransformPen(pen, (scale_x, 0, 0, scale_y, offset_x, offset_y)))
    return pen.glyph()


def replace_component_outline(
    output_font: TTFont,
    template_font: TTFont,
    source_font: TTFont,
    target_codepoint: int,
    source_codepoint: int,
) -> None:
    template_name = template_font.getBestCmap()[target_codepoint]
    source_name = source_font.getBestCmap()[source_codepoint]
    target_bounds = glyph_bounds(template_font, template_name)
    output_font["glyf"][template_name] = transformed_glyph(
        source_font,
        source_name,
        target_bounds,
    )


def add_no_final_initial(
    output_font: TTFont,
    template_font: TTFont,
    source_initial_codepoint: int,
    target_codepoint: int,
    raw_alias: int,
    index: int,
) -> None:
    template_name = template_font.getBestCmap()[source_initial_codepoint]
    _, source_bottom, _, source_top = glyph_bounds(template_font, template_name)
    if source_top <= source_bottom:
        raise RuntimeError(f"empty KDAOC choseong U+{source_initial_codepoint:04X}")

    scale_y = (NO_FINAL_TOP - NO_FINAL_BOTTOM) / (source_top - source_bottom)
    offset_y = NO_FINAL_BOTTOM - source_bottom * scale_y
    recording = DecomposingRecordingPen(template_font.getGlyphSet())
    template_font.getGlyphSet()[template_name].draw(recording)
    pen = TTGlyphPen(None)
    recording.replay(TransformPen(pen, (1, 0, 0, scale_y, 0, offset_y)))

    target_name = f"d2ExtNoFinalCho{index:02d}"
    glyph_order = output_font.getGlyphOrder()
    if target_name not in glyph_order:
        glyph_order.append(target_name)
        output_font.setGlyphOrder(glyph_order)
    output_font["glyf"][target_name] = pen.glyph()
    output_font["hmtx"].metrics[target_name] = template_font["hmtx"].metrics[template_name]

    for cmap_table in output_font["cmap"].tables:
        if cmap_table.isUnicode():
            cmap_table.cmap[target_codepoint] = target_name
            cmap_table.cmap[raw_alias] = target_name
        elif raw_alias <= 0xFF:
            # The old DAoC renderer can select the font's 8-bit cmap and ask
            # for the Windows-1252 byte directly.
            cmap_table.cmap[raw_alias] = target_name


def set_names(font: TTFont, source_font: TTFont) -> None:
    family = "DAoC D2Coding Legacy Extended"
    source_style = source_font["name"].getDebugName(2) or "Regular"
    is_bold = "bold" in source_style.lower()
    style = "Bold" if is_bold else "Regular"
    full_name = f"{family} {style}" if is_bold else family
    postscript = f"DAoCD2Coding-LegacyExtended{'-Bold' if is_bold else ''}"
    for name_id, value in (
        (1, family),
        (2, style),
        (4, full_name),
        (6, postscript),
    ):
        font["name"].setName(value, name_id, 3, 1, 0x409)
        font["name"].setName(value, name_id, 1, 0, 0)
    font["name"].setName("Version 17.7", 5, 3, 1, 0x409)
    font["name"].setName("Version 17.7", 5, 1, 0, 0)
    font["head"].fontRevision = 17.7

    source_names = source_font["name"]
    copyright_text = source_names.getDebugName(0)
    license_text = source_names.getDebugName(13)
    license_url = source_names.getDebugName(14)
    if copyright_text:
        value = f"{copyright_text} Modified for DAoC legacy Hangul composition."
        font["name"].setName(value, 0, 3, 1, 0x409)
    if license_text:
        font["name"].setName(license_text, 13, 3, 1, 0x409)
    if license_url:
        font["name"].setName(license_url, 14, 3, 1, 0x409)
    if "OS/2" in font and "OS/2" in source_font:
        font["OS/2"].usWeightClass = source_font["OS/2"].usWeightClass
        if is_bold:
            font["OS/2"].fsSelection |= 1 << 5
            font["OS/2"].fsSelection &= ~(1 << 6)
    if is_bold:
        font["head"].macStyle |= 1


def validate(font: TTFont) -> None:
    cmap = font.getBestCmap()
    if 0x20 in component_map() or 0x20 in NO_FINAL_INITIAL_CODEPOINTS:
        raise RuntimeError("U+0020 must never be an extended Hangul component")

    space_name = cmap.get(0x20)
    if space_name is None:
        raise RuntimeError("U+0020 space glyph is missing")
    if cmap.get(0xA0) == space_name:
        raise RuntimeError("U+0020 space and U+00A0 no-final medial share a glyph")

    space_advance = font["hmtx"].metrics[space_name][0]
    font["glyf"][space_name] = TTGlyphPen(None).glyph()
    font["hmtx"].metrics[space_name] = (space_advance, 0)
    if font["glyf"][space_name].numberOfContours != 0:
        raise RuntimeError("U+0020 is not an empty space glyph")

    for target_codepoint in component_map():
        glyph_name = cmap.get(target_codepoint)
        if glyph_name is None:
            raise RuntimeError(f"missing legacy component U+{target_codepoint:04X}")
        glyph = font["glyf"][glyph_name]
        glyph.recalcBounds(font["glyf"])
        if glyph.numberOfContours == 0:
            raise RuntimeError(f"empty legacy component U+{target_codepoint:04X}")

    for index, (target_codepoint, raw_alias) in enumerate(
        zip(NO_FINAL_INITIAL_CODEPOINTS, NO_FINAL_INITIAL_RAW_ALIASES, strict=True)
    ):
        expected_name = f"d2ExtNoFinalCho{index:02d}"
        if cmap.get(target_codepoint) != expected_name:
            raise RuntimeError(f"missing extended choseong U+{target_codepoint:04X}")
        if cmap.get(raw_alias) != expected_name:
            raise RuntimeError(
                f"missing DAoC raw alias U+{raw_alias:04X} for U+{target_codepoint:04X}"
            )
        for cmap_table in font["cmap"].tables:
            if raw_alias <= 0xFF and cmap_table.cmap.get(raw_alias) != expected_name:
                raise RuntimeError(
                    f"cmap {cmap_table.platformID}/{cmap_table.platEncID} "
                    f"is missing raw alias U+{raw_alias:04X}"
                )
        glyph = font["glyf"][expected_name]
        glyph.recalcBounds(font["glyf"])
        if glyph.numberOfContours == 0:
            raise RuntimeError(f"empty extended choseong U+{target_codepoint:04X}")
        if glyph.yMin != NO_FINAL_BOTTOM or glyph.yMax != NO_FINAL_TOP:
            raise RuntimeError(
                f"bad bounds for U+{target_codepoint:04X}: {glyph.yMin}..{glyph.yMax}"
            )


def build(template_path: Path, source_path: Path, output_path: Path) -> None:
    template_font = TTFont(template_path)
    source_font = TTFont(source_path)
    output_font = TTFont(template_path, recalcBBoxes=True, recalcTimestamp=False)

    for target_codepoint, source_codepoint in component_map().items():
        replace_component_outline(
            output_font,
            template_font,
            source_font,
            target_codepoint,
            source_codepoint,
        )

    source_initial_codepoints = [encode_initial(code) for code in INITIAL_CODES]
    for index, values in enumerate(zip(
        source_initial_codepoints,
        NO_FINAL_INITIAL_CODEPOINTS,
        NO_FINAL_INITIAL_RAW_ALIASES,
        strict=True,
    )):
        add_no_final_initial(
            output_font,
            template_font,
            *values,
            index,
        )

    set_names(output_font, source_font)
    validate(output_font)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_font.save(output_path, reorderTables=True)

    # Re-open the serialized font so table compilation errors cannot hide.
    written = TTFont(output_path, recalcBBoxes=True, recalcTimestamp=False)
    validate(written)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--template", required=True, type=Path)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    build(args.template, args.source, args.output)


if __name__ == "__main__":
    main()
