using System;

namespace WebUIToolkit.DependencyNotices.Tool;

public static class OutputSanitizer
{
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string result = value.Replace('\0', '\uFFFD').Replace('\r', ' ').Replace('\n', ' ');
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            result = result.Replace(home, "<user-home>", StringComparison.OrdinalIgnoreCase);
        }

        result = SanitizeUris(result, "https://");
        result = SanitizeUris(result, "http://");
        return result;
    }

    private static string SanitizeUris(string value, string prefix)
    {
        int searchFrom = 0;
        while (value.IndexOf(prefix, searchFrom, StringComparison.OrdinalIgnoreCase) is int start && start >= 0)
        {
            int end = start;
            while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] is not '\'' and not '"' and not '<' and not '>') end++;
            string candidate = value[start..end];
            if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
            {
                UriBuilder safe = new(uri)
                {
                    UserName = string.Empty,
                    Password = string.Empty,
                    Query = string.Empty,
                    Fragment = string.Empty,
                };
                string replacement = safe.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
                value = string.Concat(value.AsSpan(0, start), replacement, value.AsSpan(end));
                searchFrom = start + replacement.Length;
            }
            else
            {
                searchFrom = end;
            }
        }

        return value;
    }
}
