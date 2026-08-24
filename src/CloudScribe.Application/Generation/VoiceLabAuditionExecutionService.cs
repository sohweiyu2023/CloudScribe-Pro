namespace CloudScribe.Application.Generation;

public sealed class VoiceLabAuditionExecutionService
{
    private readonly VoiceLabAuditionCoordinator _coordinator;
    private readonly string _providerStableId;
    private readonly string _accountStableId;
    private readonly string _projectStableId;
    private readonly string _voiceStableId;
    private readonly string _voiceFingerprint;

    public VoiceLabAuditionExecutionService(
        VoiceLabAuditionCoordinator coordinator,
        string providerStableId,
        string accountStableId,
        string projectStableId,
        string voiceStableId,
        string voiceFingerprint)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _providerStableId = Require(providerStableId, nameof(providerStableId));
        _accountStableId = Require(accountStableId, nameof(accountStableId));
        _projectStableId = Require(projectStableId, nameof(projectStableId));
        _voiceStableId = Require(voiceStableId, nameof(voiceStableId));
        _voiceFingerprint = Require(voiceFingerprint, nameof(voiceFingerprint));
    }

    public Task<VoiceLabAuditionOutcome> ExecuteAsync(
        VoiceLabAuditionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _coordinator.ExecuteBoundAsync(
            request,
            _providerStableId,
            _accountStableId,
            _projectStableId,
            _voiceStableId,
            _voiceFingerprint,
            cancellationToken);
    }

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}
