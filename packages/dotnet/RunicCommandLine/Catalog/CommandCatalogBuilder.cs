using System;
using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;

namespace RunicCommandLine;

/// <summary>Builds and validates an immutable command catalog.</summary>
public sealed class CommandCatalogBuilder
{
    private readonly List<CommandBuilderNode> _commands = [];

    /// <summary>Adds a typed root command.</summary>
    public CommandBuilder<TOptions, THandler, TResult> Command<TOptions, THandler, TResult>(string name)
        where THandler : notnull, ICommandHandler<TOptions, TResult>
    {
        var command = new CommandBuilder<TOptions, THandler, TResult>(name);
        _commands.Add(command.Node);
        return command;
    }

    /// <summary>Adds and configures a typed root command.</summary>
    public CommandCatalogBuilder Command<TOptions, THandler, TResult>(
        string name,
        Action<CommandBuilder<TOptions, THandler, TResult>> configure)
        where THandler : notnull, ICommandHandler<TOptions, TResult>
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Command<TOptions, THandler, TResult>(name));
        return this;
    }

    /// <summary>Validates and freezes all registered definitions.</summary>
    public CommandCatalog Build()
    {
        var issues = new List<CommandCatalogIssue>();
        ValidateSiblings(_commands, "root", issues);

        foreach (CommandBuilderNode command in _commands)
        {
            command.Validate(command.Name, issues);
        }

        if (issues.Count != 0)
        {
            throw new CommandCatalogValidationException(CommandDescriptor.Freeze(issues));
        }

        var descriptors = new List<CommandDescriptor>(_commands.Count);
        foreach (CommandBuilderNode command in _commands)
        {
            descriptors.Add(command.Freeze());
        }

        return new CommandCatalog(CommandDescriptor.Freeze(descriptors));
    }

    internal static void ValidateSiblings(
        IReadOnlyList<CommandBuilderNode> commands,
        string location,
        List<CommandCatalogIssue> issues)
    {
        var spellings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CommandBuilderNode command in commands)
        {
            RegisterSpelling(command.Name, command.Name);
            foreach (string alias in command.Aliases)
            {
                RegisterSpelling(alias, command.Name);
            }
        }

        void RegisterSpelling(string spelling, string owner)
        {
            if (!spellings.TryAdd(spelling, owner))
            {
                issues.Add(new CommandCatalogIssue(
                    "RCLI0004",
                    location,
                    $"Command spelling '{spelling}' is registered more than once."));
            }
        }
    }
}

/// <summary>Configures one closed typed command registration.</summary>
public sealed class CommandBuilder<TOptions, THandler, TResult>
    where THandler : notnull, ICommandHandler<TOptions, TResult>
{
    private readonly TypedCommandBuilderNode<TOptions, THandler, TResult> _node;

    internal CommandBuilder(string name)
    {
        _node = new TypedCommandBuilderNode<TOptions, THandler, TResult>(name);
    }

    internal CommandBuilderNode Node => _node;

    /// <summary>Registers the closed options binder.</summary>
    public CommandBuilder<TOptions, THandler, TResult> BindWith(ICommandOptionsBinder<TOptions> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        _node.Binder = binder;
        return this;
    }

    /// <summary>Registers the closed handler factory.</summary>
    public CommandBuilder<TOptions, THandler, TResult> CreateHandlerWith(ICommandHandlerFactory<THandler> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _node.HandlerFactory = factory;
        return this;
    }

    /// <summary>Registers the closed result codec.</summary>
    public CommandBuilder<TOptions, THandler, TResult> Produces(ICommandResultCodec<TResult> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _node.ResultCodec = codec;
        return this;
    }

    /// <summary>Adds a parser-neutral option.</summary>
    public CommandBuilder<TOptions, THandler, TResult> Option(
        string id,
        string name,
        CommandArity arity,
        CommandOptionRepeatPolicy repeatPolicy = CommandOptionRepeatPolicy.Error,
        bool isSensitive = false,
        string? descriptionKey = null,
        params string[] aliases)
    {
        _node.AddOption(id, name, arity, repeatPolicy, isSensitive, descriptionKey, aliases);
        return this;
    }

    /// <summary>Adds a positional argument.</summary>
    public CommandBuilder<TOptions, THandler, TResult> Argument(
        string id,
        string name,
        CommandArity arity,
        bool isSensitive = false,
        string? descriptionKey = null)
    {
        _node.AddArgument(id, name, arity, isSensitive, descriptionKey);
        return this;
    }

    /// <summary>Adds command aliases.</summary>
    public CommandBuilder<TOptions, THandler, TResult> Alias(params string[] aliases)
    {
        _node.AddAliases(aliases);
        return this;
    }

    /// <summary>Sets the localization key for help text.</summary>
    public CommandBuilder<TOptions, THandler, TResult> Describe(string descriptionKey)
    {
        _node.SetDescription(descriptionKey);
        return this;
    }

    /// <summary>Adds a typed child command.</summary>
    public CommandBuilder<TChildOptions, TChildHandler, TChildResult> Subcommand<TChildOptions, TChildHandler, TChildResult>(
        string name)
        where TChildHandler : notnull, ICommandHandler<TChildOptions, TChildResult>
    {
        var command = new CommandBuilder<TChildOptions, TChildHandler, TChildResult>(name);
        _node.AddSubcommand(command.Node);
        return command;
    }

    /// <summary>Adds and configures a typed child command.</summary>
    public CommandBuilder<TOptions, THandler, TResult> Subcommand<TChildOptions, TChildHandler, TChildResult>(
        string name,
        Action<CommandBuilder<TChildOptions, TChildHandler, TChildResult>> configure)
        where TChildHandler : notnull, ICommandHandler<TChildOptions, TChildResult>
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Subcommand<TChildOptions, TChildHandler, TChildResult>(name));
        return this;
    }

}

internal sealed class TypedCommandBuilderNode<TOptions, THandler, TResult> : CommandBuilderNode
    where THandler : notnull, ICommandHandler<TOptions, TResult>
{
    private string? _frozenPayloadType;
    private JsonTypeInfo<TResult>? _frozenTypeInfo;

    internal TypedCommandBuilderNode(string name)
        : base(name)
    {
    }

    internal ICommandOptionsBinder<TOptions>? Binder { get; set; }

    internal ICommandHandlerFactory<THandler>? HandlerFactory { get; set; }

    internal ICommandResultCodec<TResult>? ResultCodec { get; set; }

    internal override void ValidateRegistration(string location, List<CommandCatalogIssue> issues)
    {
        _frozenPayloadType = null;
        _frozenTypeInfo = null;

        if (Binder is null)
        {
            issues.Add(new CommandCatalogIssue("RCLI0010", location, "A closed options binder is required."));
        }

        if (HandlerFactory is null)
        {
            issues.Add(new CommandCatalogIssue("RCLI0011", location, "A closed handler factory is required."));
        }

        if (ResultCodec is null)
        {
            issues.Add(new CommandCatalogIssue("RCLI0012", location, "A closed result codec is required."));
        }
        else
        {
            try
            {
                string payloadType = ResultCodec.PayloadType;
                JsonTypeInfo<TResult> typeInfo = ResultCodec.TypeInfo;
                if (typeInfo is null)
                {
                    throw new ArgumentException("Source-generated type metadata is required.");
                }

                CommandResponseValidation.ValidatePayloadType(payloadType, nameof(ResultCodec.PayloadType));
                _frozenPayloadType = payloadType;
                _frozenTypeInfo = typeInfo;
            }
            catch (Exception exception) when (exception is not (
                OutOfMemoryException or
                AccessViolationException or
                AppDomainUnloadedException or
                BadImageFormatException))
            {
                issues.Add(new CommandCatalogIssue(
                    "RCLI0017",
                    location,
                    "The result codec requires a valid <lower-name>/<positive-major> payload identity."));
            }
        }
    }

    internal override CommandRegistration CreateRegistration() =>
        new CommandRegistration<TOptions, THandler, TResult>(
            Binder!,
            HandlerFactory!,
            new FrozenCommandResultCodec<TResult>(
                ResultCodec!,
                _frozenPayloadType!,
                _frozenTypeInfo!));
}

internal abstract class CommandBuilderNode
{
    private readonly List<string> _aliases = [];
    private readonly List<OptionDefinition> _options = [];
    private readonly List<ArgumentDefinition> _arguments = [];
    private readonly List<CommandBuilderNode> _subcommands = [];

    internal CommandBuilderNode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    internal string Name { get; }

    internal IReadOnlyList<string> Aliases => _aliases;

    private string? DescriptionKey { get; set; }

    internal void AddAliases(IEnumerable<string> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        foreach (string alias in aliases)
        {
            ArgumentNullException.ThrowIfNull(alias);
            _aliases.Add(alias);
        }
    }

    internal void SetDescription(string descriptionKey)
    {
        ArgumentNullException.ThrowIfNull(descriptionKey);
        DescriptionKey = descriptionKey;
    }

    internal void AddOption(
        string id,
        string name,
        CommandArity arity,
        CommandOptionRepeatPolicy repeatPolicy,
        bool isSensitive,
        string? descriptionKey,
        IEnumerable<string> aliases)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(aliases);
        var aliasCopy = new List<string>();
        foreach (string alias in aliases)
        {
            ArgumentNullException.ThrowIfNull(alias);
            aliasCopy.Add(alias);
        }

        _options.Add(new OptionDefinition(id, name, aliasCopy, arity, repeatPolicy, isSensitive, descriptionKey));
    }

    internal void AddArgument(string id, string name, CommandArity arity, bool isSensitive, string? descriptionKey)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        _arguments.Add(new ArgumentDefinition(id, name, arity, isSensitive, descriptionKey));
    }

    internal void AddSubcommand(CommandBuilderNode command) => _subcommands.Add(command);

    internal void Validate(string path, List<CommandCatalogIssue> issues)
    {
        ValidateCommandName(Name, path, issues);
        foreach (string alias in _aliases)
        {
            ValidateCommandName(alias, path, issues);
        }

        if (DescriptionKey is not null && string.IsNullOrWhiteSpace(DescriptionKey))
        {
            issues.Add(new CommandCatalogIssue("RCLI0003", path, "The description key cannot be empty."));
        }

        ValidateOptions(path, issues);
        ValidateArguments(path, issues);
        ValidateRegistration(path, issues);
        CommandCatalogBuilder.ValidateSiblings(_subcommands, path, issues);
        foreach (CommandBuilderNode subcommand in _subcommands)
        {
            subcommand.Validate($"{path} {subcommand.Name}", issues);
        }
    }

    internal abstract void ValidateRegistration(string location, List<CommandCatalogIssue> issues);

    internal abstract CommandRegistration CreateRegistration();

    internal CommandDescriptor Freeze()
    {
        var options = new List<CommandOptionDescriptor>(_options.Count);
        foreach (OptionDefinition option in _options)
        {
            options.Add(new CommandOptionDescriptor(
                option.Id,
                option.Name,
                CommandOptionDescriptor.Freeze(option.Aliases),
                option.Arity,
                option.RepeatPolicy,
                option.IsSensitive,
                option.DescriptionKey));
        }

        var arguments = new List<CommandArgumentDescriptor>(_arguments.Count);
        foreach (ArgumentDefinition argument in _arguments)
        {
            arguments.Add(new CommandArgumentDescriptor(
                argument.Id,
                argument.Name,
                argument.Arity,
                argument.IsSensitive,
                argument.DescriptionKey));
        }

        var children = new List<CommandDescriptor>(_subcommands.Count);
        foreach (CommandBuilderNode child in _subcommands)
        {
            children.Add(child.Freeze());
        }

        return new CommandDescriptor(
            Name,
            CommandOptionDescriptor.Freeze(_aliases),
            DescriptionKey,
            CommandDescriptor.Freeze(options),
            CommandDescriptor.Freeze(arguments),
            CommandDescriptor.Freeze(children),
            CreateRegistration());
    }

    private void ValidateOptions(string path, List<CommandCatalogIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var spellings = new HashSet<string>(StringComparer.Ordinal);
        foreach (OptionDefinition option in _options)
        {
            if (!IsIdentifier(option.Id))
            {
                issues.Add(new CommandCatalogIssue("RCLI0005", path, $"Option ID '{option.Id}' is invalid."));
            }

            if (!ids.Add(option.Id))
            {
                issues.Add(new CommandCatalogIssue("RCLI0006", path, $"Option ID '{option.Id}' is registered more than once."));
            }

            ValidateOptionSpelling(option.Name);
            foreach (string alias in option.Aliases)
            {
                ValidateOptionSpelling(alias);
            }

            if (!Enum.IsDefined(option.RepeatPolicy))
            {
                issues.Add(new CommandCatalogIssue("RCLI0009", path, $"Option '{option.Name}' has an invalid repeat policy."));
            }

            if (option.DescriptionKey is not null && string.IsNullOrWhiteSpace(option.DescriptionKey))
            {
                issues.Add(new CommandCatalogIssue("RCLI0003", path, $"Option '{option.Name}' has an empty description key."));
            }
        }

        void ValidateOptionSpelling(string spelling)
        {
            if (!IsOptionSpelling(spelling) || IsReservedOption(spelling))
            {
                issues.Add(new CommandCatalogIssue("RCLI0007", path, $"Option spelling '{spelling}' is invalid or reserved."));
            }

            if (!spellings.Add(spelling))
            {
                issues.Add(new CommandCatalogIssue("RCLI0008", path, $"Option spelling '{spelling}' is registered more than once."));
            }
        }
    }

    private void ValidateArguments(string path, List<CommandCatalogIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        bool optionalSeen = false;
        for (int index = 0; index < _arguments.Count; index++)
        {
            ArgumentDefinition argument = _arguments[index];
            if (!IsIdentifier(argument.Id) || !IsIdentifier(argument.Name))
            {
                issues.Add(new CommandCatalogIssue("RCLI0013", path, $"Argument '{argument.Name}' has an invalid name or ID."));
            }

            if (!ids.Add(argument.Id))
            {
                issues.Add(new CommandCatalogIssue("RCLI0014", path, $"Argument ID '{argument.Id}' is registered more than once."));
            }

            if (argument.DescriptionKey is not null && string.IsNullOrWhiteSpace(argument.DescriptionKey))
            {
                issues.Add(new CommandCatalogIssue("RCLI0003", path, $"Argument '{argument.Name}' has an empty description key."));
            }

            if (optionalSeen && argument.Arity.Minimum != 0)
            {
                issues.Add(new CommandCatalogIssue("RCLI0015", path, "A required argument cannot follow an optional argument."));
            }

            optionalSeen |= argument.Arity.Minimum == 0;
            if (argument.Arity.Maximum is null && index != _arguments.Count - 1)
            {
                issues.Add(new CommandCatalogIssue("RCLI0016", path, "Only the final argument may have unbounded arity."));
            }
        }
    }

    private static void ValidateCommandName(string name, string path, List<CommandCatalogIssue> issues)
    {
        if (!IsIdentifier(name) || name is "help" or "version" or "output")
        {
            issues.Add(new CommandCatalogIssue("RCLI0002", path, $"Command name or alias '{name}' is invalid or reserved."));
        }
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || value[0] is < 'a' or > 'z')
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            if ((character is < 'a' or > 'z') && (character is < '0' or > '9') && character != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOptionSpelling(string value)
    {
        if (value.StartsWith("--", StringComparison.Ordinal))
        {
            return IsIdentifier(value[2..]);
        }

        if (value.Length > 0 && value[0] == '/')
        {
            return IsIdentifier(value[1..]);
        }

        return value.Length == 2 && value[0] == '-' &&
            ((value[1] is >= 'a' and <= 'z') || (value[1] is >= 'A' and <= 'Z'));
    }

    private static bool IsReservedOption(string value) =>
        value is "--help" or "-h" or "--version" or "--output";

    private sealed record OptionDefinition(
        string Id,
        string Name,
        IReadOnlyList<string> Aliases,
        CommandArity Arity,
        CommandOptionRepeatPolicy RepeatPolicy,
        bool IsSensitive,
        string? DescriptionKey);

    private sealed record ArgumentDefinition(
        string Id,
        string Name,
        CommandArity Arity,
        bool IsSensitive,
        string? DescriptionKey);
}
