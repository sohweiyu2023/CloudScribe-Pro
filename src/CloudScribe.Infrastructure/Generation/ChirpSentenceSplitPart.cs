using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record ChirpSentenceSplitPart(ChirpNarrationRange Range, GoogleSpeechCompilation Compilation, long MaximumMinorUnits);
