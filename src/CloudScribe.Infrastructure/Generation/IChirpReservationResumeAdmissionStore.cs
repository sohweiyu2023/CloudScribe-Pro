namespace CloudScribe.Infrastructure.Generation;

public interface IChirpReservationResumeAdmissionStore
{
    Task<ChirpReservationResumePolicy?> LoadVerifiedAsync(CancellationToken cancellationToken = default);
}
