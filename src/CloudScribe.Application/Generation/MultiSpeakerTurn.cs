namespace CloudScribe.Application.Generation;

public sealed record MultiSpeakerTurn(
    int TurnIndex,
    string SpeakerId,
    string Text);
