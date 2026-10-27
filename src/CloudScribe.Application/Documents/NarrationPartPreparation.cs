namespace CloudScribe.Application.Documents;

public sealed record NarrationPartPreparation(Guid GroupId, bool AlreadyComplete, int ExpectedPartCount);
