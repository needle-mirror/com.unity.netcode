using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.NetCode.Roslyn;

namespace Unity.NetCode.Generators
{
    // The RemotesSerializer instances are created by CodeGenerator. The class itself is not threadsafe,
    // but since every SourceGenerator has its own Context it is safe use.
    // Please avoid to use shared static variables or state here and verify that in case you need, they are immutable or thread safe.
    internal class RemotesCodeGen
    {
        private GhostCodeGen m_CommandGenerator;

        public RemotesCodeGen(CodeGenerator.Context context)
        {
            var generator = context.codeGenCache.GetTemplate(CodeGenerator.RemoteSynchronization);
            m_CommandGenerator = generator.Clone();
        }

        public void Generate(CodeGenerator.Context context, TypeInformation typeInfo) 
        {
            var replacements = new Dictionary<string, string>
            {
                // REMOTES_NAME shouldn't contain namespace
                {"REMOTES_NAME", context.RemotesContext.BackingRemoteComponentName.Replace(".", "").Replace('+', '_')},
                // {"REMOTES_NAME", typeInfo.TypeFullName.Replace(".", "").Replace('+', '_')},
                {"REMOTES_NAMESPACE", typeInfo.Namespace == "" ? "global" : typeInfo.Namespace}
            };

            // Make the namespace block
            if ( typeInfo.Namespace != "" )
            {
                m_CommandGenerator.GenerateFragment("HAS_REMOTES_NAMESPACE_START", replacements);
                m_CommandGenerator.GenerateFragment("HAS_REMOTES_NAMESPACE_END", replacements);   
            }


            if (!string.IsNullOrEmpty(typeInfo.Namespace))
                context.imports.Add(typeInfo.Namespace);

            foreach (var import in context.imports)
            {
                replacements["REMOTES_USING"] = CodeGenerator.GetValidNamespaceForType(context.generatedNs, import);
                m_CommandGenerator.GenerateFragment("REMOTES_USING_STATEMENT", replacements);
            }

            // Check to see if we have a handle function
            if ( typeInfo.IsAutoInvokeRemote )
            {
                replacements["REMOTES_HANDLER_ID"] = ComputeRemoteHash( context ).ToString();
                replacements["REMOTES_AUTO_INVOKE_HANDLE_ARGS"] = typeInfo.IsGhostBehaviourMethod ? "em.World" : "";
                m_CommandGenerator.GenerateFragment("REMOTES_AUTO_INVOKE", replacements);
            }

            // Check to see if we are a Remote method
            if ( typeInfo.MethodSymbol != null )
            {
                List<string> callArguments = new List<string>();
                List<string> methodArguments = new List<string>();
                List<string> constructorArguments = new List<string>();

                foreach (var type in typeInfo.GhostFields)
                {
                    replacements["MEMBER_NAME"] = $"{type.FieldName}";
                    replacements["MEMBER_TYPE"] = type.FieldTypeName;
                    m_CommandGenerator.GenerateFragment("REMOTES_REMOTE_METHOD_ARGS_AS_MEMBERS", replacements);


                    bool isParamTargetGhostData = typeInfo.IsGhostBehaviourMethod && ( type.FieldName == GetTargetGhostIdMemberName() || type.FieldName == GetTargetSpawnTickMemberName() );

                    if (!isParamTargetGhostData)
                    {
                        callArguments.Add($"{type.FieldName}");
                        methodArguments.Add($"{type.FieldTypeName} _{type.FieldName}");

                        constructorArguments.Add($"{type.FieldName} = _{type.FieldName}");
                    }
                    else
                    {
                        // This needs serious consideration to fix up properly
                        if (type.FieldName == GetTargetGhostIdMemberName())
                        {
                            constructorArguments.Add($"{GetTargetGhostIdMemberName()} = this.Ghost.GhostId");
                        }
                        else
                        {
                            constructorArguments.Add($"{GetTargetSpawnTickMemberName()} = this.Ghost.SpawnTick.SerializedData");
                        }
                    }
                }

                replacements["REMOTES_REMOTE_METHOD_STATIC"] = typeInfo.MethodSymbol.IsStatic ? "static" : "";

                // pass the member name to the handle function
                replacements["REMOTES_REMOTE_METHOD_NAME"] = typeInfo.MethodSymbol.Name;
                replacements["REMOTES_REMOTE_METHOD_INVOKE_CALL"] = $"{typeInfo.MethodSymbol.ReceiverType.GetFullTypeName()}.{typeInfo.MethodSymbol.Name}";

                replacements["REMOTES_REMOTE_METHOD_CALL_ARGS"] = string.Join(",", callArguments);

                if (typeInfo.IsGhostBehaviourMethod)
                {

                    replacements["REMOTES_REMOTE_GHOST_METHOD_CLASS_NAME"] = typeInfo.MethodSymbol.ReceiverType.GetFullTypeName();
                    replacements["REMOTES_REMOTE_GHOST_TARGET_ID_NAME"] = GetTargetGhostIdMemberName();
                    replacements["REMOTES_REMOTE_GHOST_SPAWN_TICK_NAME"] = GetTargetSpawnTickMemberName();

                    m_CommandGenerator.GenerateFragment("REMOTES_REMOTE_GHOST_METHOD_HANDLE_METHOD", replacements);
                }
                else
                {
                    m_CommandGenerator.GenerateFragment("REMOTES_REMOTE_STATIC_METHOD_HANDLE_METHOD", replacements);
                }

                // Make the invoke function
                replacements["REMOTES_REMOTE_METHOD_ARGS"] = string.Join(",", methodArguments);

                replacements["REMOTES_REMOTE_METHOD_DIRECTION_ARGS"] = "";

                // TODO: there is more we can do here, we never need to generate the system to process this remote on the server/client if its not needed
                // this is not an optimization it will also make sure no one can call the function remotely by packet messing since the system to process it simply won't exist
                var remotesAttribute = Roslyn.Extensions.GetAttribute(typeInfo.MethodSymbol, "Unity.NetCode", "RemoteAttribute");
                if ( remotesAttribute != null )
                {
                    foreach ( var ca in remotesAttribute.ConstructorArguments )
                    {
                        if (Roslyn.Extensions.GetFullTypeName(ca.Type) == "Unity.NetCode.Directionality")
                        {
                            switch (ca.Value)
                            {
                                case 0: // Undefined
                                    replacements["REMOTES_REMOTE_METHOD_DIRECTION_ERROR"] = "Debug.LogError( \"Remote Methods must specify a Directionality.\" );";
                                    break;
                                case 1: // ClientToServer
                                    replacements["REMOTES_REMOTE_METHOD_DIRECTION_ARGS"] = ", ClientServerBootstrap.ClientWorlds, Directionality.ClientToServer";
                                    replacements["REMOTES_REMOTE_METHOD_DIRECTION_ERROR"] = typeInfo.IsGhostBehaviourMethod
                                        ? "if (!IsClient) Debug.LogError( \"Can't Invoke a ClientToServer Remote on a Server GhostBehaviour.\" );"
                                        : "if (!Netcode.IsClientRole) Debug.LogError( \"Can't Invoke a static ClientToServer Remote on a Server instance.\" );";
                                    break;
                                case 2: // ServerToClient
                                    replacements["REMOTES_REMOTE_METHOD_DIRECTION_ARGS"] = ", ClientServerBootstrap.ServerWorlds, Directionality.ServerToClient";
                                    replacements["REMOTES_REMOTE_METHOD_DIRECTION_ERROR"] = typeInfo.IsGhostBehaviourMethod
                                        ? "if (!IsServer) Debug.LogError( \"Can't Invoke a ServerToClient Remote on a Client GhostBehaviour.\" );"
                                        : "if (!Netcode.IsServerRole) Debug.LogError( \"Can't Invoke a static ServerToClient Remote on a Client instance.\" );";
                                    break;
                            }
                        }
                    }
                }

                replacements["REMOTES_REMOTE_METHOD_CONSTRUICTOR_ARGS"] = string.Join(",", constructorArguments);

                string containingTypeAsString = "";
                var containingType = typeInfo.MethodSymbol.ContainingType;

                switch (containingType.DeclaredAccessibility)
                {
                    case Microsoft.CodeAnalysis.Accessibility.Public:
                        containingTypeAsString += "public";
                        break;
                };

                containingTypeAsString += " partial";

                switch ( containingType.TypeKind )
                {
                    case Microsoft.CodeAnalysis.TypeKind.Class:
                        containingTypeAsString += " class";
                        break;
                    case Microsoft.CodeAnalysis.TypeKind.Struct:
                        containingTypeAsString += " struct";
                        break;
                }

                containingTypeAsString += $" {containingType.Name}";

                replacements["REMOTES_METHOD_CONTAINER_TYPE_DECLARATION"] = containingTypeAsString;

                m_CommandGenerator.GenerateFragment("REMOTES_REMOTE_METHOD", replacements);
            }

            var serializerName = context.generatorName + "RemoteSerializer.cs";
            m_CommandGenerator.GenerateFile(serializerName, replacements, context.batch);
        }

        public static ulong ComputeRemoteHash(CodeGenerator.Context context)
        {
            var hash = Utilities.TypeHash.FNV1A64("NetCode.IRemote");
            hash = Utilities.TypeHash.CombineFNV1A64(hash, Utilities.TypeHash.FNV1A64(context.generatorName));
            return hash;
        }

        public override string ToString()
        {
            var debugInformation = m_CommandGenerator?.ToString();
            return debugInformation;
        }

        public static string GetTargetGhostIdMemberName()
        {
            return "_targetGhostId";
        }

        public static string GetTargetSpawnTickMemberName()
        {
            return $"{GetTargetGhostIdMemberName()}_SpawnTick";
        }

        public static string GetGhostBehaviourFullTypeName()
        {
            return "Unity.NetCode.GhostBehaviour";
        }
    }
}
