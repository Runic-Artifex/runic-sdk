// Writes the scaling suite's sized ViewModels (tests/dotnet/Runic.Application.Scaling.Tests/SizedFixtures.cs).
// A case runs at two sizes (budgets.json "sizes"), and a generated Bridge needs its members at compile time, so each
// size is its own ViewModel, in a CommunityToolkit.Mvvm and a ReactiveUI flavour. Run it after changing the sizes:
//   bun tests/scaling/generate-fixtures.mjs
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(fileURLToPath(import.meta.url), "../../..");
const output = resolve(root, "tests/dotnet/Runic.Application.Scaling.Tests/SizedFixtures.cs");

// Scalar members of the wide ViewModels, and keyed collections of the collection ViewModels.
export const memberSizes = [20, 80];
export const collectionSizes = [1, 4];

const flavours = {
  Toolkit: { base: "ObservableObject", set: field => `SetProperty(ref ${field}, value)`, notify: "OnPropertyChanged(new PropertyChangedEventArgs(member))" },
  Reactive: { base: "ReactiveObject", set: field => `this.RaiseAndSetIfChanged(ref ${field}, value)`, notify: "this.RaisePropertyChanged(member ?? \"\")" },
};

const pad = index => String(index).padStart(2, "0");

// Even members are strings, which a unit compares as text members (text-splice/1); odd members are numbers, which it
// encodes to compare (WriteChangedMembers).
function wide(size, flavour, { base, set, notify }) {
  const name = `Wide${size}${flavour}`;
  const members = Array.from({ length: size }, (_, index) => ({ name: `M${pad(index)}`, field: `_m${pad(index)}`, text: index % 2 === 0 }));
  return `public sealed class ${name}ViewModel : ${base}, IWideViewModel
{
${members.map(({ field, text }) => text ? `    private string ${field} = "";` : `    private int ${field};`).join("\n")}

    [RunicIgnore] public GetterCounts Counts { get; } = new(${size});
    [RunicIgnore] public int Size => ${size};

${members.map(({ name, field, text }, index) => `    public ${text ? "string" : "int"} ${name} { get { Counts.Read(${index}); return ${field}; } set => ${set(field)}; }`).join("\n")}

    public void SetScalar(int index, int value)
    {
        switch (index)
        {
${members.map(({ name, text }, index) => `            case ${index}: ${name} = ${text ? "value.ToString(CultureInfo.InvariantCulture)" : "value"}; break;`).join("\n")}
            default: throw new ArgumentOutOfRangeException(nameof(index), index, null);
        }
    }

    public void Notify(string? member) => ${notify};
}

public sealed partial class ${name}Window : RunicWindow<${name}ViewModel>;
`;
}

function collections(size, flavour, { base, set, notify }) {
  const name = `Collections${size}${flavour}`;
  const rows = Array.from({ length: size }, (_, index) => `Rows${index}`);
  return `public sealed class ${name}ViewModel : ${base}, ICollectionsViewModel
{
${rows.map(member => `    private readonly ObservableCollection<ScalingRow> _${member.toLowerCase()} = [];`).join("\n")}
    private int _selected;

    // Rows0..Rows${size - 1}, then Selected.
    [RunicIgnore] public GetterCounts Counts { get; } = new(${size + 1});
    [RunicIgnore] public int Size => ${size};

${rows.map((member, index) => `    [RunicCollection(nameof(ScalingRow.Key))]
    public ObservableCollection<ScalingRow> ${member} { get { Counts.Read(${index}); return _${member.toLowerCase()}; } }`).join("\n\n")}

    public int Selected { get { Counts.Read(${size}); return _selected; } set => ${set("_selected")}; }

    public ObservableCollection<ScalingRow> Rows(int index) => index switch
    {
${rows.map((member, index) => `        ${index} => _${member.toLowerCase()},`).join("\n")}
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, null),
    };

    public void Notify(string? member) => ${notify};
}

public sealed partial class ${name}Window : RunicWindow<${name}ViewModel>;
`;
}

export function source() {
  const parts = [];
  for (const size of memberSizes) for (const [flavour, shape] of Object.entries(flavours)) parts.push(wide(size, flavour, shape));
  for (const size of collectionSizes) for (const [flavour, shape] of Object.entries(flavours)) parts.push(collections(size, flavour, shape));
  return `// Written by tests/scaling/generate-fixtures.mjs: edit the script, not this file.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ReactiveUI;
using Runic.Application.Views;

namespace Runic.Application.Scaling.Tests;

${parts.join("\n")}`;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  writeFileSync(output, source());
  console.log(`Wrote ${output}`);
}
