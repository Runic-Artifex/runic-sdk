using System.Text;

namespace Runic.Desktop.Internal;

internal static class WebUiCommandLine
{
    internal static IReadOnlyList<string> Split(string commandLine)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        var tokenStarted = false;
        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];

            if (character == '\\')
            {
                if (index + 1 < commandLine.Length && commandLine[index + 1] is '\\' or '\'' or '"')
                {
                    current.Append(commandLine[++index]);
                }
                else
                {
                    current.Append(character);
                }
                tokenStarted = true;
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }
                tokenStarted = true;
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                tokenStarted = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                AddCurrent(arguments, current, ref tokenStarted);
            }
            else
            {
                current.Append(character);
                tokenStarted = true;
            }
        }

        if (quote != '\0')
        {
            throw new ArgumentException("The browser parameters contain an unterminated quote.", nameof(commandLine));
        }

        AddCurrent(arguments, current, ref tokenStarted);
        return arguments;
    }

    private static void AddCurrent(List<string> arguments, StringBuilder current, ref bool tokenStarted)
    {
        if (!tokenStarted)
        {
            return;
        }

        arguments.Add(current.ToString());
        current.Clear();
        tokenStarted = false;
    }
}
