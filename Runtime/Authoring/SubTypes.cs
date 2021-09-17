namespace Unity.NetCode
{
    /// <summary>
    /// Hold a list of constant int that can be used across the project to specify
    /// subtype in <see cref="GhostFieldAttribute"/>.
    /// User can expand that list by using an AssemblyDefinitionReference to Unity.NetCode.Gen and adding a partial
    /// class that extend and add new constant literals to that class.
    /// </summary>
    /// <remarks>
    /// Why GhostFieldSubType is not an enum: The reason is that there are unfortunately caveats, some due
    /// to our compilation pipeline and others due to the limitation of the SourceGenerator api.
    /// First: MS SourceGenerator are additive only. That means we cannot modify the syntaxtree, removing or adding nodes
    /// to it (not the way Analyzers does).
    /// To overcome that limitation, a possible solution to inject the enums literals into the assembly is to use a small
    /// IL post processor instead. Because NetCode rutime asssembly is re-imported every time a sub-type is added or removed,
    /// the assumption was that the IL post-processing will then correctly modify the the dll before any dependent dll is compiled.
    /// Although it does, and Unity.NetCode.dll contains the correct metadata, the ILPostProcessorRunner runt at a later time
    /// and some dlls are not compile correctly (depend on timing). With further investigation it might be possible to address
    /// that problem, however seems like fighting against the compilation process again, **something we wanted to avoid**.
    /// Because all of that, a partial class to hold the integral constants is used instead and esers can add new const literals.
    ///
    /// Why the AssemblyDefinitionReference? Using source generator to add a partial class diretly to NetCode.dll works fine
    /// but unfortunately will miss the IDE auto-completion functionality. No IDE at the moment provide a support for that
    /// out of the box. VS has some workaround for normal C# projects (removing the original file from the sulution etc) or
    /// by restarting the IDE, but Rider or VSCode does not work the same way. By using the Assembly Definition Reference, we
    /// are actually doing in principle the same job and completion works, making the user experience a little more pleasant.
    /// </remarks>
    static public partial class GhostFieldSubType
    {
        public const int None = 0;
    }
}
