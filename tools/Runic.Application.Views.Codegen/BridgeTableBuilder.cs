using System.Text;

/// <summary>
/// One Bridge's descriptor table and the switch arms of its <c>Members</c> class (<see cref="BridgeTableSupport"/>).
/// Arms that read the ViewModel use <c>vm</c>, which each switch method casts once from <c>model</c>.
/// </summary>
internal sealed class BridgeTableBuilder(string modelType)
{
    private readonly List<string> _records = [];

    /// <summary>The ViewModel's C# type, which <c>vm</c> has in the arms.</summary>
    internal string ModelType => modelType;
    private readonly Dictionary<string, List<(int Id, string Code)>> _arms = new(StringComparer.Ordinal);

    /// <summary>Adds a record; each field is escaped (<see cref="BridgeTableSupport.Field"/>).</summary>
    internal void Record(params string[] fields) =>
        _records.Add(string.Join(' ', fields.Select(BridgeTableSupport.Field)));

    /// <summary>An arm that returns <paramref name="expression"/>, awaited in an async operation.</summary>
    internal void Return(string operation, int id, string expression) =>
        Arm(operation, id, BridgeTableSupport.Find(operation).Async ? $"return await {expression}.ConfigureAwait(false);" : $"return {expression};");

    /// <summary>An arm of a void operation that runs <paramref name="statement"/>.</summary>
    internal void Do(string operation, int id, string statement) => Arm(operation, id, $"{statement}; return;");

    /// <summary>An arm with its own statements, which end in a return or a throw.</summary>
    internal void Arm(string operation, int id, string code)
    {
        BridgeTableSupport.Find(operation);
        if (!_arms.TryGetValue(operation, out var arms)) _arms.Add(operation, arms = []);
        if (arms.Any(arm => arm.Id == id)) throw new InvalidOperationException($"{modelType}: two {operation} arms for id {id}.");
        arms.Add((id, code));
    }

    /// <summary>The <c>Table</c> field, read once per ViewModel type when the Bridge is first created.</summary>
    internal void AppendTable(StringBuilder cs)
    {
        cs.AppendLine($"    // This ViewModel's descriptors as data, one record per member (the format is in {BridgeTableSupport.FileName}).");
        cs.AppendLine($"    private static readonly {BridgeTableSupport.TableClass} Table = new(new Members(),");
        if (_records.Count == 0)
        {
            cs.AppendLine("        \"\"u8);");
            return;
        }
        cs.AppendLine("        \"\"\"");
        foreach (var record in _records) cs.AppendLine($"        {record}");
        cs.AppendLine("        \"\"\"u8);");
    }

    private static readonly System.Text.RegularExpressions.Regex UsesViewModel = new(@"(?<![\w.@""])vm\b",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool DeclaresVariable(string code) =>
        code.Contains(" is ", StringComparison.Ordinal) || code.Contains("var ", StringComparison.Ordinal) || code.Contains("out ", StringComparison.Ordinal);

    /// <summary>The <c>Members</c> class: one switch per operation that has arms, in the catalogue's order.</summary>
    internal void AppendMembers(StringBuilder cs)
    {
        cs.AppendLine($"    private sealed class Members : {BridgeTableSupport.MembersClass}");
        cs.AppendLine("    {");
        var first = true;
        foreach (var operation in BridgeTableSupport.Operations)
        {
            if (!_arms.TryGetValue(operation.Name, out var arms)) continue;
            if (!first) cs.AppendLine();
            first = false;
            cs.AppendLine($"        public override {(operation.Async ? "async " : "")}{operation.Returns} {operation.Signature}");
            cs.AppendLine("        {");
            if (operation.Model && arms.Any(arm => UsesViewModel.IsMatch(arm.Code))) cs.AppendLine($"            var vm = ({modelType})model;");
            cs.AppendLine("            switch (id)");
            cs.AppendLine("            {");
            foreach (var (id, code) in arms.OrderBy(arm => arm.Id))
            {
                var lines = code.TrimEnd('\n').Split('\n');
                if (lines.Length == 1)
                {
                    // All arms share the switch block's scope: an arm that may declare a variable (a pattern such as
                    // `is Row current`, var or out) gets a block of its own.
                    cs.AppendLine(DeclaresVariable(lines[0]) ? $"                case {id}: {{ {lines[0]} }}" : $"                case {id}: {lines[0]}");
                    continue;
                }
                cs.AppendLine($"                case {id}:");
                cs.AppendLine("                {");
                foreach (var line in lines) cs.AppendLine(line.Length == 0 ? "" : $"                    {line}");
                cs.AppendLine("                }");
            }
            cs.AppendLine("            }");
            cs.AppendLine("            throw new global::System.ArgumentOutOfRangeException(nameof(id));");
            cs.AppendLine("        }");
        }
        cs.AppendLine("    }");
    }
}

/// <summary>
/// The arms of one command (K record) and the flags they set. A convention describes its command library's execution;
/// the plan adds the declared failure, the key resolver, retirement and close.
/// </summary>
internal sealed class BridgeCommandTable(BridgeTableBuilder table, int id)
{
    // The K record flag of each descriptor operation; Start and EncodeResult serve the generated command runners.
    private static readonly Dictionary<string, char> FlagOf = new(StringComparer.Ordinal)
    {
        ["Execute"] = 'E', ["ExecuteResult"] = 'R', ["ExecuteStream"] = 'M', ["ReadInput"] = 'I', ["EncodeArgument"] = 'A',
        ["CanExecute"] = 'C', ["Subscribe"] = 'U', ["EncodeFailure"] = 'F', ["ObserveFailures"] = 'O', ["Resolve"] = 'K',
        ["ResolveAsync"] = 'Y',
    };

    private readonly SortedSet<char> _flags = [];

    internal int Id => id;

    internal string ModelType => table.ModelType;

    /// <summary>The record's flags in a stable order, or "-".</summary>
    internal string Flags => _flags.Count == 0 ? "-" : string.Concat(_flags);

    internal void Return(string operation, string expression)
    {
        table.Return(operation, id, expression);
        if (FlagOf.TryGetValue(operation, out var flag)) _flags.Add(flag);
    }

    internal void Arm(string operation, string code)
    {
        table.Arm(operation, id, code);
        if (FlagOf.TryGetValue(operation, out var flag)) _flags.Add(flag);
    }
}
