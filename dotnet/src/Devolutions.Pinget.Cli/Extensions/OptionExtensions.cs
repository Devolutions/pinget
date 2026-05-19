using System.CommandLine;

namespace Devolutions.Pinget.Cli.Extensions;

internal static partial class Extensions
{
    extension<T>(Option<T> option)
    {
        public Option<T> WithAliases(params string[] aliases)
        {
            foreach (var alias in aliases)
            {
                option.AddAlias(alias);
            }
            return option;
        }
    }
}
