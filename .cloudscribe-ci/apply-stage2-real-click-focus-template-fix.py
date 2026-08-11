from __future__ import annotations

import argparse
import hashlib
from pathlib import Path

EXPECTED = {'src/CloudScribe.App/MainWindow.VisualCapture.cs': ('79ff7f4023b7e3138dcf0d5e1bd09914e44d4f240fd69684db7f0dd358c57bfb',
                                                     '12a64ae3d905c3bd5a57e481572f559e3b5c9312acc25af55a3a95fc2eef2870'),
 'src/CloudScribe.App/MainWindow.axaml': ('137590eee91c7593a0b0e7239c2c8089cc830264a30ca91d734ba52ad0e7742d',
                                          '0729f720bd8360d0bd98e329aa35e4c7b3e815fe086a49e0027e5ee8f5c9ba7f'),
 'tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs': ('c87b0ecd8793baa148e067de8ef99ca31ae28e641358f16d07829a023b15bc44',
                                                                'f9e63983cdab8da87f7b8c361bf6a40bd884f26772bff4e082eda87813e962b7'),
 'tools/run_python_regression_shards.py': ('c3e2e45c140c96ec0ce9634b050c6ac91f147c09dd37febb6b4e7a7662996e00',
                                           'dac5bf4325290b86eec5abf436641cf9aadfd7a0d5f623cd364e95391a100abc'),
 'tools/verify_stage2_source.py': ('862586d5730d5f0ee4a1957924e261e2a0f95598d1a697c2797a28fc804c1d45',
                                   'c11d0de72cacb350301cdcfabe9e5b6393528645a603b4738caf75ed75ab5118')}
NEW_THEME = '    <!-- Fluent TextBox recolors its private PART_BorderElement when focused.\n         The paper editor therefore owns its actual template surface instead of\n         competing with Fluent state selectors. The renamed PART_PaperSurface\n         cannot be recolored by Fluent\'s PART_BorderElement focus rule. -->\n    <ControlTheme\n        x:Key="PaperTextBoxTheme"\n        TargetType="controls:PaperTextBox"\n        BasedOn="{StaticResource {x:Type TextBox}}">\n      <Setter Property="Background" Value="Transparent" />\n      <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />\n      <Setter Property="CaretBrush" Value="{DynamicResource Brush.Ink}" />\n      <Setter Property="SelectionBrush" Value="{DynamicResource Brush.Selection}" />\n      <Setter Property="SelectionForegroundBrush" Value="{DynamicResource Brush.Ink}" />\n      <Setter Property="PlaceholderForeground" Value="{DynamicResource Brush.InkMuted}" />\n      <Setter Property="BorderBrush" Value="Transparent" />\n      <Setter Property="BorderThickness" Value="0" />\n      <Setter Property="FocusAdorner" Value="{x:Null}" />\n      <Setter Property="Template">\n        <ControlTemplate TargetType="controls:PaperTextBox">\n          <Border\n              Name="PART_PaperSurface"\n              Background="{TemplateBinding Background}"\n              BorderBrush="{TemplateBinding BorderBrush}"\n              BorderThickness="{TemplateBinding BorderThickness}"\n              CornerRadius="{TemplateBinding CornerRadius}"\n              MinWidth="{TemplateBinding MinWidth}"\n              MinHeight="{TemplateBinding MinHeight}">\n            <Grid ColumnDefinitions="Auto,*,Auto">\n              <ContentPresenter\n                  Grid.Column="0"\n                  VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}"\n                  Content="{TemplateBinding InnerLeftContent}" />\n              <DockPanel\n                  x:Name="PART_InnerDockPanel"\n                  Grid.Column="1"\n                  Margin="{TemplateBinding Padding}">\n                <TextBlock\n                    Name="PART_FloatingPlaceholder"\n                    Foreground="{TemplateBinding PlaceholderForeground}"\n                    IsVisible="False"\n                    Text="{TemplateBinding PlaceholderText}"\n                    DockPanel.Dock="Top" />\n                <ScrollViewer\n                    Name="PART_ScrollViewer"\n                    HorizontalScrollBarVisibility="{TemplateBinding (ScrollViewer.HorizontalScrollBarVisibility)}"\n                    VerticalScrollBarVisibility="{TemplateBinding (ScrollViewer.VerticalScrollBarVisibility)}"\n                    IsScrollChainingEnabled="{TemplateBinding (ScrollViewer.IsScrollChainingEnabled)}"\n                    AllowAutoHide="{TemplateBinding (ScrollViewer.AllowAutoHide)}"\n                    BringIntoViewOnFocusChange="{TemplateBinding (ScrollViewer.BringIntoViewOnFocusChange)}">\n                  <Panel>\n                    <TextBlock\n                        Name="PART_Placeholder"\n                        Foreground="{TemplateBinding PlaceholderForeground}"\n                        Text="{TemplateBinding PlaceholderText}"\n                        TextAlignment="{TemplateBinding TextAlignment}"\n                        TextWrapping="{TemplateBinding TextWrapping}"\n                        HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"\n                        VerticalAlignment="{TemplateBinding VerticalContentAlignment}">\n                      <TextBlock.IsVisible>\n                        <MultiBinding Converter="{x:Static BoolConverters.And}">\n                          <Binding ElementName="PART_TextPresenter" Path="PreeditText" Converter="{x:Static StringConverters.IsNullOrEmpty}" />\n                          <Binding RelativeSource="{RelativeSource TemplatedParent}" Path="Text" Converter="{x:Static StringConverters.IsNullOrEmpty}" />\n                        </MultiBinding>\n                      </TextBlock.IsVisible>\n                    </TextBlock>\n                    <TextPresenter\n                        Name="PART_TextPresenter"\n                        Text="{TemplateBinding Text, Mode=TwoWay}"\n                        CaretBlinkInterval="{TemplateBinding CaretBlinkInterval}"\n                        CaretIndex="{TemplateBinding CaretIndex}"\n                        SelectionStart="{TemplateBinding SelectionStart}"\n                        SelectionEnd="{TemplateBinding SelectionEnd}"\n                        TextAlignment="{TemplateBinding TextAlignment}"\n                        TextWrapping="{TemplateBinding TextWrapping}"\n                        LineHeight="{TemplateBinding LineHeight}"\n                        LetterSpacing="{TemplateBinding LetterSpacing}"\n                        PasswordChar="{TemplateBinding PasswordChar}"\n                        RevealPassword="{TemplateBinding RevealPassword}"\n                        SelectionBrush="{TemplateBinding SelectionBrush}"\n                        SelectionForegroundBrush="{TemplateBinding SelectionForegroundBrush}"\n                        CaretBrush="{TemplateBinding CaretBrush}"\n                        HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"\n                        VerticalAlignment="{TemplateBinding VerticalContentAlignment}" />\n                  </Panel>\n                  <ScrollViewer.Styles>\n                    <Style Selector="ScrollContentPresenter#PART_ContentPresenter">\n                      <Setter Property="Cursor" Value="IBeam" />\n                    </Style>\n                  </ScrollViewer.Styles>\n                </ScrollViewer>\n              </DockPanel>\n              <ContentPresenter\n                  Grid.Column="2"\n                  VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}"\n                  Content="{TemplateBinding InnerRightContent}" />\n            </Grid>\n          </Border>\n        </ControlTemplate>\n      </Setter>\n\n      <Style Selector="^ /template/ Border#PART_PaperSurface">\n        <Setter Property="Background" Value="Transparent" />\n        <Setter Property="BorderBrush" Value="Transparent" />\n        <Setter Property="BorderThickness" Value="0" />\n      </Style>\n      <Style Selector="^:pointerover">\n        <Setter Property="Background" Value="Transparent" />\n        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />\n        <Style Selector="^ /template/ Border#PART_PaperSurface">\n          <Setter Property="Background" Value="Transparent" />\n          <Setter Property="BorderBrush" Value="{DynamicResource Brush.PaperBorder}" />\n          <Setter Property="BorderThickness" Value="0,0,0,1" />\n        </Style>\n      </Style>\n      <Style Selector="^:focus">\n        <Setter Property="Background" Value="Transparent" />\n        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />\n        <Setter Property="CaretBrush" Value="{DynamicResource Brush.Ink}" />\n        <Setter Property="SelectionBrush" Value="{DynamicResource Brush.Selection}" />\n        <Setter Property="SelectionForegroundBrush" Value="{DynamicResource Brush.Ink}" />\n        <Setter Property="PlaceholderForeground" Value="{DynamicResource Brush.InkMuted}" />\n        <Style Selector="^ /template/ Border#PART_PaperSurface">\n          <Setter Property="Background" Value="Transparent" />\n          <Setter Property="BorderBrush" Value="{DynamicResource Brush.Focus}" />\n          <Setter Property="BorderThickness" Value="0,0,0,3" />\n        </Style>\n      </Style>\n      <Style Selector="^:disabled">\n        <Setter Property="Background" Value="Transparent" />\n        <Setter Property="Foreground" Value="{DynamicResource Brush.InkMuted}" />\n        <Style Selector="^ /template/ Border#PART_PaperSurface">\n          <Setter Property="Background" Value="Transparent" />\n          <Setter Property="BorderBrush" Value="Transparent" />\n          <Setter Property="BorderThickness" Value="0" />\n        </Style>\n      </Style>\n      <Style Selector="^[IsReadOnly=True]">\n        <Setter Property="Background" Value="Transparent" />\n        <Setter Property="Foreground" Value="{DynamicResource Brush.InkMuted}" />\n        <Style Selector="^ /template/ Border#PART_PaperSurface">\n          <Setter Property="Background" Value="Transparent" />\n          <Setter Property="BorderBrush" Value="Transparent" />\n          <Setter Property="BorderThickness" Value="0" />\n        </Style>\n      </Style>\n    </ControlTheme>\n'
ARCH_OLD1 = '        Assert.Contains("BasedOn=\\"{StaticResource {x:Type TextBox}}\\"", window, StringComparison.Ordinal);\n        Assert.Contains("Theme=\\"{StaticResource PaperTextBoxTheme}\\"", window, StringComparison.Ordinal);\n        Assert.Contains("^:pointerover", window, StringComparison.Ordinal);\n'
ARCH_NEW1 = '        Assert.Contains("BasedOn=\\"{StaticResource {x:Type TextBox}}\\"", window, StringComparison.Ordinal);\n        Assert.Contains("ControlTemplate TargetType=\\"controls:PaperTextBox\\"", window, StringComparison.Ordinal);\n        Assert.Contains("Name=\\"PART_PaperSurface\\"", window, StringComparison.Ordinal);\n        Assert.Contains("Theme=\\"{StaticResource PaperTextBoxTheme}\\"", window, StringComparison.Ordinal);\n        Assert.Contains("^:pointerover", window, StringComparison.Ordinal);\n'
ARCH_OLD2 = '        Assert.Contains("PlaceholderForeground", capture, StringComparison.Ordinal);\n        Assert.Contains("01-full-follow-system-dark-pointer-focus", capture, StringComparison.Ordinal);\n'
ARCH_NEW2 = '        Assert.Contains("PlaceholderForeground", capture, StringComparison.Ordinal);\n        Assert.Contains("PART_PaperSurface", capture, StringComparison.Ordinal);\n        Assert.DoesNotContain("PART_BorderElement is unavailable during visual capture", capture, StringComparison.Ordinal);\n        Assert.Contains("01-full-follow-system-dark-pointer-focus", capture, StringComparison.Ordinal);\n'
VER_OLD = '    if "PaperTextBoxTheme" not in window or \'TargetType="controls:PaperTextBox"\' not in window or \'BasedOn="{StaticResource {x:Type TextBox}}"\' not in window:\n        return fail("paper editor does not own a derived PaperTextBox control theme")\n'
VER_NEW = '    if "PaperTextBoxTheme" not in window or \'TargetType="controls:PaperTextBox"\' not in window or \'BasedOn="{StaticResource {x:Type TextBox}}"\' not in window:\n        return fail("paper editor does not own a derived PaperTextBox control theme")\n    if \'ControlTemplate TargetType="controls:PaperTextBox"\' not in window or \'Name="PART_PaperSurface"\' not in window:\n        return fail("paper editor must own a dedicated template surface rather than Fluent PART_BorderElement")\n    if \'string.Equals(border.Name, "PART_PaperSurface", StringComparison.Ordinal)\' not in capture:\n        return fail("visual capture does not audit the dedicated paper template surface")\n'

def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()

def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one anchor, found {count}")
    return text.replace(old, new, 1)

def save(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8", newline="")

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", required=True)
    root = Path(parser.parse_args().source_root).resolve()

    for relative, (pre_hash, post_hash) in EXPECTED.items():
        actual = sha256(root / relative)
        if actual not in (pre_hash, post_hash):
            raise RuntimeError(f"unexpected preimage for {relative}: expected={pre_hash} actual={actual}")

    path = root / "src/CloudScribe.App/MainWindow.axaml"
    if sha256(path) == EXPECTED["src/CloudScribe.App/MainWindow.axaml"][0]:
        text = path.read_text(encoding="utf-8")
        marker = "    <!-- The Fluent TextBox control theme owns nested :pointerover/:focus states that"
        start = text.index(marker)
        end = text.index("  </Window.Resources>", start)
        save(path, text[:start] + NEW_THEME + text[end:])

    path = root / "src/CloudScribe.App/MainWindow.VisualCapture.cs"
    if sha256(path) == EXPECTED["src/CloudScribe.App/MainWindow.VisualCapture.cs"][0]:
        text = path.read_text(encoding="utf-8")
        text = replace_once(text, 'string.Equals(border.Name, "PART_BorderElement", StringComparison.Ordinal)', 'string.Equals(border.Name, "PART_PaperSurface", StringComparison.Ordinal)', "capture surface lookup")
        text = replace_once(text, 'The document editor PART_BorderElement is unavailable during visual capture.', 'The document editor PART_PaperSurface is unavailable during visual capture.', "capture surface error")
        save(path, text)

    path = root / "tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs"
    if sha256(path) == EXPECTED["tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs"][0]:
        text = path.read_text(encoding="utf-8")
        text = replace_once(text, ARCH_OLD1, ARCH_NEW1, "architecture template contract")
        text = replace_once(text, ARCH_OLD2, ARCH_NEW2, "architecture capture contract")
        save(path, text)

    path = root / "tools/verify_stage2_source.py"
    if sha256(path) == EXPECTED["tools/verify_stage2_source.py"][0]:
        text = path.read_text(encoding="utf-8")
        save(path, replace_once(text, VER_OLD, VER_NEW, "Stage 2 source verifier"))

    path = root / "tools/run_python_regression_shards.py"
    if sha256(path) == EXPECTED["tools/run_python_regression_shards.py"][0]:
        text = path.read_text(encoding="utf-8")
        old = '(main_window_xaml, "PaperTextBoxTheme", "paper editor derived control-theme policy"),'
        new = '(main_window_xaml, "PART_PaperSurface", "paper editor dedicated template-surface policy"),'
        save(path, replace_once(text, old, new, "material marker"))

    for relative, (_, post_hash) in EXPECTED.items():
        actual = sha256(root / relative)
        if actual != post_hash:
            raise RuntimeError(f"postimage mismatch for {relative}: expected={post_hash} actual={actual}")

    print("CLOUDSCRIBE_STAGE2_REAL_CLICK_FOCUS_TEMPLATE_FIX=PASS")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
