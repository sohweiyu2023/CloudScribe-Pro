from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
APP_PROJECT = ROOT / "src" / "CloudScribe.App" / "CloudScribe.App.csproj"
SHELL = ROOT / "src" / "CloudScribe.App" / "ViewModels" / "ShellViewModel.cs"
PRICING = ROOT / "src" / "CloudScribe.App" / "ViewModels" / "ShellViewModel.Pricing.cs"
COMPOSITION = ROOT / "src" / "CloudScribe.App" / "Composition" / "CompositionRoot.cs"


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def test_final_app_is_stamped_exactly_1_0_0():
    project = _read(APP_PROJECT)
    assert "<Version>1.0.0</Version>" in project
    assert "<InformationalVersion>1.0.0</InformationalVersion>" in project
    assert "<FileVersion>1.0.0.0</FileVersion>" in project
    assert "<AssemblyVersion>1.0.0.0</AssemblyVersion>" in project
    assert "stage" not in re.search(r"<InformationalVersion>(.*?)</InformationalVersion>", project).group(1).lower()


def test_final_shell_contains_no_obsolete_stage_placeholder_contracts():
    shell = _read(SHELL)
    pricing = _read(PRICING)
    forbidden = (
        "Durable document creation arrives in Stage 3",
        "Retry and recovery actions become durable in Stage 3",
        "Generation remains gated until the durable Stage 5 engine exists",
        "Document creation and import arrive in Stage 3",
        "The audio engine and player are introduced in Stage 5",
        "exact v2.22 schema/seed bytes",
        "exact schema 1.1.5/seed bytes still required",
    )
    corpus = shell + "\n" + pricing
    present = [text for text in forbidden if text in corpus]
    assert not present, f"Final production shell still contains obsolete staged placeholders: {present}"


def test_final_shell_keeps_fail_closed_pricing_language():
    shell = _read(SHELL)
    pricing = _read(PRICING)
    corpus = shell + "\n" + pricing
    assert "activation is never automatic" in corpus.lower()
    assert "No active pricing catalog" in corpus
    assert "no provider call was attempted" in corpus


def test_stage3_document_and_import_are_composed_in_production_shell():
    composition = _read(COMPOSITION)
    assert "ConfigureStage3DocumentWorkflow" in composition
    assert "ConfigureStage3ImportWorkflow" in composition
    assert "ScheduleDocumentWorkspaceStart" in composition


def test_final_release_must_not_regress_to_known_prerelease_stamp():
    corpus = "\n".join(
        _read(path)
        for path in (APP_PROJECT, SHELL, PRICING, COMPOSITION)
    )
    assert "0.5.0-stage4-foundation-batch16" not in corpus
