using System.Text.Json;
using System.Text.Json.Nodes;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class InstalledRecoveryAdmissionStoreTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("scope-hash")]
    [InlineData("grant-digest")]
    [InlineData("expiry")]
    [InlineData("source")]
    [InlineData("count")]
    [InlineData("cap")]
    [InlineData("audit-pins")]
    [InlineData("unknown-link")]
    [InlineData("unknown-transition")]
    [InlineData("numeric-source")]
    [InlineData("schema2")]
    [InlineData("schema2-pins")]
    [InlineData("missing-nullable")]
    [InlineData("currency")]
    [InlineData("scale")]
    public async Task Schema3RequiresCanonicalBoundedSuccessorAndStrictNestedAuthority(string scenario)
    {
        using var f = new Fixture();
        var id = Guid.NewGuid();
        var transition = new RecoveryAllocationTransition(new string('d', 64), 80, 83, 70, new string('c', 64));
        var link = new RecoverySuccessorGrant(id, GoogleExplicitRecoveryService.KeyPrefix + id.ToString("N"),
            new string('d', 64), new string('b', 64), new string('e', 64), new string('c', 64), transition);
        var prior = f.Policy.Predecessors.Single() with { Successor = link };
        prior = prior with { AdmissionSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(prior with { AdmissionSha256 = "" })) };
        var policy = f.Policy with
        {
            SchemaVersion = 3,
            HistoricalAudio = [],
            Predecessors = [prior],
            Budget = f.Policy.Budget with { AllocationCommittedMinorUnits = 13, AllocationCeilingMinorUnits = 83, TransitionRows = [] }
        };
        var node = JsonNode.Parse(JsonSerializer.Serialize(policy))!;
        MutateSuccessorPolicy(node, scenario);
        f.AdmitBytes(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()));
        var admitted = await f.Store.LoadVerifiedPolicyAsync(TestContext.Current.CancellationToken);
        if (scenario is "valid") Assert.NotNull(admitted); else Assert.Null(admitted);
    }
    private static void MutateSuccessorPolicy(JsonNode node, string scenario)
    {
        var p = node["Predecessors"]![0]!;
        switch (scenario)
        {
            case "scope-hash": p["Successor"]!["AllocationTransition"]!["PriorScopeSha256"] = "bad"; break;
            case "grant-digest": p["AdmissionSha256"] = new string('f', 64); break;
            case "expiry": p["ExpiresAtUtc"] = DateTimeOffset.UtcNow.AddHours(2); break;
            case "source": p["Successor"]!["SourceApprovalSha256"] = new string('f', 64); break;
            case "count": p["MaximumNewAttempts"] = 2; break;
            case "cap": p["MaximumAdditionalReservationMinorUnits"] = 4; break;
            case "audit-pins": node["Budget"]!.AsObject().Remove("TransitionRows"); break;
            case "unknown-link": p["Successor"]!["Unknown"] = true; break;
            case "unknown-transition": p["Successor"]!["AllocationTransition"]!["Unknown"] = true; break;
            case "numeric-source": p["Successor"]!["SourceApprovalSha256"] = 17; break;
            case "schema2-pins": node["SchemaVersion"] = 2; p.AsObject().Remove("Successor"); break;
            case "missing-nullable": p["Successor"]!.AsObject().Remove("AllocationTransition"); break;
            case "currency": node["Currency"] = "EUR"; break;
            case "scale": node["Scale"] = 3; break;
            case "schema2": node["SchemaVersion"] = 2; break;
        }
    }

}
