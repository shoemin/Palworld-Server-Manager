using System.Runtime.InteropServices;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]))
    throw new ArgumentException("Provide the absolute path of the isolated qualification DLL.");

string[] cases = ["mutual confirmation", "wrong password", "changed identity", "changed context",
    "malformed points both roles", "forged and malformed confirmations", "reflected confirmation",
    "confirmation replay across sessions", "identity shared point", "reflected first message",
    "HAZARD: zeroized output accepts empty confirmation", "HAZARD: key accessible before confirmation",
    "HAZARD: failure does not consume output"];

var library = NativeLibrary.Load(args[0]);
try
{
    var check = Marshal.GetDelegateForFunctionPointer<Qualify>(NativeLibrary.GetExport(library, "astra_spake2_qualify"));
    for (var i = 0; i < cases.Length; i++)
    {
        if (check(i) != 1) throw new InvalidOperationException($"Case {i} did not reproduce: {cases[i]}");
        Console.WriteLine($"REPRODUCED {i}: {cases[i]}");
    }
    // Independent stack-local exchanges only; no claim about shared session concurrency.
    Parallel.For(0, 128, i => {
        if (check(i % 10) != 1) throw new InvalidOperationException("Concurrent fixture failed.");
    });
    if (check(-1) != 0 || check(13) != 0) throw new InvalidOperationException("Unknown case accepted.");
    Console.WriteLine("PASS: 10 behavioral fixtures, 3 reproduced API hazards, 128 parallel isolated calls, invalid case rejection.");
}
finally { NativeLibrary.Free(library); }

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int Qualify(int id);
