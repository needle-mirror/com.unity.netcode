using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace Unity.NetCode.Remotes.CodeGen
{
    internal class RemoteMethodPP : ILPostProcessor
    {
        public class CustomResolver : BaseAssemblyResolver
        {

        }
        public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly)
        {
            using var resolver = new DefaultAssemblyResolver();

            var folders = new HashSet<string>();
            foreach (var reference in compiledAssembly.References)
            {
                resolver.AddSearchDirectory(Path.Combine(Environment.CurrentDirectory, Path.GetDirectoryName(reference)));
            }

            var peStream = new MemoryStream(compiledAssembly.InMemoryAssembly.PeData);
            var pdbStream = new MemoryStream(compiledAssembly.InMemoryAssembly.PdbData);
            peStream.Position = 0;
            pdbStream.Position = 0;
            var rp = new ReaderParameters()
            {
                AssemblyResolver = resolver,
                ReadSymbols = true,
                SymbolStream = pdbStream,
                SymbolReaderProvider = new PortablePdbReaderProvider()
            };
            using var assemblyDefinition = AssemblyDefinition.ReadAssembly(peStream, rp);

            bool swaps = false;

            foreach ( var t in assemblyDefinition.MainModule.GetAllTypes() )
            {
                // TODO: we can do extra filtering here we can check the remote inherits from IRemote for a struct and a class inherits from Ghostbehaviour for now lets just filter on the attribute
                if ( t.HasMethods )
                {
                    foreach ( var userMethod in t.Methods )
                    {
                        foreach (var ca in userMethod.CustomAttributes)
                        {
                            if (ca.AttributeType.Name == "RemoteAttribute")
                            {
                                // So we also want to find the matching function, we need to get that to generate in the correct place
                                foreach (var methodInvoker in t.Methods)
                                {
                                    if ( methodInvoker.Name == $"{userMethod.Name}_Invoker" )
                                    {
                                        // We just need to swap the body!
                                        var userBody = userMethod.Body;
                                        userMethod.Body = methodInvoker.Body;
                                        methodInvoker.Body = userBody;

                                        var userDebug = userMethod.DebugInformation;
                                        userMethod.DebugInformation = methodInvoker.DebugInformation;
                                        methodInvoker.DebugInformation = userDebug;

                                        swaps = true;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if ( swaps )
            {
                var pe = new MemoryStream();
                var pdb = new MemoryStream();
                var writerParameters = new WriterParameters
                {
                    SymbolWriterProvider = new PortablePdbWriterProvider(),
                    SymbolStream = pdb,
                    WriteSymbols = true
                };
                var diagnostics = new List<DiagnosticMessage>();
                assemblyDefinition.Write(pe, writerParameters);
                peStream.Flush();
                pdb.Flush();
                return new ILPostProcessResult(new InMemoryAssembly(pe.ToArray(), pdb.ToArray()), diagnostics);
            }
            return new ILPostProcessResult(null);
        }

        public override ILPostProcessor GetInstance()
        {
            return this;
        }

        public override bool WillProcess(ICompiledAssembly compiledAssembly)
        {
            foreach ( var r in compiledAssembly.References )
            {
                if (Path.GetFileNameWithoutExtension(r) == "Unity.NetCode")
                {
                    return true;
                }
            }

            return false;
        }
    }
}
