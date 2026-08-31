using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record PlannedSpeakerTurn(
    int TurnIndex,
    string SpeakerId,
    string Text,
    SpeakerRoute Route);
