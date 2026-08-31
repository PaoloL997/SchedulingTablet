using System.Reflection;
using Xunit;

namespace Brembana.Tests;

internal static class Program
{
    public static int Main()
    {
        var cases = typeof(Program).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.GetCustomAttribute<FactAttribute>() != null)
                .Select(m => (Type: t, Method: m)))
            .ToList();

        var passed = 0;
        var failed = 0;
        foreach (var (type, method) in cases)
        {
            var name = $"{type.Name}.{method.Name}";
            try
            {
                var instance = Activator.CreateInstance(type);
                method.Invoke(instance, null);
                passed++;
                Console.WriteLine($"ok   {name}");
            }
            catch (TargetInvocationException ex)
            {
                failed++;
                Console.WriteLine($"FAIL {name}");
                Console.WriteLine($"     {ex.InnerException}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed, {cases.Count} total");
        return failed == 0 ? 0 : 1;
    }
}
