using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class GoogleTtsUsabilityHotfixBinder
{
    private static readonly JsonSerializerOptions ResumeDraftJson = new() { WriteIndented = true };
    private async Task ExportReservedResumeDraftAsync(Window dialog, ShellViewModel model, int ordinal, TextBlock status, CancellationToken token)
    {
        if (chirpPreparation is null || model.IsDocumentDirty || model.IsDocumentSaving)
            throw new InvalidOperationException("Save the exact original article before preparing a private resume review.");
        var selected = await dialog.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Select the retained historical article admission JSON for private review (no authority is installed)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Private article admission JSON") { Patterns = ["*.json"] }],
        }).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        if (selected.Count != 1) { status.Text = "Private review cancelled; reservation and capture unchanged."; return; }
        string historical = selected[0].TryGetLocalPath() ?? throw new InvalidOperationException("A local historical policy file is required.");
        long version = model.PrivateDiagnosticRequestVersion;
        var selection = model.CaptureGoogleTtsStudioRequestSelection();
        var policy = await ChirpArticleService.ReadHistoricalResumePolicyAsync(historical, token).ConfigureAwait(true);
        var draft = await chirpPreparation.CreateReservedResumeDraftAsync(selection, ordinal, policy, token).ConfigureAwait(true);
        if (version != model.PrivateDiagnosticRequestVersion || model.IsDocumentDirty || model.IsDocumentSaving
            || !string.Equals(ChirpResumeSelectionBinding.Hash(model.CaptureGoogleTtsStudioRequestSelection()), draft.SelectionSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The current selection changed during private catalog review.");
        var destination = await dialog.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Save a NEW private local review JSON; it contains financial and account binding metadata, not execution authority",
            SuggestedFileName = "CloudScribe-private-resume-review.json",
            FileTypeChoices = [new FilePickerFileType("Private review JSON") { Patterns = ["*.json"] }],
        }).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        if (destination is null) { status.Text = "Private review save cancelled; reservation and capture unchanged."; return; }
        string path = destination.TryGetLocalPath() ?? throw new InvalidOperationException("A local private destination is required.");
        await SaveReservedResumeDraftAsync(path, draft, token).ConfigureAwait(true);
        status.Text = "Private review saved locally. It grants NO authority and reserves nothing. Independent review, fresh exact approval and supported installer admission are required before revalidation. No capture or generation was started by this review; keep this file private.";
    }

    public static async Task SaveReservedResumeDraftAsync(string path, ChirpReservedResumeDraft draft, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string destination = ValidateResumeDraftDestination(path);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(draft, ResumeDraftJson);
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Private resume review exceeds its save bound.");
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        // Best-effort managed physical guard, shared with other private stores. Never delete or replace an existing file.
        _ = ValidateResumeDraftDestination(destination, fileOpened: true);
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
        _ = ValidateResumeDraftDestination(destination, fileOpened: true); output.Flush(flushToDisk: true);
    }

    private static string ValidateResumeDraftDestination(string path, bool fileOpened = false)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new InvalidOperationException("A physical local private destination is required.");
        _ = PhysicalDirectoryPolicy.ValidateExistsWithoutLinks(Path.GetDirectoryName(full)!, "Linked private review directories are refused.");
        var file = new FileInfo(full);
        if (file.LinkTarget is not null || file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Linked private review files are refused.");
        if (!fileOpened && file.Exists) throw new IOException("Choose a new private review file; existing files are preserved.");
        return full;
    }
}
