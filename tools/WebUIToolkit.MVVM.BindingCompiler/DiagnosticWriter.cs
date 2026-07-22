using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using WebUIToolkit.MVVM.Build.Compiler;

namespace WebUIToolkit.MVVM.BindingCompiler;

internal static class DiagnosticWriter
{
    public static void Write(TextWriter writer, IEnumerable<BindingDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(diagnostics);

        foreach (BindingDiagnostic diagnostic in diagnostics)
        {
            Write(
                writer,
                diagnostic.Span,
                diagnostic.Severity,
                diagnostic.Id,
                diagnostic.Message);
        }
    }

    public static void Write(
        TextWriter writer,
        BindingSourceSpan span,
        BindingDiagnosticSeverity severity,
        string id,
        string message)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(message);

        writer.Write(TerminalText.Path(span.LogicalPath));
        writer.Write('(');
        writer.Write((span.Start.Line + 1).ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write((span.Start.Column + 1).ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write((span.End.Line + 1).ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write((span.End.Column + 1).ToString(CultureInfo.InvariantCulture));
        writer.Write("): ");
        writer.Write(severity == BindingDiagnosticSeverity.Error ? "error" : "warning");
        writer.Write(' ');
        writer.Write(id);
        writer.Write(": ");
        writer.WriteLine(TerminalText.Message(message));
    }
}
