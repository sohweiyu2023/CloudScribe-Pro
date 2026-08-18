using CloudScribe.Domain.Pricing;

namespace CloudScribe.Domain.Tests;

public sealed class PricingPlanDefinitionTests
{
    [Fact]
    public void PlanPreservesExplicitMeterReferencesAndProvenance()
    {
        PricingPlanDefinition plan = new(
            "standard-plan",
            ["text-input", "audio-output"],
            "catalog:fixture-plan");

        Assert.Equal("standard-plan", plan.StableId);
        Assert.Equal(["text-input", "audio-output"], plan.MeterStableIds);
        Assert.Equal("catalog:fixture-plan", plan.ProvenanceId);
    }

    [Fact]
    public void PlanRejectsMissingOrDuplicateMeterReferences()
    {
        Assert.Throws<ArgumentException>(() => new PricingPlanDefinition(
            "standard-plan",
            [],
            "catalog:fixture-plan"));
        Assert.Throws<ArgumentException>(() => new PricingPlanDefinition(
            "standard-plan",
            ["text-input", "text-input"],
            "catalog:fixture-plan"));
    }

    [Fact]
    public void PlanRejectsAmbiguousIdentifiersAndInvisibleProvenance()
    {
        Assert.Throws<ArgumentException>(() => new PricingPlanDefinition(
            "Standard Plan",
            ["text-input"],
            "catalog:fixture-plan"));
        Assert.Throws<ArgumentException>(() => new PricingPlanDefinition(
            "standard-plan",
            ["Text Input"],
            "catalog:fixture-plan"));
        Assert.Throws<ArgumentException>(() => new PricingPlanDefinition(
            "standard-plan",
            ["text-input"],
            "catalog:\u200bfixture-plan"));
    }
}
