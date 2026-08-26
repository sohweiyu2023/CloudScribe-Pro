using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace CloudScribe.Domain.Generation;

public static class SpeechPlanSegmenter
{
    public static IReadOnlyList<SpeechPlanSegment> Segment(
        SpeechPlan plan,
        SpeechSegmentationLimits limits,
        Func<IReadOnlyList<SpeechPlanNode>, int> compiledPayloadCharacterCounter)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(compiledPayloadCharacterCounter);

        var atomicNodes = ExpandTextNodes(plan.Nodes, limits.MaximumTextElements);
        var result = new List<SpeechPlanSegment>();
        var current = new List<SpeechPlanNode>();
        var currentTextElements = 0;

        foreach (var node in atomicNodes)
        {
            var nodeElements = CountTextElements(node);
            if (nodeElements > limits.MaximumTextElements)
            {
                throw new InvalidOperationException("An indivisible speech-plan node exceeds the provider text-element limit.");
            }

            current.Add(node);
            currentTextElements += nodeElements;

            var exceedsText = currentTextElements > limits.MaximumTextElements;
            var compiledCharacters = compiledPayloadCharacterCounter(current);
            if (compiledCharacters < 0)
            {
                throw new InvalidOperationException("Compiled payload counter cannot return a negative size.");
            }

            var exceedsCompiled = compiledCharacters > limits.MaximumCompiledPayloadCharacters;
            if (!exceedsText && !exceedsCompiled)
            {
                continue;
            }

            current.RemoveAt(current.Count - 1);
            currentTextElements -= nodeElements;
            if (current.Count == 0)
            {
                throw new InvalidOperationException("An indivisible speech-plan node exceeds the compiled provider payload limit.");
            }

            AddSegment(result, current, currentTextElements);
            current = new List<SpeechPlanNode> { node };
            currentTextElements = nodeElements;

            var singleCompiledCharacters = compiledPayloadCharacterCounter(current);
            if (singleCompiledCharacters < 0 || singleCompiledCharacters > limits.MaximumCompiledPayloadCharacters)
            {
                throw new InvalidOperationException("An indivisible speech-plan node exceeds the compiled provider payload limit.");
            }
        }

        if (current.Count > 0)
        {
            AddSegment(result, current, currentTextElements);
        }

        return new ReadOnlyCollection<SpeechPlanSegment>(result);
    }

    private static List<SpeechPlanNode> ExpandTextNodes(
        IReadOnlyList<SpeechPlanNode> nodes,
        int maximumTextElements)
    {
        var expanded = new List<SpeechPlanNode>();
        foreach (var node in nodes)
        {
            if (node is not SpeechText text)
            {
                expanded.Add(node);
                continue;
            }

            foreach (var chunk in SplitText(text.Text, maximumTextElements))
            {
                expanded.Add(new SpeechText(chunk));
            }
        }

        return expanded;
    }

    private static IEnumerable<string> SplitText(string text, int maximumTextElements)
    {
        var elements = EnumerateTextElements(text);
        if (elements.Count <= maximumTextElements)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        while (start < elements.Count)
        {
            var count = Math.Min(maximumTextElements, elements.Count - start);
            var preferredCount = FindPreferredBreak(elements, start, count);
            var builder = new StringBuilder();
            for (var index = start; index < start + preferredCount; index++)
            {
                builder.Append(elements[index]);
            }

            yield return builder.ToString();
            start += preferredCount;
        }
    }

    private static int FindPreferredBreak(IReadOnlyList<string> elements, int start, int count)
    {
        if (start + count >= elements.Count)
        {
            return count;
        }

        for (var offset = count; offset > Math.Max(1, count / 2); offset--)
        {
            var element = elements[start + offset - 1];
            if (element.Length > 0 && (char.IsWhiteSpace(element[^1]) || IsSentenceTerminal(element[^1])))
            {
                return offset;
            }
        }

        return count;
    }

    private static bool IsSentenceTerminal(char value) => value is '.' or '!' or '?' or ';' or ':' or '\u3002' or '\uff01' or '\uff1f';

    private static List<string> EnumerateTextElements(string text)
    {
        var result = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            result.Add(enumerator.GetTextElement());
        }

        return result;
    }

    private static int CountTextElements(SpeechPlanNode node) => node switch
    {
        SpeechText text => new StringInfo(text.Text).LengthInTextElements,
        SpeechPronunciation pronunciation => new StringInfo(pronunciation.Text).LengthInTextElements,
        _ => 0,
    };

    private static void AddSegment(List<SpeechPlanSegment> result, List<SpeechPlanNode> nodes, int textElementCount)
    {
        result.Add(new SpeechPlanSegment(result.Count, nodes.ToArray(), textElementCount));
    }
}
