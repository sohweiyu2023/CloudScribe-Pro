using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed class GenerationOutputReservationService
{
    public IReadOnlyList<OutputReservation> ReservePlanOutputs(AudioAssemblyPlan plan, bool allowExplicitReplacement = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var reservations = new List<OutputReservation>(plan.OutputPaths.Count);
        var canonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var outputPath in plan.OutputPaths)
        {
            var fullPath = Path.GetFullPath(outputPath);
            if (!canonical.Add(fullPath))
            {
                throw new InvalidOperationException("Duplicate canonical output path detected.");
            }

            var exists = File.Exists(fullPath) || Directory.Exists(fullPath);
            if (exists && !allowExplicitReplacement)
            {
                throw new IOException($"Output collision detected for '{fullPath}'. Explicit replacement authorization is required.");
            }

            reservations.Add(new OutputReservation(fullPath, exists));
        }

        return reservations;
    }
}
