using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Runic.Application.Testing.Tests;

// ObservableValidator.GetErrors(null) returns every property's ValidationResult, each naming
// its property in MemberNames, so the object-level read must not repeat what GetErrors(member)
// reports. It has no View: an ObservableValidator in this assembly would fail the generator's
// Native AOT check (RUNICBRIDGE002) that CodegenCacheTests runs, so the test writes its
// validation with hand-written member metadata.
public sealed partial class ToolkitValidationViewModel : ObservableValidator
{
    [Required(ErrorMessage = "Name is required.")]
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [Range(1, 10, ErrorMessage = "Count must be between 1 and 10.")]
    [ObservableProperty]
    public partial int Count { get; set; }

    public void Validate() => ValidateAllProperties();
}
