#!/usr/bin/env python3
"""Validate Stage 2 runtime screenshot evidence without third-party packages."""

from __future__ import annotations

import hashlib
import json
import pathlib
import struct
import sys
import zlib
from datetime import datetime, timedelta, timezone

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
MAX_SCREENSHOT_BYTES = 25 * 1024 * 1024
MAX_MANIFEST_BYTES = 256 * 1024
MAX_EVIDENCE_ENTRIES = 32
MAX_PNG_WIDTH = 4096
MAX_PNG_HEIGHT = 4096
MAX_PNG_PIXELS = 8_000_000
MAX_PNG_CHUNKS = 4096
MAX_EVIDENCE_AGE = timedelta(hours=2)
KNOWN_CRITICAL_CHUNKS = {b"IHDR", b"PLTE", b"IDAT", b"IEND"}
SOURCE_ROOT = pathlib.Path(__file__).resolve().parents[1]
EXPECTED_CAPTURE_SURFACE = "Avalonia.RenderTargetBitmap"
EXPECTED_CAPTURE_BITMAP_DPI = 96.0
EXPECTED_TYPOGRAPHY_SCALE_METHOD = "semantic-resource-multiplier"
MIN_TEXT_CONTRAST_RATIO = 4.5
MIN_CARET_CONTRAST_RATIO = 3.0
EXPECTED_CASES = {
    "01-full-follow-system-dark-pointer-focus": (1600, 1000, "FollowSystem", "Offline", "Studio", False, False, True, True, True, False, 1.0),
    "02-standard-cosmic-paper-ready": (1280, 900, "CosmicPaper", "Ready", "Studio", False, False, False, False, None, False, 1.0),
    "03-compact-cosmic-night-loading": (980, 820, "CosmicNight", "Loading", "Studio", False, False, False, False, None, False, 1.0),
    "04-narrow-cosmic-paper-empty": (700, 820, "CosmicPaper", "Empty", "Studio", False, False, False, False, None, False, 1.0),
    "05-standard-high-contrast-error": (1280, 900, "HighContrast", "Error", "Studio", False, False, True, False, None, False, 1.0),
    "06-full-focus-reading": (1500, 950, "CosmicNight", "Ready", "Studio", True, False, True, False, None, False, 1.0),
    "07-compact-follow-system-light-pointer-focus": (980, 820, "FollowSystem", "Offline", "Studio", False, False, True, True, False, False, 1.0),
    "08-narrow-navigation-drawer": (700, 820, "CosmicNight", "Offline", "Studio", False, True, False, False, None, False, 1.0),
    "09-standard-route-empty-state": (1280, 900, "CosmicPaper", "Ready", "Library", False, False, False, False, None, False, 1.0),
    "10-standard-reduced-motion": (1280, 900, "CosmicNight", "Ready", "Settings", False, False, False, False, None, True, 1.0),
    "11-full-text-scale-125": (1600, 1000, "CosmicPaper", "Ready", "Studio", False, False, True, False, None, False, 1.25),
    "12-full-text-scale-150": (1600, 1000, "CosmicPaper", "Ready", "Studio", False, False, True, False, None, False, 1.5),
    "13-full-text-scale-175": (1600, 1000, "CosmicPaper", "Ready", "Studio", False, False, True, False, None, False, 1.75),
    "14-full-text-scale-200": (1600, 1000, "CosmicPaper", "Ready", "Studio", False, False, True, False, None, False, 2.0),
    "15-narrow-text-scale-200": (700, 900, "CosmicPaper", "Ready", "Studio", False, False, True, False, None, False, 2.0),
    "16-minimum-window-cosmic-night": (520, 700, "CosmicNight", "Offline", "Studio", False, False, False, False, None, False, 1.0),
    "17-minimum-window-text-scale-200": (520, 820, "CosmicPaper", "Ready", "Studio", False, False, False, False, None, False, 2.0),
}


class EvidenceError(ValueError):
    pass


def read_bounded_bytes(path: pathlib.Path, maximum_bytes: int, label: str) -> bytes:
    with path.open("rb") as stream:
        payload = stream.read(maximum_bytes + 1)
    if len(payload) > maximum_bytes:
        raise EvidenceError(f"{label} exceeds {maximum_bytes} bytes")
    return payload


def bounded_directory_entries(root: pathlib.Path) -> list[pathlib.Path]:
    entries: list[pathlib.Path] = []
    for path in root.iterdir():
        if len(entries) >= MAX_EVIDENCE_ENTRIES:
            raise EvidenceError(
                f"visual evidence directory contains more than {MAX_EVIDENCE_ENTRIES} entries"
            )
        if path.is_symlink():
            raise EvidenceError(f"visual evidence directory contains symbolic-link entry {path.name!r}")
        entries.append(path)
    return entries


def sha256(path: pathlib.Path) -> str:
    with path.open("rb") as stream:
        digest = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
        return digest.hexdigest()


def paeth(left: int, above: int, upper_left: int) -> int:
    prediction = left + above - upper_left
    left_distance = abs(prediction - left)
    above_distance = abs(prediction - above)
    upper_left_distance = abs(prediction - upper_left)
    if left_distance <= above_distance and left_distance <= upper_left_distance:
        return left
    if above_distance <= upper_left_distance:
        return above
    return upper_left


def parse_png(path: pathlib.Path, include_center_brightness: bool = False):
    if path.is_symlink():
        raise EvidenceError(f"{path.name}: symbolic-link screenshots are not accepted")
    payload = read_bounded_bytes(path, MAX_SCREENSHOT_BYTES, f"{path.name}: screenshot")
    if len(payload) < 64 or not payload.startswith(PNG_SIGNATURE):
        raise EvidenceError(f"{path.name}: invalid or truncated PNG signature")

    position = len(PNG_SIGNATURE)
    width = height = bit_depth = color_type = interlace = None
    idat_parts: list[bytes] = []
    saw_iend = False
    chunk_index = 0
    while position + 12 <= len(payload):
        if chunk_index >= MAX_PNG_CHUNKS:
            raise EvidenceError(f"{path.name}: PNG contains more than {MAX_PNG_CHUNKS} chunks")
        length = struct.unpack(">I", payload[position : position + 4])[0]
        chunk_type = payload[position + 4 : position + 8]
        if len(chunk_type) != 4 or any(not (65 <= value <= 90 or 97 <= value <= 122) for value in chunk_type):
            raise EvidenceError(f"{path.name}: invalid PNG chunk type {chunk_type!r}")
        if chunk_type[0] & 0x20 == 0 and chunk_type not in KNOWN_CRITICAL_CHUNKS:
            raise EvidenceError(f"{path.name}: unknown critical PNG chunk {chunk_type!r}")
        data_start = position + 8
        data_end = data_start + length
        crc_end = data_end + 4
        if crc_end > len(payload):
            raise EvidenceError(f"{path.name}: truncated PNG chunk")
        data = payload[data_start:data_end]
        expected_crc = struct.unpack(">I", payload[data_end:crc_end])[0]
        actual_crc = zlib.crc32(chunk_type + data) & 0xFFFFFFFF
        if actual_crc != expected_crc:
            raise EvidenceError(f"{path.name}: PNG CRC mismatch in {chunk_type!r}")

        if chunk_index == 0 and chunk_type != b"IHDR":
            raise EvidenceError(f"{path.name}: IHDR must be the first PNG chunk")
        if chunk_type == b"IHDR":
            if width is not None:
                raise EvidenceError(f"{path.name}: duplicate IHDR chunk")
            if length != 13:
                raise EvidenceError(f"{path.name}: invalid IHDR length")
            width, height, bit_depth, color_type, compression, filtering, interlace = struct.unpack(
                ">IIBBBBB", data
            )
            if width < 1 or height < 1:
                raise EvidenceError(f"{path.name}: invalid dimensions {width}x{height}")
            if width > MAX_PNG_WIDTH or height > MAX_PNG_HEIGHT or width * height > MAX_PNG_PIXELS:
                raise EvidenceError(
                    f"{path.name}: PNG dimensions {width}x{height} exceed the bounded evidence geometry"
                )
            if compression != 0 or filtering != 0:
                raise EvidenceError(f"{path.name}: unsupported PNG compression/filter method")
        elif chunk_type == b"IDAT":
            idat_parts.append(data)
        elif chunk_type == b"IEND":
            if length != 0:
                raise EvidenceError(f"{path.name}: invalid IEND length")
            if crc_end != len(payload):
                raise EvidenceError(f"{path.name}: trailing bytes after IEND are not accepted")
            saw_iend = True
            break
        position = crc_end
        chunk_index += 1

    if width is None or height is None or not idat_parts or not saw_iend:
        raise EvidenceError(f"{path.name}: incomplete PNG structure")
    if bit_depth != 8 or color_type not in (2, 6) or interlace != 0:
        raise EvidenceError(
            f"{path.name}: expected non-interlaced 8-bit RGB/RGBA PNG, "
            f"found bit depth {bit_depth}, color type {color_type}, interlace {interlace}"
        )

    channels = 3 if color_type == 2 else 4
    stride = width * channels
    expected_length = height * (stride + 1)
    compressed = b"".join(idat_parts)
    try:
        decompressor = zlib.decompressobj()
        filtered = decompressor.decompress(compressed, expected_length + 1)
        if decompressor.unconsumed_tail or len(filtered) > expected_length:
            raise EvidenceError(f"{path.name}: decompressed PNG exceeds its bounded expected size")
        filtered += decompressor.flush(expected_length + 1 - len(filtered))
        if decompressor.unused_data or not decompressor.eof:
            raise EvidenceError(f"{path.name}: PNG contains trailing or incomplete compressed image data")
    except zlib.error as exc:
        raise EvidenceError(f"{path.name}: IDAT decompression failed: {exc}") from exc
    if len(filtered) != expected_length:
        raise EvidenceError(
            f"{path.name}: unexpected decompressed length {len(filtered)}; expected {expected_length}"
        )

    previous = bytearray(stride)
    offset = 0
    sampled_pixels: set[bytes] = set()
    sample_step = max(1, (width * height) // 200_000)
    pixel_number = 0
    center_bright = 0
    center_sampled = 0
    center_x0 = int(width * 0.32)
    center_x1 = int(width * 0.76)
    center_y0 = int(height * 0.25)
    center_y1 = int(height * 0.72)
    for row_index in range(height):
        filter_type = filtered[offset]
        offset += 1
        encoded = filtered[offset : offset + stride]
        offset += stride
        decoded = bytearray(stride)
        for index, value in enumerate(encoded):
            left = decoded[index - channels] if index >= channels else 0
            above = previous[index]
            upper_left = previous[index - channels] if index >= channels else 0
            if filter_type == 0:
                result = value
            elif filter_type == 1:
                result = (value + left) & 0xFF
            elif filter_type == 2:
                result = (value + above) & 0xFF
            elif filter_type == 3:
                result = (value + ((left + above) // 2)) & 0xFF
            elif filter_type == 4:
                result = (value + paeth(left, above, upper_left)) & 0xFF
            else:
                raise EvidenceError(f"{path.name}: invalid PNG filter type {filter_type}")
            decoded[index] = result

        for index in range(0, stride, channels):
            column_index = index // channels
            if (
                center_x0 <= column_index < center_x1
                and center_y0 <= row_index < center_y1
                and (column_index - center_x0) % 4 == 0
                and (row_index - center_y0) % 4 == 0
            ):
                red, green, blue = decoded[index], decoded[index + 1], decoded[index + 2]
                center_sampled += 1
                if ((red * 2126) + (green * 7152) + (blue * 722)) >= (180 * 10000):
                    center_bright += 1
            if pixel_number % sample_step == 0 and len(sampled_pixels) < 32:
                sampled_pixels.add(bytes(decoded[index : index + channels]))
            pixel_number += 1
        previous = decoded

    if len(sampled_pixels) < 16:
        raise EvidenceError(
            f"{path.name}: screenshot has only {len(sampled_pixels)} sampled colors; "
            "blank or non-rendered evidence is not accepted"
        )
    if include_center_brightness:
        if center_sampled <= 0:
            raise EvidenceError(f"{path.name}: center brightness region produced no samples")
        return width, height, len(sampled_pixels), center_bright / center_sampled
    return width, height, len(sampled_pixels)


def reject_duplicate_members(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise EvidenceError(f"duplicate JSON member {key!r} is not accepted")
        result[key] = value
    return result


def require_string(item: dict[str, object], key: str, case_name: str) -> str:
    value = item.get(key)
    if not isinstance(value, str) or not value:
        raise EvidenceError(f"{case_name}: manifest field {key!r} must be a non-empty string")
    return value



def parse_rgb(value: object, field: str, case_name: str) -> tuple[int, int, int]:
    if not isinstance(value, str) or len(value) != 7 or not value.startswith("#"):
        raise EvidenceError(f"{case_name}: editor visual field {field!r} must be #RRGGBB")
    try:
        return tuple(int(value[index : index + 2], 16) for index in (1, 3, 5))  # type: ignore[return-value]
    except ValueError as exc:
        raise EvidenceError(f"{case_name}: editor visual field {field!r} must be hexadecimal #RRGGBB") from exc


def relative_luminance(rgb: tuple[int, int, int]) -> float:
    channels: list[float] = []
    for channel in rgb:
        normalized = channel / 255.0
        channels.append(
            normalized / 12.92
            if normalized <= 0.04045
            else ((normalized + 0.055) / 1.055) ** 2.4
        )
    return (0.2126 * channels[0]) + (0.7152 * channels[1]) + (0.0722 * channels[2])


def contrast_ratio(first: tuple[int, int, int], second: tuple[int, int, int]) -> float:
    lighter, darker = sorted((relative_luminance(first), relative_luminance(second)), reverse=True)
    return (lighter + 0.05) / (darker + 0.05)


def validate_editor_visual_audit(case: dict[str, object], case_name: str) -> None:
    audit = case.get("EditorVisualAudit")
    if not isinstance(audit, dict):
        raise EvidenceError(f"{case_name}: EditorVisualAudit must be an object")

    focused = audit.get("Focused")
    expected_focused = case.get("EditorFocused")
    if not isinstance(focused, bool) or focused is not expected_focused:
        raise EvidenceError(
            f"{case_name}: editor actual focus {focused!r} does not match manifest EditorFocused {expected_focused!r}"
        )

    foreground = parse_rgb(audit.get("Foreground"), "Foreground", case_name)
    surface = parse_rgb(audit.get("SurfaceBackground"), "SurfaceBackground", case_name)
    caret = parse_rgb(audit.get("Caret"), "Caret", case_name)
    selection_background = parse_rgb(
        audit.get("SelectionBackground"), "SelectionBackground", case_name
    )
    selection_foreground = parse_rgb(
        audit.get("SelectionForeground"), "SelectionForeground", case_name
    )
    placeholder = parse_rgb(
        audit.get("PlaceholderForeground"), "PlaceholderForeground", case_name
    )

    checks = (
        ("text", contrast_ratio(foreground, surface), MIN_TEXT_CONTRAST_RATIO),
        ("placeholder", contrast_ratio(placeholder, surface), MIN_TEXT_CONTRAST_RATIO),
        ("caret", contrast_ratio(caret, surface), MIN_CARET_CONTRAST_RATIO),
        (
            "selected text",
            contrast_ratio(selection_foreground, selection_background),
            MIN_TEXT_CONTRAST_RATIO,
        ),
    )
    for label, ratio, minimum in checks:
        if ratio + 1e-9 < minimum:
            raise EvidenceError(
                f"{case_name}: editor {label} contrast {ratio:.2f}:1 is below {minimum:.1f}:1"
            )


def validate_generated_at(value: object, current_instant: datetime | None = None) -> datetime:
    if not isinstance(value, str):
        raise EvidenceError("generated_at_utc must be an ISO-8601 string")
    try:
        generated_instant = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise EvidenceError("generated_at_utc is not valid ISO-8601") from exc
    if generated_instant.tzinfo is None or generated_instant.utcoffset() != timedelta(0):
        raise EvidenceError("generated_at_utc must identify a UTC instant")
    now = current_instant or datetime.now(generated_instant.tzinfo)
    if generated_instant > now + timedelta(minutes=5):
        raise EvidenceError("generated_at_utc is implausibly far in the future")
    if generated_instant < now - MAX_EVIDENCE_AGE:
        raise EvidenceError("generated_at_utc is stale; runtime evidence must come from the current promotion run")
    return generated_instant

def validate_capture_truth_boundary(manifest: dict[str, object]) -> None:
    if manifest.get("capture_surface") != EXPECTED_CAPTURE_SURFACE:
        raise EvidenceError(
            f"capture_surface must be {EXPECTED_CAPTURE_SURFACE!r}; automated evidence must identify its render surface"
        )
    for key in ("capture_bitmap_dpi_x", "capture_bitmap_dpi_y"):
        value = manifest.get(key)
        if not isinstance(value, (int, float)) or isinstance(value, bool) or float(value) != EXPECTED_CAPTURE_BITMAP_DPI:
            raise EvidenceError(f"{key} must truthfully identify the fixed 96-DPI capture bitmap")
    if manifest.get("typography_scale_method") != EXPECTED_TYPOGRAPHY_SCALE_METHOD:
        raise EvidenceError(
            "typography_scale_method must identify semantic-resource multiplication rather than OS text scaling"
        )
    for key in (
        "operating_system_text_scale_verified",
        "mixed_dpi_verified",
        "windows_accessibility_verified",
    ):
        if manifest.get(key) is not False:
            raise EvidenceError(
                f"{key} must remain false for automated RenderTargetBitmap evidence; separate platform evidence is required"
            )


def validate(root: pathlib.Path) -> None:
    if root.is_symlink():
        raise EvidenceError("visual evidence directory must not be a symbolic link")
    manifest_path = root / "visual-evidence-manifest.json"
    if manifest_path.is_symlink():
        raise EvidenceError("visual-evidence-manifest.json must not be a symbolic link")
    if not manifest_path.is_file():
        raise EvidenceError("visual-evidence-manifest.json is missing")
    manifest_payload = read_bounded_bytes(
        manifest_path,
        MAX_MANIFEST_BYTES,
        "visual evidence manifest",
    )
    manifest = json.loads(
        manifest_payload.decode("utf-8"),
        object_pairs_hook=reject_duplicate_members,
    )
    directory_entries = bounded_directory_entries(root)
    if manifest.get("schema") != "cloudscribe-stage2-visual-evidence-1.2":
        raise EvidenceError("unexpected visual evidence schema")
    current_state = json.loads((SOURCE_ROOT / "SESSION_STATE.json").read_text(encoding="utf-8"))
    expected_repository_version = current_state.get("repository_version")
    if manifest.get("repository_version") != expected_repository_version:
        raise EvidenceError(
            "visual evidence repository_version does not match the current SESSION_STATE.json"
        )
    expected_source_manifest_sha256 = sha256(SOURCE_ROOT / "SHA256SUMS.txt")
    if manifest.get("source_manifest_sha256") != expected_source_manifest_sha256:
        raise EvidenceError(
            "visual evidence source_manifest_sha256 does not match the current SHA256SUMS.txt"
        )
    if manifest.get("runtime_platform") not in {"Windows", "Linux"}:
        raise EvidenceError("runtime_platform must identify the executed Windows or Linux capture path")
    runtime_framework = manifest.get("runtime_framework")
    if not isinstance(runtime_framework, str) or not runtime_framework.startswith(".NET 10."):
        raise EvidenceError("runtime_framework must identify the executed .NET 10 runtime")
    if manifest.get("runtime_evidence") is not True or manifest.get("concept_art") is not False:
        raise EvidenceError("manifest must identify real runtime evidence and reject concept-art substitution")
    validate_capture_truth_boundary(manifest)
    validate_generated_at(manifest.get("generated_at_utc"))

    cases = manifest.get("cases")
    if not isinstance(cases, list) or len(cases) != len(EXPECTED_CASES):
        raise EvidenceError(f"expected {len(EXPECTED_CASES)} manifest cases")

    case_names: list[str] = []
    screenshot_hashes: set[str] = set()
    referenced_files: set[str] = set()
    for case in cases:
        if not isinstance(case, dict):
            raise EvidenceError("each manifest case must be an object")
        name = require_string(case, "Name", "case")
        file_name = require_string(case, "File", name)
        if pathlib.Path(file_name).name != file_name or not file_name.endswith(".png"):
            raise EvidenceError(f"{name}: unsafe or non-PNG file name {file_name!r}")
        if file_name != f"{name}.png":
            raise EvidenceError(f"{name}: screenshot file name does not match case name")
        width = case.get("Width")
        height = case.get("Height")
        if not isinstance(width, int) or not isinstance(height, int):
            raise EvidenceError(f"{name}: width and height must be integers")
        expected = EXPECTED_CASES.get(name)
        if expected is None:
            raise EvidenceError(f"{name}: unexpected visual evidence case")
        (
            expected_width,
            expected_height,
            theme,
            lifecycle,
            route,
            focus_reading,
            navigation_drawer,
            editor_focused,
            pointer_over_editor,
            system_uses_dark,
            reduced_motion,
            typography_scale,
        ) = expected
        expected_metadata = {
            "Width": expected_width,
            "Height": expected_height,
            "Theme": theme,
            "LifecycleState": lifecycle,
            "Route": route,
            "FocusReading": focus_reading,
            "NavigationDrawer": navigation_drawer,
            "EditorFocused": editor_focused,
            "PointerOverEditor": pointer_over_editor,
            "ReducedMotion": reduced_motion,
            "TypographyScale": typography_scale,
        }
        if system_uses_dark is not None:
            expected_metadata["SystemUsesDark"] = system_uses_dark
        elif not isinstance(case.get("SystemUsesDark"), bool):
            raise EvidenceError(f"{name}: manifest field 'SystemUsesDark' must be boolean")
        for key, expected_value in expected_metadata.items():
            if case.get(key) != expected_value:
                raise EvidenceError(
                    f"{name}: manifest field {key!r} is {case.get(key)!r}; expected {expected_value!r}"
                )
        validate_editor_visual_audit(case, name)
        screenshot = root / file_name
        if screenshot.is_symlink():
            raise EvidenceError(f"{name}: symbolic-link screenshots are not accepted")
        if not screenshot.is_file():
            raise EvidenceError(f"{name}: screenshot is missing")
        screenshot_size = screenshot.stat().st_size
        if screenshot_size < 4096:
            raise EvidenceError(f"{name}: screenshot is implausibly small ({screenshot_size} bytes)")
        if screenshot_size > MAX_SCREENSHOT_BYTES:
            raise EvidenceError(f"{name}: screenshot exceeds the bounded evidence size ({screenshot_size} bytes)")
        actual_hash = sha256(screenshot)
        recorded_hash = require_string(case, "Sha256", name).lower()
        if actual_hash != recorded_hash:
            raise EvidenceError(f"{name}: SHA-256 does not match the manifest")
        png_width, png_height, _, center_bright_fraction = parse_png(
            screenshot, include_center_brightness=True
        )
        if (png_width, png_height) != (width, height):
            raise EvidenceError(
                f"{name}: manifest dimensions {width}x{height} do not match PNG {png_width}x{png_height}"
            )
        case_names.append(name)
        referenced_files.add(file_name)
        if editor_focused and theme != "HighContrast" and center_bright_fraction < 0.55:
            raise EvidenceError(
                f"{name}: rendered editor/paper center is too dark "
                f"({center_bright_fraction:.1%} bright samples); focused/pointer-over paper surface regressed"
            )
        if actual_hash in screenshot_hashes:
            raise EvidenceError(f"{name}: duplicate screenshot bytes detected")
        screenshot_hashes.add(actual_hash)

    if case_names != list(EXPECTED_CASES):
        raise EvidenceError("visual evidence cases are missing, reordered, or renamed")
    png_entries = [path for path in directory_entries if path.name.endswith(".png")]
    actual_pngs = {path.name for path in png_entries if path.is_file()}
    if actual_pngs != referenced_files:
        raise EvidenceError("screenshot directory contains unreferenced or missing PNG files")


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: verify_stage2_visual_evidence.py <evidence-directory>", file=sys.stderr)
        return 2
    try:
        raw_root = pathlib.Path(sys.argv[1])
        if raw_root.is_symlink():
            raise EvidenceError("visual evidence directory must not be a symbolic link")
        validate(raw_root.resolve())
    except (EvidenceError, OSError, json.JSONDecodeError) as exc:
        print(f"Stage 2 visual evidence validation FAILED: {exc}", file=sys.stderr)
        return 1
    print("Stage 2 visual evidence validation PASSED (strict manifest, hashes, dimensions, distinct nonblank PNG content, editor contrast)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
