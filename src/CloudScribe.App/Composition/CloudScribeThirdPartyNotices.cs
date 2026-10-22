using System.Collections.ObjectModel;

namespace CloudScribe.App.Composition;

public static class CloudScribeThirdPartyNotices
{
    public const string IndexName = "THIRD-PARTY-NOTICES.md";

    public static IReadOnlyDictionary<string, string> ReadDocuments()
    {
        const string prefix = "CloudScribe.Notices/";
        System.Reflection.Assembly assembly = typeof(CloudScribeThirdPartyNotices).Assembly;
        SortedDictionary<string, string> documents = new(StringComparer.Ordinal);
        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("A bundled notice is unavailable.");
            using StreamReader reader = new(stream);
            documents.Add(resourceName[prefix.Length..].Replace('\\', '/'), reader.ReadToEnd());
        }

        if (!documents.ContainsKey(IndexName)) throw new InvalidOperationException("The bundled notices index is unavailable.");
        return new ReadOnlyDictionary<string, string>(documents);
    }
}
