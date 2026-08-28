from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
APP_PROJECT = ROOT / "src" / "CloudScribe.App" / "CloudScribe.App.csproj"
SHELL = ROOT / "src" / "CloudScribe.App" / "ViewModels" / "ShellViewModel.cs"
PRICING = ROOT / "src" / "CloudScribe.App" / "ViewModels" / "ShellViewModel.Pricing.cs"
FINAL_PRESENTATION = ROOT / "src" / "CloudScribe.App" / "ViewModels" / "ShellViewModel.FinalReleasePresentation.cs"
STAGE3_MOUNT = ROOT / "src" / "CloudScribe.App" / "MainWindow.Stage3Library.cs"
STAGE7_VOICE_LAB = ROOT / "src" / "CloudScribe.App" / "ViewModels" / "ShellViewModel.Stage7VoiceLab.cs"
STAGE8_RESTORE = ROOT / "src" / "CloudScribe.App" / "ViewModels" / "ShellViewModel.Stage8RestoreRecovery.cs"
COMPOSITION = ROOT / "src" / "CloudScribe.App" / "Composition" / "CompositionRoot.cs"


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def test_final_app_is_stamped_exactly_1_0_0():
    project = _read(APP_PROJECT)
    assert "<Version>1.0.0</Version>" in project
    assert "<InformationalVersion>1.0.0</InformationalVersion>" in project
    assert "<FileVersion>1.0.0.0</FileVersion>" in project
    assert "<AssemblyVersion>1.0.0.0</AssemblyVersion>" in project
    informational = re.search(r"<InformationalVersion>(.*?)</InformationalVersion>", project)
    assert informational is not None
    assert "stage" not in informational.group(1).lower()
    assert "0.5.0-stage4-foundation-batch16" not in project


def test_active_final_presentation_contains_no_obsolete_stage_contracts():
    final_presentation = _read(FINAL_PRESENTATION)
    forbidden = (
        "arrive in Stage 3",
        "arrives in Stage 3",
        "introduced in Stage 5",
        "generation engine incomplete",
        "generation engine are complete",
        "exact v2.22",
        "schema 1.1.5/seed",
        "STAGE 2 PREVIEW",
        "UNSAVED PREVIEW",
    )
    present = [text for text in forbidden if text.lower() in final_presentation.lower()]
    assert not present, f"Active Final presentation still contains staged placeholders: {present}"


def test_final_presentation_truthfully_exposes_completed_local_workflows():
    final_presentation = _read(FINAL_PRESENTATION)
    required = (
        "Create and import durable local projects",
        "TXT, Markdown, HTML, DOCX, and clipboard text",
        "autosave, checkpoints, search, rename, delete, and recovery",
        "Deterministic local WAV synthesis",
        "resumable manifests",
        "Resumable local generation and recovery",
    )
    missing = [text for text in required if text not in final_presentation]
    assert not missing, f"Final presentation does not expose completed workflows: {missing}"


def test_final_shell_keeps_fail_closed_pricing_provider_spend_language():
    final_presentation = _read(FINAL_PRESENTATION)
    pricing = _read(PRICING)
    corpus = final_presentation + "\n" + pricing
    required = (
        "activation is never automatic",
        "No active pricing catalog",
        "no provider call was attempted",
        "billable approval remains blocked",
        "spend approval",
    )
    missing = [text for text in required if text.lower() not in corpus.lower()]
    assert not missing, f"Fail-closed Final language weakened or missing: {missing}"


def test_async_pricing_refresh_cannot_restore_legacy_admission_copy():
    pricing = _read(PRICING)
    forbidden = (
        "exact v2.22",
        "exact schema 1.1.5/seed",
        "seed bytes still required",
    )
    present = [text for text in forbidden if text.lower() in pricing.lower()]
    assert not present, f"Async pricing state can restore legacy admission copy: {present}"
    assert "billable approval remains blocked" in pricing
    assert "activation is never automatic" in pricing


def test_legacy_workspace_copy_is_only_replacement_input_and_is_reapplied():
    mount = _read(STAGE3_MOUNT)
    required = (
        "ReplaceLegacyStage2WorkspaceCopy(window, viewModel);",
        "window.Opened += HandleStage3WindowOpened;",
        "nameof(ShellViewModel.LifecycleDescription)",
        'case "STAGE 2 PREVIEW":',
        'textBlock.Text = "LOCAL AUTOSAVE";',
        'case "UNSAVED PREVIEW":',
        "DocumentSaveState",
        "FinalEmptyLifecycleDescription",
        "FinalErrorLifecycleDescription",
    )
    missing = [text for text in required if text not in mount]
    assert not missing, f"Legacy workspace replacement path is incomplete: {missing}"


def test_stage3_document_import_and_final_presentation_are_composed_in_production_shell():
    composition = _read(COMPOSITION)
    required = (
        "ConfigureStage3DocumentWorkflow",
        "ConfigureStage3ImportWorkflow",
        "ScheduleDocumentWorkspaceStart",
        "ApplyFinalReleasePresentation",
    )
    missing = [text for text in required if text not in composition]
    assert not missing, f"Production composition is missing required Final wiring: {missing}"


def test_stage7_voice_lab_and_stage8_restore_are_real_production_wiring_not_dead_code():
    composition = _read(COMPOSITION)
    stage7 = _read(STAGE7_VOICE_LAB)
    stage8 = _read(STAGE8_RESTORE)

    assert "ConfigureStage7VoiceLabCatalog" in stage7
    assert "ConfigureStage7VoiceLabAudition" in stage7
    assert "ConfigureStage8RestoreRecovery" in stage8

    required_composition = (
        "ConfigureStage7VoiceLabCatalog",
        "ConfigureStage7VoiceLabAudition",
        "ConfigureStage8RestoreRecovery",
    )
    missing = [text for text in required_composition if text not in composition]
    assert not missing, (
        "Stage7/8 implementations exist but are not wired into the production shell: "
        f"{missing}"
    )


def test_known_prerelease_stamp_is_absent_from_active_production_sources():
    corpus = "\n".join(
        _read(path)
        for path in (APP_PROJECT, SHELL, PRICING, FINAL_PRESENTATION, COMPOSITION)
    )
    assert "0.5.0-stage4-foundation-batch16" not in corpus
