using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace WebUIToolkit.CommandLine;

internal static class CommandFaultSanitizer
{
    private const int MaximumCodeLength = 64;
    private const int MaximumMessageLength = 4_096;
    private const int MaximumDetailCount = 32;
    private const int MaximumDetailKeyLength = 64;
    private const int MaximumDetailValueLength = 1_024;

    internal static CommandFault Sanitize(CommandFault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);

        string code = SanitizeScalar(fault.Code, MaximumCodeLength);
        string message = SanitizeScalar(fault.Message, MaximumMessageLength);
        if (!IsSafeCode(code) || string.IsNullOrWhiteSpace(message) ||
            ContainsTechnicalContent(fault.Message))
        {
            return SoftwareFailure();
        }

        var details = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> detail in fault.Details)
        {
            if (details.Count == MaximumDetailCount)
            {
                break;
            }

            string key = SanitizeScalar(detail.Key, MaximumDetailKeyLength);
            if (string.IsNullOrWhiteSpace(key) || details.ContainsKey(key))
            {
                continue;
            }

            string value = ContainsTechnicalContent(detail.Value)
                ? "[redacted]"
                : SanitizeScalar(detail.Value, MaximumDetailValueLength);
            details.Add(key, value);
        }

        return new CommandFault(code, message, details, fault.Retryable);
    }

    internal static string SanitizeText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SanitizeScalar(value, MaximumMessageLength);
    }

    internal static string SanitizeRequiredText(string value)
    {
        string sanitized = SanitizeText(value);
        return string.IsNullOrWhiteSpace(sanitized)
            ? "The diagnostic message was redacted."
            : sanitized;
    }

    internal static string SanitizeArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SanitizeScalar(value, MaximumDetailValueLength);
    }

    internal static bool IsSafeText(string value, int maximumUtf8Bytes, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value);
        return (allowEmpty || value.Length != 0) &&
            string.Equals(value, SanitizeScalar(value, maximumUtf8Bytes), StringComparison.Ordinal) &&
            !ContainsTechnicalContent(value);
    }

    internal static bool IsSafeCode(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumCodeLength ||
            value[0] is < 'A' or > 'Z')
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            if (character is not ('.' or '_' or '-' or >= 'A' and <= 'Z' or >= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    private static string SanitizeScalar(string value, int maximumLength)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        bool previousWasSpace = false;
        int utf8Bytes = 0;
        int index = 0;
        while (index < value.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf16(
                value.AsSpan(index),
                out Rune rune,
                out int consumed);
            if (status != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                consumed = 1;
            }

            bool replaceWithSpace = Rune.IsControl(rune) ||
                rune.Value is 0x1B or 0x2028 or 0x2029;
            if (replaceWithSpace)
            {
                if (!previousWasSpace)
                {
                    if (utf8Bytes == maximumLength)
                    {
                        break;
                    }

                    builder.Append(' ');
                    previousWasSpace = true;
                    utf8Bytes++;
                }

                index += consumed;
                continue;
            }

            int runeBytes = rune.Utf8SequenceLength;
            if (utf8Bytes + runeBytes > maximumLength)
            {
                break;
            }

            builder.Append(rune.ToString());
            previousWasSpace = rune.Value == ' ';
            utf8Bytes += runeBytes;
            index += consumed;
        }

        return builder.ToString();
    }

    private static CommandFault SoftwareFailure() =>
        new("WUTCLI5000", "The command failed unexpectedly.");

    internal static bool ContainsTechnicalContent(string value)
    {
        if (value.Contains("Exception", StringComparison.Ordinal) ||
            value.Contains("\\\\", StringComparison.Ordinal) ||
            value.Contains("/home/", StringComparison.Ordinal) ||
            value.Contains("/Users/", StringComparison.Ordinal) ||
            value.Contains("/root/", StringComparison.Ordinal) ||
            value.Contains("/tmp/", StringComparison.Ordinal))
        {
            return true;
        }

        for (int index = 0; index + 2 < value.Length; index++)
        {
            if (((value[index] is >= 'A' and <= 'Z') || (value[index] is >= 'a' and <= 'z')) &&
                value[index + 1] == ':' && value[index + 2] is '\\' or '/')
            {
                return true;
            }
        }

        return false;
    }

}
