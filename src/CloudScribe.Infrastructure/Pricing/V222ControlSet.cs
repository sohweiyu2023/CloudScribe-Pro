using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudScribe.Infrastructure.Pricing;

public sealed class V222ControlSet
{
    public const string CarrierSha256 = "62a2b2bf1da323b87430264568340a41cce71d65d975328cfc6f9a0b2b3cc986";
    public const string PricingSchemaSha256 = "1dc77a16130efa0fa2428e954bbfc5c7d30088283bbaf5b3dddff5694e01972b";
    public const string PricingSeedSha256 = "3e647812dcae11face91b66c3df642f19134de34b8d706e2c2183c87266e8b61";
    public const string RuntimePolicySchemaSha256 = "bdcc03005a48d9d8bdcb139d468a9c3f277526aa4d9dbe19c2c6309b5bff390c";
    public const string RuntimePolicySeedSha256 = "9561a4f5c1d58dd471424566b05f7325a52ed06a4c57ec53b17f5395ae621525";
    public const string LimitsContractSha256 = "5d3e17debc58e0775bf472f7eebd79db32447de457fcec20d924a860dcfcb6d7";
    public const string ValidationReportSha256 = "0410bd29a1d3018efb606efd79747c4ede921d43b39e0693a4963d67ad41bde6";
    public const string CatalogVersion = "2026.07.20.2";

    private const string PricingSchemaEntry = "02_Pricing/cloudscribe-pricing.schema-1.1.5.json";
    private const string PricingSeedEntry = "02_Pricing/cloudscribe-pricing.seed-2026-07-20.schema-1.1.5.json";
    private const string RuntimeSchemaEntry = "03_Implementation/cloudscribe-runtime-policy.schema-1.3.json";
    private const string RuntimeSeedEntry = "03_Implementation/cloudscribe-runtime-policy.seed-2026-07-20.schema-1.3.json";
    private const string LimitsEntry = "06_Product_and_Distribution/CloudScribe_Pro_Batch_Limits_Autosave_Settings_Contract_v2.22.md";
    private const string ReportEntry = "02_Pricing/CloudScribe_Pricing_Catalog_Validation_v1.1.5_2026-07-20.json";

    private readonly byte[] _pricingSchema;
    private readonly byte[] _pricingSeed;
    private readonly byte[] _runtimePolicySchema;
    private readonly byte[] _runtimePolicySeed;
    private readonly byte[] _limitsContract;
    private readonly byte[] _validationReport;

    public V222ControlSet(StrictJsonObjectReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        byte[] archiveBytes = RehydrateAuthenticatedCarrier(typeof(V222ControlSet).Assembly);
        using MemoryStream archiveStream = new(archiveBytes, writable: false);
        using ZipArchive archive = new(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

        _pricingSchema = ReadAuthenticatedEntry(archive, PricingSchemaEntry, PricingSchemaSha256);
        _pricingSeed = ReadAuthenticatedEntry(archive, PricingSeedEntry, PricingSeedSha256);
        _runtimePolicySchema = ReadAuthenticatedEntry(archive, RuntimeSchemaEntry, RuntimePolicySchemaSha256);
        _runtimePolicySeed = ReadAuthenticatedEntry(archive, RuntimeSeedEntry, RuntimePolicySeedSha256);
        _limitsContract = ReadAuthenticatedEntry(archive, LimitsEntry, LimitsContractSha256);
        _validationReport = ReadAuthenticatedEntry(archive, ReportEntry, ValidationReportSha256);

        using JsonDocument pricingSchemaDocument = reader.Parse(_pricingSchema);
        using JsonDocument pricingSeedDocument = reader.Parse(_pricingSeed);
        using JsonDocument runtimeSchemaDocument = reader.Parse(_runtimePolicySchema);
        using JsonDocument runtimeSeedDocument = reader.Parse(_runtimePolicySeed);
        using JsonDocument reportDocument = reader.Parse(_validationReport);

        RequireString(pricingSeedDocument.RootElement, "schema_version", "1.1.5");
        JsonElement catalog = RequireObject(pricingSeedDocument.RootElement, "catalog");
        RequireString(catalog, "catalog_version", CatalogVersion);
        RequireString(runtimeSeedDocument.RootElement, "schema_version", "1.3");
        RequireBoolean(reportDocument.RootElement, "passed", true);
        RequireString(reportDocument.RootElement, "catalog_version", CatalogVersion);
    }

    public ReadOnlyMemory<byte> PricingSchemaUtf8 => _pricingSchema;
    public ReadOnlyMemory<byte> PricingSeedUtf8 => _pricingSeed;
    public ReadOnlyMemory<byte> RuntimePolicySchemaUtf8 => _runtimePolicySchema;
    public ReadOnlyMemory<byte> RuntimePolicySeedUtf8 => _runtimePolicySeed;
    public ReadOnlyMemory<byte> LimitsContractUtf8 => _limitsContract;
    public ReadOnlyMemory<byte> ValidationReportUtf8 => _validationReport;

    private static byte[] RehydrateAuthenticatedCarrier(Assembly assembly)
    {
        string[] names = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("CloudScribe.V222Carrier.part", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0)
        {
            throw new InvalidOperationException("Authenticated v2.22 clean carrier resources are missing.");
        }

        StringBuilder base64 = new();
        foreach (string name in names)
        {
            using Stream stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Required authenticated v2.22 carrier resource is missing: {name}");
            using StreamReader reader = new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
            foreach (char character in reader.ReadToEnd())
            {
                if (!char.IsWhiteSpace(character))
                {
                    base64.Append(character);
                }
            }
        }

        byte[] archiveBytes;
        try
        {
            archiveBytes = Convert.FromBase64String(base64.ToString());
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Authenticated v2.22 clean carrier is not strict Base64.", exception);
        }

        Authenticate(archiveBytes, CarrierSha256, "v2.22 clean carrier archive");
        return archiveBytes;
    }

    private static byte[] ReadAuthenticatedEntry(ZipArchive archive, string name, string expectedSha256)
    {
        ZipArchiveEntry entry = archive.GetEntry(name)
            ?? throw new InvalidOperationException($"Authenticated v2.22 control archive is missing: {name}");
        using Stream stream = entry.Open();
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        Authenticate(bytes, expectedSha256, name);
        return bytes;
    }

    private static void Authenticate(ReadOnlySpan<byte> bytes, string expectedSha256, string name)
    {
        string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Authenticated v2.22 identity mismatch for {name}: {actual}");
        }
    }

    private static JsonElement RequireObject(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Authenticated v2.22 control is missing required object property '{propertyName}'.");
        }
        return value;
    }

    private static void RequireString(JsonElement parent, string propertyName, string expected)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || !string.Equals(value.GetString(), expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Authenticated v2.22 control has unexpected '{propertyName}'.");
        }
    }

    private static void RequireBoolean(JsonElement parent, string propertyName, bool expected)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value)
            || (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
            || value.GetBoolean() != expected)
        {
            throw new InvalidOperationException($"Authenticated v2.22 validation report has unexpected '{propertyName}'.");
        }
    }
}
