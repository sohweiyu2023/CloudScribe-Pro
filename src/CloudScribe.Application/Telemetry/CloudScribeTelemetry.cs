using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CloudScribe.Application.Telemetry;

public static class CloudScribeTelemetry
{
    public const string InstrumentationName = "CloudScribe.Pro";

    public static readonly ActivitySource ActivitySource = new(InstrumentationName);

    public static readonly Meter Meter = new(InstrumentationName);

    public static readonly Counter<long> SupportBundlePreviews =
        Meter.CreateCounter<long>("cloudscribe.support_bundle.previews");
}
