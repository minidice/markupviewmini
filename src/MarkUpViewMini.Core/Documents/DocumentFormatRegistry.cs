namespace MarkUpViewMini.Core.Documents;

public sealed class DocumentFormatRegistry
{
    private readonly IReadOnlyDictionary<string, IDocumentFormatProvider> providersByExtension;

    public DocumentFormatRegistry(IEnumerable<IDocumentFormatProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var providersByExtension = new Dictionary<string, IDocumentFormatProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            foreach (var extension in provider.Descriptor.Extensions)
            {
                if (!providersByExtension.TryAdd(extension, provider))
                {
                    throw new ArgumentException($"A provider is already registered for extension '{extension}'.", nameof(providers));
                }
            }
        }

        this.providersByExtension = providersByExtension;
    }

    public IDocumentFormatProvider Resolve(string path)
    {
        var extension = Path.GetExtension(path);
        if (providersByExtension.TryGetValue(extension, out var provider))
        {
            return provider;
        }

        throw new NotSupportedException($"No document format provider is registered for extension '{extension}'.");
    }

    public IReadOnlySet<string> GetExtensions(DocumentCapabilities requiredCapabilities) =>
        new HashSet<string>(
            providersByExtension
                .Where(pair => pair.Value.Descriptor.Capabilities.HasFlag(requiredCapabilities))
                .Select(pair => pair.Key)
                .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
                .ThenBy(extension => extension, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
}
