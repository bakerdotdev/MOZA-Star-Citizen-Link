using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MozaStarCitizen.App.Telemetry.DBoxSdkLog;

public sealed class DBoxSdkXmlRecordFramer
{
    private const int MaximumBufferedCharacters = 1024 * 1024;
    private const string StartToken = "<Log";
    private const string EndToken = "</Log>";
    private readonly StringBuilder _buffer = new();

    public long DiscardedCharacters { get; private set; }

    public long DiscardedNonWhitespaceCharacters { get; private set; }

    public int BufferedCharacterCount => _buffer.Length;

    public bool HasNonWhitespaceBufferedContent =>
        _buffer.ToString().Any(character => !char.IsWhiteSpace(character));

    public IReadOnlyList<string> Append(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _buffer.Append(text);
        }

        var records = new List<string>();
        while (_buffer.Length > 0)
        {
            var snapshot = _buffer.ToString();
            var start = snapshot.IndexOf(StartToken, StringComparison.Ordinal);
            if (start < 0)
            {
                TrimJunkWithoutStartToken();
                break;
            }

            if (start > 0)
            {
                DiscardPrefix(start);
                snapshot = _buffer.ToString();
            }

            var end = snapshot.IndexOf(EndToken, StartToken.Length, StringComparison.Ordinal);
            var nextStart = snapshot.IndexOf(StartToken, StartToken.Length, StringComparison.Ordinal);
            if (nextStart >= 0 && (end < 0 || nextStart < end))
            {
                DiscardPrefix(nextStart);
                continue;
            }

            if (end < 0)
            {
                if (_buffer.Length > MaximumBufferedCharacters)
                {
                    DiscardPrefix(_buffer.Length);
                }

                break;
            }

            var recordLength = end + EndToken.Length;
            records.Add(snapshot[..recordLength]);
            _buffer.Remove(0, recordLength);
        }

        return records;
    }

    public void Reset()
    {
        DiscardPrefix(_buffer.Length);
    }

    private void TrimJunkWithoutStartToken()
    {
        var keep = Math.Min(_buffer.Length, StartToken.Length - 1);
        var discard = _buffer.Length - keep;
        if (discard <= 0)
        {
            return;
        }

        DiscardPrefix(discard);
    }

    private void DiscardPrefix(int count)
    {
        if (count <= 0)
        {
            return;
        }

        for (var index = 0; index < count; index++)
        {
            if (!char.IsWhiteSpace(_buffer[index]))
            {
                DiscardedNonWhitespaceCharacters++;
            }
        }

        _buffer.Remove(0, count);
        DiscardedCharacters += count;
    }
}

public static class DBoxSdkXmlLogParser
{
    private const long MaximumRecordCharacters = 1024 * 1024;

    public static bool TryParse(
        string xml,
        out DBoxSdkLogRecord? record,
        out string? error)
    {
        record = null;
        error = null;

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumRecordCharacters,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };

            using var stringReader = new StringReader(xml);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            var root = XElement.Load(xmlReader, LoadOptions.None);
            if (root.Name.NamespaceName.Length != 0 ||
                !string.Equals(root.Name.LocalName, "Log", StringComparison.Ordinal))
            {
                error = "The record root must be an unqualified Log element.";
                return false;
            }

            foreach (var element in root.DescendantsAndSelf())
            {
                var attributes = element.Attributes().ToArray();
                if (element.Name.NamespaceName.Length != 0 ||
                    attributes.Any(attribute =>
                        attribute.IsNamespaceDeclaration ||
                        attribute.Name.NamespaceName.Length != 0))
                {
                    error = "Namespaces and namespace declarations are not accepted.";
                    return false;
                }

                if (attributes
                    .GroupBy(attribute => attribute.Name.LocalName, StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() != 1))
                {
                    error = "Case-insensitive duplicate attributes are not accepted.";
                    return false;
                }

                if (element.Nodes()
                    .OfType<XText>()
                    .Any(text => text.Value.Any(character => !char.IsWhiteSpace(character))))
                {
                    error = "Non-whitespace element text is not accepted.";
                    return false;
                }
            }

            var methods = root.Elements().ToArray();
            if (methods.Length != 1 || methods[0].Name.NamespaceName.Length != 0)
            {
                error = "The Log record must contain exactly one unqualified method element.";
                return false;
            }

            var method = methods[0];
            var methodName = method.Name.LocalName;
            var children = method.Elements().ToArray();
            if (methodName == "RegisterEvent" &&
                children.Any(element =>
                    !string.Equals(element.Name.LocalName, "Field", StringComparison.Ordinal)))
            {
                error = "RegisterEvent may contain only Field elements.";
                return false;
            }

            if (methodName is not ("RegisterEvent" or "PostEvent") && children.Length != 0)
            {
                error = $"{methodName} may not contain child elements.";
                return false;
            }

            if (children.Any(element => element.Elements().Any()))
            {
                error = "Nested field/value elements are not accepted.";
                return false;
            }

            var fields = methodName == "RegisterEvent"
                ? children.Select(ParseField).ToArray()
                : [];
            var values = methodName == "PostEvent"
                ? children.Select(ParsePostedValue).ToArray()
                : [];

            record = new DBoxSdkLogRecord
            {
                ElapsedMilliseconds = ReadRequiredDouble(root, "TimeStamp"),
                Method = methodName,
                MethodId = ReadNullableInt32(method, "MethodId"),
                AppKey = ReadString(method, "AppKey"),
                AppBuild = ReadNullableInt32(method, "AppBuild"),
                ApiKey = ReadString(method, "ApiKey"),
                EventKey = ReadNullableUInt32(method, "Key"),
                EventMeaningId = ReadNullableInt32(method, "Meaning"),
                EventMeaningName = ReadString(method, "MeaningName"),
                DataSize = ReadNullableInt32(method, "DataSize"),
                DeclaredFieldCount = ReadNullableInt32(method, "FieldCount"),
                Fields = fields,
                Values = values
            };
            return true;
        }
        catch (Exception ex) when (ex is XmlException or FormatException or InvalidDataException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static DBoxSdkFieldDefinition ParseField(XElement element) =>
        new(
            ReadRequiredInt32(element, "Type"),
            ReadRequiredInt32(element, "Flags"),
            ReadRequiredInt32(element, "Meaning"),
            ReadRequiredInt32(element, "Offset"),
            ReadString(element, "TypeName"),
            ReadString(element, "MeaningName"));

    private static DBoxSdkPostedValue ParsePostedValue(XElement element)
    {
        var attributes = element.Attributes()
            .Where(attribute => !string.Equals(attribute.Name.LocalName, "Type", StringComparison.Ordinal))
            .ToDictionary(
                attribute => attribute.Name.LocalName,
                attribute => attribute.Value,
                StringComparer.OrdinalIgnoreCase);

        return new DBoxSdkPostedValue(
            ReadNullableInt32(element, "Type"),
            element.Name.LocalName,
            attributes);
    }

    private static double ReadRequiredDouble(XElement element, string name)
    {
        var raw = ReadRequiredString(element, name);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) ||
            value < 0)
        {
            throw new FormatException($"{name} is not a finite, non-negative number.");
        }

        return value;
    }

    private static int ReadRequiredInt32(XElement element, string name)
    {
        var raw = ReadRequiredString(element, name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"{name} is not a valid Int32.");
    }

    private static int? ReadNullableInt32(XElement element, string name)
    {
        var raw = ReadString(element, name);
        if (raw is null)
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"{name} is not a valid Int32.");
    }

    private static uint? ReadNullableUInt32(XElement element, string name)
    {
        var raw = ReadString(element, name);
        if (raw is null)
        {
            return null;
        }

        return uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"{name} is not a valid UInt32.");
    }

    private static string ReadRequiredString(XElement element, string name) =>
        ReadString(element, name) ??
        throw new InvalidDataException($"Required attribute {name} is missing.");

    private static string? ReadString(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
            ?.Value;
}
