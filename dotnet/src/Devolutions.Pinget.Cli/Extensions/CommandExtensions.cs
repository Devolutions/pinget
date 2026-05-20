using System.CommandLine;

namespace Devolutions.Pinget.Cli.Extensions;

internal static partial class Extensions
{
    extension(Command command)
    {       
        public Command WithAliases(params string[] aliases)
        {
            foreach (var alias in aliases)
            {
                command.AddAlias(alias);
            }
            return command;
        }
    }
}
