from __future__ import annotations

import argparse
import hashlib
import pathlib

PREIMAGE_SHA256 = {
    "src/CloudScribe.App/MainWindow.VisualCapture.cs": "d140d1836ee9070149afeafd6dd95c1e1f36deb2d3ab60033741f4b13c0d9a28",
    "src/CloudScribe.App/MainWindow.axaml": "146e7395924c757721de6e7e89d0a6f833192c861fd794ed2e64909ec0a9c65d",
    "tools/verify_stage2_source.py": "42bd813fa5e5d697fceec555e08b276cfc7da07df4c1f4b63d1eddb762d3fd57",
    "tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs": "2badcd169ecaed5444f7b12fcd20ff304f6d93081d90f1a9426ef9edd9abd2fc",
    "tools/run_python_regression_shards.py": "03d277226cdf42a49070ffd5104231c798c5cdaa0be223063f0cdea33fd2deda",
}

POSTIMAGE_SHA256 = {
    "src/CloudScribe.App/MainWindow.VisualCapture.cs": "79ff7f4023b7e3138dcf0d5e1bd09914e44d4f240fd69684db7f0dd358c57bfb",
    "src/CloudScribe.App/MainWindow.axaml": "137590eee91c7593a0b0e7239c2c8089cc830264a30ca91d734ba52ad0e7742d",
    "src/CloudScribe.App/Controls/PaperTextBox.cs": "16b3dad6f4edf29da02b8337c7f3270d943275c5a3e567f04fb1fdd2acd826af",
    "tools/verify_stage2_source.py": "b2d22b34a6940920d4b62d822fbca6c4c7e0e877d170fb7ac942a4d1d8827420",
    "tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs": "9313bf6a7681d0000c36b8af2ae4453f26e8ac9a8fc165c4334a458fec4054d5",
    "tools/run_python_regression_shards.py": "62c10f15a1f8e2c39796d3863e9b3f7e462f37da30999a5b2577b5703cf67ca5",
}


def sha256_file(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_lf(path: pathlib.Path) -> str:
    return path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")


def write_lf(path: pathlib.Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label} replacement expected exactly once; found {count}")
    return text.replace(old, new, 1)


def verify_preimages(root: pathlib.Path) -> None:
    for relative, expected in PREIMAGE_SHA256.items():
        path = root / relative
        if not path.is_file():
            raise RuntimeError(f"compile-fix preimage is missing: {relative}")
        actual = sha256_file(path)
        if actual != expected:
            raise RuntimeError(
                f"compile-fix preimage mismatch for {relative}: expected={expected} actual={actual}"
            )
    new_control = root / "src/CloudScribe.App/Controls/PaperTextBox.cs"
    if new_control.exists():
        raise RuntimeError(f"compile-fix new control unexpectedly already exists: {new_control}")


def patch_visual_capture(root: pathlib.Path) -> None:
    path = root / "src/CloudScribe.App/MainWindow.VisualCapture.cs"
    text = read_lf(path)
    text = replace_once(text, "using Avalonia.Input;\n", "", "unused Avalonia.Input import")
    text = replace_once(
        text,
        '''        string runtimePlatform = OperatingSystem.IsWindows()\n            ? "Windows"\n            : OperatingSystem.IsLinux()\n                ? "Linux"\n                : OperatingSystem.IsMacOS()\n                    ? "macOS"\n                    : "Unknown";\n''',
        '        string runtimePlatform = GetRuntimePlatform();\n',
        "runtime platform helper",
    )
    text = replace_once(
        text,
        '''    private static bool IsFatalVisualCaptureException(Exception exception) => exception is\n''',
        '''    private static string GetRuntimePlatform() =>\n        OperatingSystem.IsWindows()\n            ? "Windows"\n            : OperatingSystem.IsLinux()\n                ? "Linux"\n                : OperatingSystem.IsMacOS()\n                    ? "macOS"\n                    : "Unknown";\n\n    private static bool IsFatalVisualCaptureException(Exception exception) => exception is\n''',
        "runtime platform helper definition",
    )
    text = replace_once(
        text,
        '''            if (this is not IInputRoot inputRoot)\n            {\n                throw new InvalidOperationException("Stage 2 visual capture requires an Avalonia input root.");\n            }\n            inputRoot.PointerOverElement = captureCase.PointerOverEditor ? DocumentEditor : null;\n''',
        '            DocumentEditor.SetVisualCapturePointerOver(captureCase.PointerOverEditor);\n',
        "public-API-safe pointer pseudoclass seam",
    )
    write_lf(path, text)


def patch_window_theme(root: pathlib.Path) -> None:
    path = root / "src/CloudScribe.App/MainWindow.axaml"
    text = read_lf(path)
    text = replace_once(
        text,
        '    xmlns:input="using:CloudScribe.App.Input"\n',
        '    xmlns:input="using:CloudScribe.App.Input"\n    xmlns:controls="using:CloudScribe.App.Controls"\n',
        "PaperTextBox XML namespace",
    )
    text = replace_once(
        text,
        '        TargetType="TextBox"\n        BasedOn="{StaticResource {x:Type TextBox}}">',
        '        TargetType="controls:PaperTextBox"\n        BasedOn="{StaticResource {x:Type TextBox}}">',
        "PaperTextBox control theme target",
    )
    text = replace_once(
        text,
        '                  <TextBox\n                      Classes="document-title"',
        '                  <controls:PaperTextBox\n                      Classes="document-title"',
        "document title PaperTextBox",
    )
    text = replace_once(
        text,
        '                <TextBox\n                    x:Name="DocumentEditor"',
        '                <controls:PaperTextBox\n                    x:Name="DocumentEditor"',
        "document editor PaperTextBox",
    )
    write_lf(path, text)


def create_paper_text_box(root: pathlib.Path) -> None:
    path = root / "src/CloudScribe.App/Controls/PaperTextBox.cs"
    write_lf(
        path,
        '''using Avalonia.Controls;\n\nnamespace CloudScribe.App.Controls;\n\npublic sealed class PaperTextBox : TextBox\n{\n    internal void SetVisualCapturePointerOver(bool value) => PseudoClasses.Set(":pointerover", value);\n}\n''',
    )


def patch_stage2_verifier(root: pathlib.Path) -> None:
    path = root / "tools/verify_stage2_source.py"
    text = read_lf(path)
    text = replace_once(
        text,
        '''    if "PointerOverElement" not in capture or "SystemUsesDark" not in capture:\n        return fail("visual capture does not exercise real pointer-over plus deterministic Follow System state")\n''',
        '''    if "SetVisualCapturePointerOver" not in capture or "SystemUsesDark" not in capture:\n        return fail("visual capture does not exercise the bounded pointer-over pseudoclass seam plus deterministic Follow System state")\n''',
        "Stage 2 pointer-over source contract",
    )
    text = replace_once(
        text,
        '''    if "PaperTextBoxTheme" not in window or 'BasedOn="{StaticResource {x:Type TextBox}}"' not in window:\n        return fail("paper editor does not own a derived TextBox control theme")\n''',
        '''    if "PaperTextBoxTheme" not in window or 'TargetType="controls:PaperTextBox"' not in window or 'BasedOn="{StaticResource {x:Type TextBox}}"' not in window:\n        return fail("paper editor does not own a derived PaperTextBox control theme")\n''',
        "Stage 2 PaperTextBox theme contract",
    )
    write_lf(path, text)


def patch_architecture_tests(root: pathlib.Path) -> None:
    path = root / "tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs"
    text = read_lf(path)
    text = replace_once(
        text,
        '''        Assert.Contains("x:Key=\\"PaperTextBoxTheme\\"", window, StringComparison.Ordinal);\n        Assert.Contains("BasedOn=\\"{StaticResource {x:Type TextBox}}\\"", window, StringComparison.Ordinal);\n''',
        '''        Assert.Contains("x:Key=\\"PaperTextBoxTheme\\"", window, StringComparison.Ordinal);\n        Assert.Contains("TargetType=\\"controls:PaperTextBox\\"", window, StringComparison.Ordinal);\n        Assert.Contains("BasedOn=\\"{StaticResource {x:Type TextBox}}\\"", window, StringComparison.Ordinal);\n''',
        "architecture PaperTextBox target assertion",
    )
    text = replace_once(
        text,
        '        Assert.Contains("PointerOverElement", capture, StringComparison.Ordinal);\n',
        '        Assert.Contains("SetVisualCapturePointerOver", capture, StringComparison.Ordinal);\n',
        "architecture pointer pseudoclass seam assertion",
    )
    write_lf(path, text)


def patch_material_regressions(root: pathlib.Path) -> None:
    path = root / "tools/run_python_regression_shards.py"
    text = read_lf(path)
    text = replace_once(
        text,
        '        (capture, "01-full-follow-system-dark-pointer-focus", "Follow System dark pointer/focus case"),\n',
        '        (capture, "SetVisualCapturePointerOver", "bounded pointer-over pseudoclass seam"),\n',
        "material pointer pseudoclass seam marker",
    )
    write_lf(path, text)


def verify_postimages(root: pathlib.Path) -> None:
    for relative, expected in POSTIMAGE_SHA256.items():
        path = root / relative
        if not path.is_file():
            raise RuntimeError(f"compile-fix postimage is missing: {relative}")
        actual = sha256_file(path)
        if actual != expected:
            raise RuntimeError(
                f"compile-fix postimage mismatch for {relative}: expected={expected} actual={actual}"
            )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", required=True)
    args = parser.parse_args()
    root = pathlib.Path(args.source_root).resolve()
    if not (root / "CloudScribe.sln").is_file():
        raise RuntimeError(f"CloudScribe source root is invalid: {root}")

    verify_preimages(root)
    patch_visual_capture(root)
    patch_window_theme(root)
    create_paper_text_box(root)
    patch_stage2_verifier(root)
    patch_architecture_tests(root)
    patch_material_regressions(root)
    verify_postimages(root)
    print("CLOUDSCRIBE_STAGE2_FOCUS_COMPILE_FIX=PASS public_pointer_api=protected-pseudoclass-seam")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
