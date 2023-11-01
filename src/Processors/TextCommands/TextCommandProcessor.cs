using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DSharpPlus.CommandAll.Commands;
using DSharpPlus.CommandAll.Converters;
using DSharpPlus.CommandAll.EventArgs;
using DSharpPlus.CommandAll.Exceptions;
using DSharpPlus.CommandAll.Processors.TextCommands;
using DSharpPlus.CommandAll.Processors.TextCommands.Parsing;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DSharpPlus.CommandAll.Processors
{
    public sealed class TextCommandProcessor : ICommandProcessor<MessageCreateEventArgs>
    {
        public IReadOnlyDictionary<Type, ConverterDelegate<MessageCreateEventArgs>> Converters { get; private set; } = new Dictionary<Type, ConverterDelegate<MessageCreateEventArgs>>();
        public ResolvePrefixDelegateAsync PrefixResolver { get; private set; } = new DefaultPrefixResolver("!").ResolvePrefixAsync;
        public TextArgumentSplicer TextArgumentSplicer { get; private set; } = DefaultTextArgumentSplicer.Splice;

        private readonly Dictionary<Type, ITextArgumentConverter> _converters = [];
        private CommandAllExtension? _extension;
        private ILogger<TextCommandProcessor> _logger = NullLogger<TextCommandProcessor>.Instance;
        private bool _eventsRegistered;

        public void AddConverter<T>(ITextArgumentConverter<T> converter) => _converters.Add(typeof(T), converter);
        public void AddConverter(Type type, ITextArgumentConverter converter)
        {
            if (!converter.GetType().IsAssignableTo(typeof(ITextArgumentConverter<>).MakeGenericType(type)))
            {
                throw new ArgumentException($"Type '{converter.GetType().FullName}' must implement '{typeof(ITextArgumentConverter<>).MakeGenericType(type).FullName}'", nameof(converter));
            }

            _converters.TryAdd(type, converter);
        }
        public void AddConverters(Assembly assembly) => AddConverters(assembly.GetTypes());
        public void AddConverters(IEnumerable<Type> types)
        {
            foreach (Type type in types)
            {
                // Ignore types that don't have a concrete implementation (abstract classes or interfaces)
                // Additionally ignore types that have open generics (ITextArgumentConverter<T>) instead of closed generics (ITextArgumentConverter<string>)
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                {
                    continue;
                }

                // Check if the type implements ITextArgumentConverter<T>
                Type? genericArgumentConverter = type.GetInterfaces().FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ITextArgumentConverter<>));
                if (genericArgumentConverter is null)
                {
                    continue;
                }

                try
                {
                    object converter;

                    // Check to see if we have a service provider available
                    if (_extension is not null)
                    {
                        // If we do, try to create the converter using the service provider.
                        converter = ActivatorUtilities.CreateInstance(_extension.ServiceProvider, type);
                    }
                    else
                    {
                        // If we don't, try using a parameterless constructor.
                        converter = Activator.CreateInstance(type) ?? throw new InvalidOperationException($"Failed to create instance of {type.FullName ?? type.Name}");
                    }

                    // GenericTypeArguments[0] here is the T in ITextArgumentConverter<T>
                    AddConverter(genericArgumentConverter.GenericTypeArguments[0], (ITextArgumentConverter)converter);
                }
                catch (Exception error)
                {
                    // Log the error if possible
                    TextLogging.FailedConverterCreation(_logger, type.FullName ?? type.Name, error);
                }
            }
        }

        public Task ConfigureAsync(CommandAllExtension extension)
        {
            _extension = extension;
            _logger = extension.ServiceProvider.GetService<ILogger<TextCommandProcessor>>() ?? NullLogger<TextCommandProcessor>.Instance;

            AddConverters(typeof(TextCommandProcessor).Assembly);
            Dictionary<Type, ConverterDelegate<MessageCreateEventArgs>> converters = [];
            foreach ((Type type, ITextArgumentConverter converter) in _converters)
            {
                MethodInfo executeConvertAsyncMethod = typeof(TextCommandProcessor)
                    .GetMethod(nameof(ExecuteConvertAsync), BindingFlags.NonPublic | BindingFlags.Static)?
                    .MakeGenericMethod(type) ?? throw new InvalidOperationException($"Method {nameof(ExecuteConvertAsync)} does not exist");

                converters.Add(type, executeConvertAsyncMethod.CreateDelegate<ConverterDelegate<MessageCreateEventArgs>>(converter));
            }

            Converters = converters.ToFrozenDictionary();
            if (!_eventsRegistered)
            {
                _eventsRegistered = true;
                extension.Client.MessageCreated += ExecuteTextCommandAsync;
            }

            return Task.CompletedTask;
        }

        public async Task ExecuteTextCommandAsync(DiscordClient client, MessageCreateEventArgs eventArgs)
        {
            if (_extension is null)
            {
                throw new InvalidOperationException("TextCommandProcessor has not been configured.");
            }
            else if (eventArgs.Author.IsBot || eventArgs.Message.Content.Length == 0)
            {
                return;
            }

            int prefixLength = await PrefixResolver(_extension, eventArgs.Message);
            if (prefixLength < 0)
            {
                return;
            }

            string commandText = eventArgs.Message.Content[prefixLength..];
            int index = commandText.IndexOf(' ');
            if (index == -1)
            {
                index = commandText.Length;
            }

            if (!_extension.Commands.TryGetValue(commandText[..index], out Command? command))
            {
                return;
            }

            int nextIndex = 0;
            while (nextIndex != -1)
            {
                // Add 2 to skip the space
                nextIndex = commandText.IndexOf(' ', nextIndex + 2);
                if (nextIndex == -1)
                {
                    break;
                }

                // Backspace once to trim the space
                nextIndex--;

                // Resolve subcommands
                Command? foundCommand = command.Subcommands.FirstOrDefault(command => command.Name.Equals(commandText[index..nextIndex], StringComparison.OrdinalIgnoreCase));
                if (foundCommand is null)
                {
                    break;
                }

                index = nextIndex;
                command = foundCommand;
            }

            Debug.Assert(command is not null, "Command should not be null at this point.");
            TextConverterContext converterContext = new()
            {
                Splicer = TextArgumentSplicer,
                Extension = _extension,
                Channel = eventArgs.Channel,
                Command = command,
                User = eventArgs.Author,
                TextArguments = commandText[index..]
            };

            CommandContext? commandContext = await ParseArgumentsAsync(converterContext, eventArgs);
            if (commandContext is null)
            {
                return;
            }

            await _extension.CommandExecutor.ExecuteAsync(commandContext);
        }

        public void SetPrefixResolver(ResolvePrefixDelegateAsync prefixResolver) => PrefixResolver = prefixResolver;
        public void SetTextArgumentSplicer(TextArgumentSplicer textArgumentSplicer) => TextArgumentSplicer = textArgumentSplicer;

        private async Task<CommandContext?> ParseArgumentsAsync(TextConverterContext converterContext, MessageCreateEventArgs eventArgs)
        {
            Dictionary<CommandArgument, object?> parsedArguments = [];
            try
            {
                while (converterContext.NextArgument())
                {
                    IOptional optional = await Converters[converterContext.Argument.Type](converterContext, eventArgs);
                    if (!optional.HasValue)
                    {
                        break;
                    }

                    parsedArguments.Add(converterContext.Argument, optional.RawValue);
                }
            }
            catch (Exception error)
            {
                if (_extension is null)
                {
                    return null;
                }

                await _extension._commandErrored.InvokeAsync(converterContext.Extension, new CommandErroredEventArgs()
                {
                    Context = new TextContext()
                    {
                        User = eventArgs.Author,
                        Channel = eventArgs.Channel,
                        Extension = converterContext.Extension,
                        Command = converterContext.Command,
                        Arguments = parsedArguments,
                        Message = eventArgs.Message,
                    },
                    Exception = new ParseArgumentException(converterContext.Argument, error)
                });
            }

            return new TextContext()
            {
                User = eventArgs.Author,
                Channel = eventArgs.Channel,
                Extension = converterContext.Extension,
                Command = converterContext.Command,
                Arguments = parsedArguments,
                Message = eventArgs.Message
            };
        }

        private static async Task<IOptional> ExecuteConvertAsync<T>(ITextArgumentConverter<T> converter, ConverterContext context, MessageCreateEventArgs eventArgs)
        {
            if (!converter.RequiresText || context.As<TextConverterContext>().NextTextArgument())
            {
                return await converter.ConvertAsync(context, eventArgs);
            }
            else if (context.Argument.DefaultValue.HasValue)
            {
                return Optional.FromValue((T?)context.Argument.DefaultValue.Value);
            }

            throw new ParseArgumentException(context.Argument, message: $"Missing text argument for {context.Argument.Name}.");
        }
    }
}
