using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using Unity.NetCode.Generators;

namespace Unity.NetCode.GeneratorTests
{
    [TestFixture]
    class GlobalQualifiedTypeNameTests : BaseTest
    {
        [Test]
        public void GetGlobalQualifiedTypeName_ArraySuffix_QualifiesElementTypeViaSymbol()
        {
            var testData = @"
namespace Unity.NetCode.Test
{
    public struct Elem
    {
        public int v;
    }

    public struct Holder
    {
        public Unity.NetCode.Test.Elem[] Items;
    }
}";
            var compilation = GeneratorTestHelpers.CreateCompilation(CSharpSyntaxTree.ParseText(testData));
            var holder = compilation.GetSymbolsWithName("Holder").OfType<INamedTypeSymbol>().First();
            var itemsField = holder.GetMembers("Items").OfType<IFieldSymbol>().First();

            var qualified = Roslyn.Extensions.GetGlobalQualifiedTypeName(itemsField.Type);

            Assert.That(qualified, Does.Contain("global::Unity.NetCode.Test.Elem"));
            Assert.That(qualified, Does.EndWith("[]"));
            Assert.That(qualified, Does.Not.StartWith("Unity.NetCode.Test.Elem[]"));
        }

        [Test]
        public void GetGlobalQualifiedTypeName_ConstructedGenericWithNestedSuffix_QualifiesTypeArguments()
        {
            var testData = @"
namespace Unity.NetCode.Test
{
    public struct Payload
    {
        public int v;
    }

    public struct GenericOuter<T> where T : struct
    {
        public struct Inner
        {
            public T value;
        }
    }
}";
            var compilation = GeneratorTestHelpers.CreateCompilation(CSharpSyntaxTree.ParseText(testData));
            var payload = compilation.GetSymbolsWithName("Payload").OfType<INamedTypeSymbol>().First();
            var genericOuter = compilation.GetSymbolsWithName("GenericOuter").OfType<INamedTypeSymbol>().First();
            var constructed = genericOuter.Construct(payload);
            var inner = constructed.GetTypeMembers("Inner").First();

            var qualified = Roslyn.Extensions.GetGlobalQualifiedTypeName(inner);

            Assert.That(qualified, Does.Contain("global::Unity.NetCode.Test.Payload"));
            Assert.That(qualified, Does.Contain("Inner"));
        }

        [Test]
        public void SourceGenerator_GhostComponentSerializer_UsesSymbolForGlobalQualifiedComponentType()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Collections;

            namespace Unity.NetCode.Test
            {
                public struct Item : IComponentData
                {
                    [GhostField] public int v;
                }

                public struct Container : IComponentData
                {
                    [GhostField] public FixedList32Bytes<Item> items;
                }
            }

            namespace Unity.NetCode.Test.Unity.NetCode.Test
            {
            }";

            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { Receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);

            var results = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(0, results.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));

            var serializerSource = results.GeneratedSources
                .FirstOrDefault(s => s.SyntaxTree.GetText().ToString().Contains("ContainerGhostComponentSerializer"));
            Assert.IsNotNull(serializerSource, "Expected ghost serializer for Container");

            var serializerText = serializerSource!.SyntaxTree.GetText().ToString();
            Assert.That(serializerText, Does.Contain("global::Unity.NetCode.Test.Container"),
                "GHOST_COMPONENT_TYPE should be emitted via ITypeSymbol fully-qualified formatting");
            Assert.AreEqual(0, serializerSource.SyntaxTree.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error));
        }

        [Test]
        public void SourceGenerator_Variant_UsesAdapteeTypeForComponentTypeNotVariantStruct()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;
            using Unity.Transforms;
            namespace Unity.NetCode
            {
                [GhostComponentVariation(typeof(Transforms.LocalTransform))]
                [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.All)]
                public struct VariantTest
                {
                    [GhostField(Smoothing=SmoothingAction.Interpolate)] public float3 Position;
                }
            }";

            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { Receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(1, walker.Receiver.Variants.Count);

            var results = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(0, results.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));

            var outputTree = results.GeneratedSources[0].SyntaxTree;
            var initBlockWalker = new InializationBlockWalker();
            outputTree.GetCompilationUnitRoot().Accept(initBlockWalker);
            Assert.IsNotNull(initBlockWalker.Intializer);
            var componentTypeAssignment = initBlockWalker.Intializer!.Expressions
                .First(e => ((AssignmentExpressionSyntax)e).Left.ToString() == "ComponentType");
            Assert.That(componentTypeAssignment.ToString(), Does.Contain("Unity.Transforms.LocalTransform"));
            Assert.That(componentTypeAssignment.ToString(), Does.Not.Contain("VariantTest"));
        }

        [Test]
        public void SourceGenerator_InputComponentData_NestedInCollidingNamespace_UsesGlobalQualifiedNames()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;

            namespace Unity.NetCode.Test
            {
                internal class Host
                {
                    internal struct NestedInput : IInputComponentData
                    {
                        public float value;
                    }
                }
            }

            namespace Unity.NetCode.Test.Unity.NetCode.Test
            {
            }";

            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { Receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(1, walker.Receiver.Candidates.Count);

            var results = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(0, results.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));

            var registrationText = results.GeneratedSources
                .First(s => s.HintName.Contains("GhostComponentSerializerCollection"))
                .SyntaxTree.GetText().ToString();

            Assert.That(registrationText, Does.Contain("global::Unity.NetCode.Test.Host.NestedInput"));
            Assert.That(registrationText, Does.Not.Match(@"ComponentType\.ReadWrite<Unity\.NetCode\.Test\.Host\.NestedInput>"));
        }
    }
}
