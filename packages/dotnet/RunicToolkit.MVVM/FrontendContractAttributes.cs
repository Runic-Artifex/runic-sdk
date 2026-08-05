namespace RunicToolkit.MVVM;

/// <summary>Declares a ViewModel as the C# source of a generated frontend contract.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WebUiFrontendContractAttribute : Attribute
{
    /// <summary>Creates a C#-first frontend contract declaration.</summary>
    public WebUiFrontendContractAttribute(
        string name,
        string client,
        Type serializerContextType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(client);
        ArgumentNullException.ThrowIfNull(serializerContextType);
        Name = name;
        Client = client;
        SerializerContextType = serializerContextType;
    }

    /// <summary>Gets the stable wire contract name.</summary>
    public string Name { get; }

    /// <summary>Gets the generated client and C# contract group name.</summary>
    public string Client { get; }

    /// <summary>Gets the source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> type.</summary>
    public Type SerializerContextType { get; }

    /// <summary>Gets or sets the namespace for the generated C# adapter.</summary>
    public string GeneratedNamespace { get; set; } = string.Empty;

    /// <summary>Gets or sets the containing class name for the generated C# adapter.</summary>
    public string GeneratedClassName { get; set; } = string.Empty;
}

/// <summary>Declares a projected property in a C#-first frontend contract.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class WebUiFrontendPropertyAttribute : Attribute
{
    /// <summary>Creates a property declaration with an explicit stable wire ID.</summary>
    public WebUiFrontendPropertyAttribute(int id, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name;
    }

    /// <summary>Gets the stable wire member ID.</summary>
    public int Id { get; }

    /// <summary>Gets the frontend member name.</summary>
    public string Name { get; }

    /// <summary>Gets or sets the public ViewModel member used by the generated adapter.</summary>
    public string SourceMember { get; set; } = string.Empty;

    /// <summary>Gets or sets an explicit TypeScript type, or an empty value to infer it.</summary>
    public string TypeScriptType { get; set; } = string.Empty;

    /// <summary>Gets or sets the serializer-context property, or an empty value to infer it.</summary>
    public string JsonTypeInfoProperty { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the frontend may mutate the property.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Gets or sets whether validation patches are projected for the property.</summary>
    public bool IncludeValidation { get; set; }
}

/// <summary>Declares a projected collection in a C#-first frontend contract.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class WebUiFrontendCollectionAttribute : Attribute
{
    /// <summary>Creates a collection declaration with an explicit stable wire ID.</summary>
    public WebUiFrontendCollectionAttribute(int id, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name;
    }

    /// <summary>Gets the stable wire member ID.</summary>
    public int Id { get; }

    /// <summary>Gets the frontend member name.</summary>
    public string Name { get; }

    /// <summary>Gets or sets the public ViewModel collection used by the generated adapter.</summary>
    public string SourceMember { get; set; } = string.Empty;

    /// <summary>Gets or sets an explicit TypeScript item type, or an empty value to infer it.</summary>
    public string TypeScriptType { get; set; } = string.Empty;

    /// <summary>Gets or sets the serializer-context property for one item, or an empty value to infer it.</summary>
    public string JsonTypeInfoProperty { get; set; } = string.Empty;

    /// <summary>Gets or sets whether validation patches are projected for the collection.</summary>
    public bool IncludeValidation { get; set; }
}

/// <summary>Declares a projected command in a C#-first frontend contract.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class WebUiFrontendCommandAttribute : Attribute
{
    /// <summary>Creates a command declaration with an explicit stable wire ID.</summary>
    public WebUiFrontendCommandAttribute(int id, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name;
    }

    /// <summary>Gets the stable wire member ID.</summary>
    public int Id { get; }

    /// <summary>Gets the frontend member name.</summary>
    public string Name { get; }

    /// <summary>Gets or sets the public relay-command member used by the generated adapter.</summary>
    public string SourceMember { get; set; } = string.Empty;

    /// <summary>Gets or sets an explicit TypeScript argument type, or an empty value to infer it.</summary>
    public string TypeScriptArgument { get; set; } = string.Empty;

    /// <summary>Gets or sets the serializer-context property for the command argument.</summary>
    public string JsonTypeInfoProperty { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the generated adapter binds an asynchronous relay command.</summary>
    public bool IsAsync { get; set; }
}
