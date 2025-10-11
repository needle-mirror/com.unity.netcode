# Source generators

Understand how Netcode for Entities uses a source generator to automatically generate code for serialization, commands, and RPCs.

> [!NOTE]
> You can use the information on this page to debug issues with the source generator or extend its functionality. However, this is intended for advanced users. If you have any issues with the source generator, please file a bug report using the Unity Bug Reporter.

Netcode for Entities uses a [Roslyn source generator](https://docs.unity3d.com/Documentation/Manual/roslyn-analyzers.html) to automatically generate the following at compile time:

* All the serialization code for replicated components and buffers, `ICommand`, RPCs, and `IInputCommandData`.
* All the necessary boilerplate systems that handle RPCs and commands.
* Systems that copy to and from `IInputCommandData` to the underlying `ICommand` buffer.
* Other internal systems (mostly used for registration of replicated types).
* Extracting all the information from replicated types to avoid using reflection at runtime.

## Source generator structure

The project is organized as follows:

```
Unity.NetCode
- Editor
- Runtime
  -- SourceGenerators      Labels
  --- NetCodeGenerator.dll  *SourceGenerator*
  ---- Source~  (hidden, not handled by Unity)
  ------ NetCodeSourceGenerator
  ------- CodeGenerator
  ------- Generators
  ------- Helpers
  ------ Tests
  ------ SourceGenerators.sln
```

The `NetCodeSourceGenerator.dll` is generated from the `Source~` folder and used by the Editor compilation pipeline to inject the generated code into each assembly definition (including `Assembly-CSharp.dll` and similar).

### Source generator set up

The source generator .dll has some specific requirements to function correctly:

* It must not be imported by the Unity Editor or any platform, because it's incompatible with the Unity runtime.
* It must be labeled with the `SourceGenerator` label to be detected by the compilation pipeline.

By default, the source generator .dll in the package is already set up with the `SourceGenerator` label and is placed in the `Packages/com.unity.netcode/Runtime/SourceGenerators/Source~` folder. If these settings are disrupted after recompilation, you can restore them using the Editor, by editing the meta file, or restoring the previous meta file.

### Source generator output

By default, the Netcode for Entities generator puts all the generated files in the `Temp/NetcodeGenerated` folder, which is accessible from the **Multiplayer** menu in the Editor. A subfolder is created for each assembly for which serialization code has been generated.

The generator writes all informational and debugging logs inside the `Temp/NetcodeGenerated/sourcegenerator.log` folder. Errors and warnings are also emitted in the Editor console.

## Config files and logging

You can configure source generator behavior using a config file. Unity automatically detects the presence of AnalyzerConfig files, whether they're global (at the root of the `Assets` folder) or on a per-assembly definition level, similar to .buildrule files.

Create a `Default.globalconfig` file in the `Assets` folder to set global options for the source generator. The file should contain key/value pairs in the following format:

```
# write comments using the hash symbol
is_global=true

your_key=your value
your_key=your value
...
```

For more information about formatting global AnalyzerConfig files, refer to [Microsoft's documentation](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/configuration-files#global-analyzerconfig).

Netcode for Entities supports the following keys:

| Key                                               | Available values                            | Description                                                                                                                                                                        |
|---------------------------------------------------|----------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `unity.netcode.sourcegenerator.outputfolder`        | A valid relative string .         | Override the output folder where the generator puts logs and generated files. File path must be relative to the project path. Default is `Temp/NetCodeGenerated`.                           |
| `unity.netcode.sourcegenerator.write_files_to_disk` | Empty or 1 (enabled), or 0 (disabled). | Set whether to write generated files to disk.                                                                                                                                      |
| `unity.netcode.sourcegenerator.write_logs_to_disk`  | Empty or 1 (enabled), or 0 (disabled). | Set whether to write logs to disk. All logs are redirected to the Editor logs if disabled.                                                                       |
| `unity.netcode.sourcegenerator.emit_timing`         | Empty or 1 (enabled), or 0 (disabled). | Set whether to log timing information for each compiled assembly.                                                                                                                               |
| `unity.netcode.sourcegenerator.logging_level`       | `Info`, `warning`, or `error`.             | Set the logging level. Default is `error`.                                                                                                                                |
| `unity.netcode.sourcegenerator.attach_debugger`     | An optional assembly name.        | Stop the generator execution and wait for a debugger to be attached. If the assembly name is non-empty, the generator waits for the debugger only when the assembly is being processed. |

## Build the source generator

If you need to recompile the source generator (to fix an issue or extend it, for example) you can do so manually outside of Unity using the [.NET SDK 6.0 or higher](https://dotnet.microsoft.com/en-us/download/dotnet/6.0).

Use the following command-line commands from within the `Packages\com.unity.netcode\Runtime\SourceGenerators\Source~` directory:

* To compile a release build: `dotnet publish -c Release`
* To compile a debug build: `dotnet publish -c Debug`

The source generator can also be built and debugged using the provided `Packages/com.unity.netcode/Runtime/SourceGenerators/Source~/SourceGenerators.sln` solution.

## Debug source generator problems

Source generator execution is invoked by an external process and you need to attach a debugger to step through the code and debug. To begin, open the `SourceGenerators.sln` in either Rider or VisualStudio and recompile the generator using the [debug configuration](#build-the-source-generator).

To simplify the process of attaching the debugger when the source generator is invoked, Netcode for Entities provides some utilities that let you attach the debugger to the running process in a controllable manner.

### Use the global config

Add the `unity.netcode.sourcegenerator.attach_debugger` option to the config file and the source generator will wait for the debugger to be attached, either for the entire invocation or for a specific assembly (if you specify one).

### Modify the generator code

You can use the `Debug.LaunchDebugger` helper method to launch the debugger at any point during source generation. It's recommended to call it from within `NetcodeSourceGenerator.cs`, inside the `Execute` method.

```csharp
// Launch the debugger unconditionally
Debug.LaunchDebugger()
// Launch the debugger if the current processed assembly matches the name
Debug.LaunchDebugger(GeneratorExecutionContext context, string assembly)
```

```csharp
public void Execute(GeneratorExecutionContext executionContext)
{
    ....
    Debug.LaunchDebugger();
    try
    {
        Generate(executionContext, diagnostic);
    }
    catch (Exception e)
    {
       ...
    }
```

Because the `Execute` method is invoked multiple times (once per assembly), you will get multiple debugger pop ups if you're not using the assembly filter.

In all cases, a dialog box will open at the right time, stating which process ID you should attach to.
