# v0.2.5 Validation Notes

Static validation performed in the generation environment:

- all `.xaml` and `.csproj` files parse as XML;
- all project references resolve;
- XAML event handlers resolve to code-behind methods;
- changed C# files pass delimiter/string/comment lexical sanity checks;
- App/Core/SelfTest versions are aligned at 0.2.5;
- the old `Process.Start(info)?.Dispose()` fire-and-forget launch pattern is absent;
- the process service contains owned lifetime tracking, process exit-code capture, lifetime finalization, and UI lifetime events;
- Start/Stop/Force Stop UI controls are named and runtime-state controlled;
- a self-test verifies that the shipping process exit code is preferred when classifying a lifetime result.

This environment does not provide the Windows .NET SDK/WPF toolchain, so `build.cmd` on the test PC remains the authoritative compiler and self-test gate.
