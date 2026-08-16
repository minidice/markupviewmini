using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.Infrastructure.Recovery;

public sealed record RecoveryRecord(
    int SchemaVersion,
    Guid TabId,
    string Path,
    DiskFileVersion BaselineVersion,
    EncodingDescriptor Encoding,
    NewLineKind NewLine,
    string PreferredNewLine,
    long Revision,
    DateTime SavedAtUtc,
    string BodyUtf16Base64)
{
    public const int CurrentSchemaVersion = 2;

    public static string EncodeBody(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var bytes = new byte[checked(body.Length * 2)];
        for (var index = 0; index < body.Length; index++)
        {
            var codeUnit = body[index];
            bytes[index * 2] = (byte)codeUnit;
            bytes[(index * 2) + 1] = (byte)(codeUnit >> 8);
        }

        return Convert.ToBase64String(bytes);
    }

    public string DecodeBody()
    {
        var bytes = Convert.FromBase64String(BodyUtf16Base64);
        if (bytes.Length % 2 != 0 ||
            !string.Equals(Convert.ToBase64String(bytes), BodyUtf16Base64, StringComparison.Ordinal))
        {
            throw new FormatException("The recovery body has an invalid UTF-16 code-unit length.");
        }

        return string.Create(
            bytes.Length / 2,
            bytes,
            static (characters, source) =>
            {
                for (var index = 0; index < characters.Length; index++)
                {
                    characters[index] = (char)(source[index * 2] | (source[(index * 2) + 1] << 8));
                }
            });
    }
}
