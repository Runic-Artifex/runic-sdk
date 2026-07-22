using System;
using System.Collections.Generic;
using System.Text.Json;

namespace WebUIToolkit.DependencyNotices.Rendering;

public static class NoticeManifestRenderer
{
    public static byte[] Render(NoticeRenderOptions options, IReadOnlyList<RenderedNoticeOutput> outputs)
    {
        List<NoticeManifestInput> inputs = new(options.Inputs);
        inputs.Sort(static (left, right) =>
        {
            int comparison = StringComparer.Ordinal.Compare(left.Name, right.Name);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Sha256, right.Sha256);
        });

        List<RenderedNoticeOutput> orderedOutputs = new(outputs);
        orderedOutputs.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.FileName, right.FileName));
        List<string> roots = SortedDistinct(options.SelectedRoots);
        List<string> profiles = SortedDistinct(options.Profiles);

        return RenderingUtilities.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("toolVersion", options.ToolVersion);
            writer.WritePropertyName("inputs");
            writer.WriteStartArray();
            foreach (NoticeManifestInput input in inputs)
            {
                writer.WriteStartObject();
                writer.WriteString("name", input.Name);
                writer.WriteString("sha256", input.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (options.EvidenceLockSha256 is null)
            {
                writer.WriteNull("evidenceLockSha256");
            }
            else
            {
                writer.WriteString("evidenceLockSha256", options.EvidenceLockSha256);
            }

            WriteStrings(writer, "selectedRoots", roots);
            WriteStrings(writer, "profiles", profiles);
            writer.WritePropertyName("outputs");
            writer.WriteStartArray();
            foreach (RenderedNoticeOutput output in orderedOutputs)
            {
                writer.WriteStartObject();
                writer.WriteString("name", output.FileName);
                writer.WriteString("sha256", output.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static List<string> SortedDistinct(IReadOnlyList<string> source)
    {
        SortedSet<string> values = new(StringComparer.Ordinal);
        foreach (string value in source)
        {
            _ = values.Add(value);
        }

        return new List<string>(values);
    }

    private static void WriteStrings(Utf8JsonWriter writer, string propertyName, List<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
