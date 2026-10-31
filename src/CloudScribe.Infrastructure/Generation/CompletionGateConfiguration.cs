using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed record CompletionGateConfiguration(int SchemaVersion, string PythonPath, string PythonSha256,
    string ScriptPath, string ScriptSha256, string ModuleSha256, string RegisterPath, string ScopeSha256,
    string ApplicationDirectory, string CandidateApplicationDirectory, string AppSha256, string InfrastructureSha256,
    string ProfileRootSha256, string AccountId, string ProjectId, string CredentialReferenceId, string CatalogEndpoint, string SynthesisEndpoint,
    string PlanManifestPath, string PlanManifestSha256, string VoiceName, string LanguageCode)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Pin { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string ConfigPath { get; init; } = "";
}
