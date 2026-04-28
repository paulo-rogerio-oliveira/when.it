using Microsoft.Extensions.Options;

namespace DbSense.Core.Tests.Helpers;

internal static class TestOptions
{
    public static IOptions<T> Wrap<T>(T value) where T : class => Options.Create(value);
}
