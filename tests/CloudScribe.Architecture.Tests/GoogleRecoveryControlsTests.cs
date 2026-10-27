using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleRecoveryControlsTests
{
    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(2, -1)]
    public void SingleAdmittedChoiceIsVisibleButAmbiguousChoicesAndSpendRemainExplicit(int count, int selected)
    {
        MethodInfo factory = typeof(GoogleTtsUsabilityHotfixBinder).GetMethod("CreateRecoveryControls", BindingFlags.NonPublic | BindingFlags.Static)!;
        object controls = factory.Invoke(null, [Enumerable.Range(0, count).Select(index => $"synthetic-{index}").ToArray()])!;
        T Read<T>(string name) => (T)controls.GetType().GetProperty(name)!.GetValue(controls)!;
        var choice = Read<ComboBox>("Choice");
        Assert.Equal(selected, choice.SelectedIndex);
        Assert.Equal(count > 0, choice.IsEnabled);
        Assert.Equal("Independently admitted historical narration attempt", AutomationProperties.GetName(choice));
        Assert.Equal("Additional reservation maximum in smallest currency units", AutomationProperties.GetName(Read<NumericUpDown>("Maximum")));
        Assert.Equal(0m, Read<NumericUpDown>("Maximum").Value);
        Assert.False(Read<CheckBox>("Acknowledge").IsEnabled);
        Assert.NotEqual(true, Read<CheckBox>("Acknowledge").IsChecked);
    }
}
