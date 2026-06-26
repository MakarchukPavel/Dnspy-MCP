using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace DnSpyMcp.TestTarget;

internal static class Program
{
    // Static fields — for list_static_fields / read_memory tests.
    public static int TickCounter;
    public static string StateLabel = "initial";
    public static readonly List<Widget> AliveWidgets = new();

    private static void Main(string[] args)
    {
        Console.WriteLine($"dnspymcptest PID={Process.GetCurrentProcess().Id}");
        Console.WriteLine($"  CLR  : {Environment.Version}");
        Console.WriteLine($"  args : [{string.Join(", ", args)}]");
        Console.WriteLine("Ready. Waiting for debugger. Ctrl+C to quit.");

        // Allocate some objects up front so heap tools have something to find.
        for (int i = 0; i < 10; i++)
            AliveWidgets.Add(new Widget($"widget-{i}", i * 7));

        var loopCancel = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; loopCancel.Set(); };

        while (!loopCancel.IsSet)
        {
            TickCounter++;
            StateLabel = TickCounter % 2 == 0 ? "even" : "odd";
            var result = Compute(TickCounter, TickCounter * 3);
            // Pass a Widget through a method each tick so value-based breakpoint
            // conditions (arg0.Value == N, arg0.Kind == 'Gadget', arg0.Name == ...)
            // have a live object argument to evaluate against.
            Inspect(AliveWidgets[TickCounter % AliveWidgets.Count]);
            if (TickCounter % 10 == 0)
                Console.WriteLine($"[tick {TickCounter}] state={StateLabel} compute={result} widgets={AliveWidgets.Count}");
            if (TickCounter % 50 == 0)
                Churn();
            Thread.Sleep(500);
        }

        Console.WriteLine("bye.");
    }

    // Deliberately simple call chain for breakpoint / step / clrstack tests.
    private static int Compute(int a, int b)
    {
        var sum = Add(a, b);
        var prod = Multiply(a, b);
        return sum + prod;
    }

    private static int Add(int a, int b) => a + b;

    // Receives a Widget every tick — exists so value-based conditional
    // breakpoints can gate on an object argument's fields (arg0.Value,
    // arg0.Name, arg0.Kind). Returns the value so it isn't optimized away.
    private static int Inspect(Widget w) => w.Value;

    // Overloads — used by reverse_list_overloads / signature-selection tests.
    public static string Greet(string name) => $"hi {name}";
    public static string Greet(string name, int times) => string.Concat(Enumerable.Repeat($"hi {name} ", times));
    public static string Greet(string greeting, string name) => $"{greeting}, {name}";

    private static int Multiply(int a, int b)
    {
        int acc = 0;
        for (int i = 0; i < b; i++)
            acc += a;
        return acc;
    }

    // Allocate + release to exercise GC / heap_stats.
    private static void Churn()
    {
        var tmp = new List<Widget>();
        for (int i = 0; i < 100; i++)
            tmp.Add(new Widget($"churn-{i}", i));
        tmp.Clear();
    }
}

// Top-level + nested type pair used by reverse_list_methods nested-type
// regression test (verifies the FindReflection fallback handles `+` ↔ `/`
// nested-type separator both ways).
public sealed class OuterContainer
{
    public sealed class InnerNested
    {
        public int Echo(int x) => x;
    }
}

// Tiny inheritance hierarchy used by the reverse_subtypes / *_overrides /
// *_overridden_by_base tests. Not exercised at runtime — exists purely so
// the metadata has a virtual base + override pair to query.
public abstract class Animal
{
    public abstract string Speak();
    public virtual string Habitat => "earth";
}

// Interface + impl for reverse_interface_*_implemented_by tests.
public interface IPet
{
    string Nickname { get; }
    event EventHandler<string>? Renamed;
    void Pat();
}

[Tag("ExampleCat")]
public sealed class Cat : Animal, IPet
{
    public override string Speak() => "meow";
    public override string Habitat => "indoor";

    public string Nickname => "Mr. Whiskers";
    public event EventHandler<string>? Renamed;
    public void Pat()
    {
        Renamed?.Invoke(this, Nickname);
    }
}

// Static class + extension method for reverse_type_extension_methods tests.
public static class CatExtensions
{
    public static string Describe(this Cat cat) => $"{cat.Nickname} ({cat.Habitat})";
}

// Custom attribute used by reverse_find_attribute_usage tests — defined in
// the test target so it resolves through ResolveTypes(workspace).
[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class TagAttribute : Attribute
{
    public string Name { get; }
    public TagAttribute(string name) => Name = name;
}

// Enum used to verify the struct-decoder maps numeric values to member names.
public enum WidgetKind
{
    Unknown = 0,
    Gadget = 1,
    Gizmo = 2,
    Doohickey = 7,
}

public sealed class Widget
{
    public string Name { get; }
    public int Value { get; set; }
    public DateTime CreatedAt { get; }
    // Value-type fields exercised by the struct-decoder regression tests:
    // Guid (well-known raw-read path) and an enum (name-mapping path).
    public Guid Id { get; }
    public WidgetKind Kind { get; }

    public Widget(string name, int value)
    {
        Name = name;
        Value = value;
        CreatedAt = DateTime.UtcNow;
        Id = Guid.NewGuid();
        Kind = (WidgetKind)(value % 3); // 0/1/2 -> Unknown/Gadget/Gizmo
    }

    public override string ToString() => $"Widget({Name}, {Value})";

    // Func-eval (eval.call) targets: a computed property (NOT an auto-property,
    // so it must RUN to produce a value), a zero-arg method, and a thrower.
    public string Label => $"{Name}#{Value}";
    public int Doubled() => Value * 2;
    public string Boom() => throw new InvalidOperationException("boom from Widget");
}
