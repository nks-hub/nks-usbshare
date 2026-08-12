namespace NksUsbShare.Tests;

/// <summary>
/// A deliberately tiny test harness. The project takes no third-party dependencies, so it
/// brings no test framework either: tests are plain methods, failures are exceptions, and the
/// process exit code is what CI reads.
/// </summary>
public static class TestRunner
{
    private static readonly List<(string Suite, string Name, Func<Task> Body)> Tests = [];
    private static int _passed, _failed, _skipped;

    public static void Add(string suite, string name, Action body)
        => Tests.Add((suite, name, () => { body(); return Task.CompletedTask; }));

    public static void Add(string suite, string name, Func<Task> body) => Tests.Add((suite, name, body));

    public static async Task<int> RunAsync(string? filter)
    {
        var selected = Tests
            .Where(t => filter is null || t.Suite.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                       || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var currentSuite = "";
        foreach (var (suite, name, body) in selected)
        {
            if (suite != currentSuite)
            {
                currentSuite = suite;
                Console.WriteLine();
                Console.WriteLine($"  {suite}");
            }

            try
            {
                await body();
                _passed++;
                Write(ConsoleColor.Green, "    PASS  ");
                Console.WriteLine(name);
            }
            catch (SkipException ex)
            {
                _skipped++;
                Write(ConsoleColor.DarkYellow, "    SKIP  ");
                Console.WriteLine($"{name} — {ex.Message}");
            }
            catch (Exception ex)
            {
                _failed++;
                Write(ConsoleColor.Red, "    FAIL  ");
                Console.WriteLine(name);
                Console.WriteLine($"          {ex.Message.ReplaceLineEndings("\n          ")}");
                if (ex.StackTrace is { } stack)
                {
                    var firstFrame = stack.Split('\n').FirstOrDefault()?.Trim();
                    if (firstFrame is not null) Console.WriteLine($"          {firstFrame}");
                }
            }
        }

        Console.WriteLine();
        Write(_failed == 0 ? ConsoleColor.Green : ConsoleColor.Red,
            $"  {_passed} passed, {_failed} failed, {_skipped} skipped\n");
        Console.WriteLine();
        return _failed == 0 ? 0 : 1;
    }

    private static void Write(ConsoleColor color, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}

/// <summary>Thrown by a test that cannot run in the current environment (no hardware, wrong OS).</summary>
public sealed class SkipException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string because)
    {
        if (!condition) throw new Exception($"Expected true: {because}");
    }

    public static void False(bool condition, string because)
    {
        if (condition) throw new Exception($"Expected false: {because}");
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}: expected <{expected}>, got <{actual}>");
    }

    public static void SequenceEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string what)
    {
        if (expected.SequenceEqual(actual)) return;
        throw new Exception($"{what}: expected [{Convert.ToHexString(expected)}], got [{Convert.ToHexString(actual)}]");
    }

    public static void NotNull<T>(T? value, string what) where T : class
    {
        if (value is null) throw new Exception($"{what}: expected a value, got null");
    }

    public static async Task Throws<TException>(Func<Task> action, string what) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new Exception($"{what}: expected {typeof(TException).Name}, got {ex.GetType().Name}: {ex.Message}");
        }
        throw new Exception($"{what}: expected {typeof(TException).Name}, but nothing was thrown");
    }

    public static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new Exception($"{what}: expected {typeof(TException).Name}, got {ex.GetType().Name}: {ex.Message}");
        }
        throw new Exception($"{what}: expected {typeof(TException).Name}, but nothing was thrown");
    }

    public static void Skip(string reason) => throw new SkipException(reason);
}
