using System.Reflection;

namespace Ashstrap.Core.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

public sealed class AssertionException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{what}: expected <{expected}>, got <{actual}>");
    }

    public static void Exists(string path) =>
        True(File.Exists(path) || Directory.Exists(path), $"Expected to still exist: {path}");

    public static void Gone(string path) =>
        True(!File.Exists(path) && !Directory.Exists(path), $"Expected to be removed: {path}");

    public static T Throws<T>(Action a) where T : Exception
    {
        try { a(); }
        catch (T ex) { return ex; }
        throw new AssertionException($"Expected {typeof(T).Name}");
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Where(m => filter is null || $"{m.DeclaringType!.Name}.{m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name)
            .ToList();

        int passed = 0, failed = 0;
        foreach (var t in tests)
        {
            var name = $"{t.DeclaringType!.Name}.{t.Name}";
            try
            {
                var r = t.Invoke(null, null);
                if (r is Task task) task.GetAwaiter().GetResult();
                Console.WriteLine($"  PASS  {name}");
                passed++;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : ex;
                Console.WriteLine($"  FAIL  {name}\n        {inner.GetType().Name}: {inner.Message}");
                if (inner is not AssertionException) Console.WriteLine(inner.StackTrace);
                failed++;
            }
        }
        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed, {tests.Count} total");
        return failed == 0 ? 0 : 1;
    }
}
