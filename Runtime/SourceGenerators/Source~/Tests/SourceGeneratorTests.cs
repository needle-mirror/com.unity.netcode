using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Unity.NetCode.Generators;
using System.IO;
using System.Linq;

namespace Unity.NetCode.GeneratorTests
{
    [TestFixture]
    class SourceGeneratorTests : BaseTest
    {
        [Test]
        public void InnerNamespacesAreHandledCorrectly()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;

            namespace N1
            {
                public struct T1{}
                namespace N2
                {
                    public struct T2{}
                }
            }
            namespace N1.N2.N3
            {
                public struct T3
                {
                }
            }";

            var tree = CSharpSyntaxTree.ParseText(testData);
            var compilation = GeneratorTestHelpers.CreateCompilation(tree);
            var model = compilation.GetSymbolsWithName("T1").FirstOrDefault();
            Assert.IsNotNull(model);
            Assert.AreEqual("N1", Roslyn.Extensions.GetFullyQualifiedNamespace(model));
            Assert.AreEqual("N1.T1", Roslyn.Extensions.GetFullTypeName(model));
            model = compilation.GetSymbolsWithName("T2").FirstOrDefault();
            Assert.IsNotNull(model);
            Assert.AreEqual("N1.N2", Roslyn.Extensions.GetFullyQualifiedNamespace(model));
            Assert.AreEqual("N1.N2.T2", Roslyn.Extensions.GetFullTypeName(model));
            model = compilation.GetSymbolsWithName("T3").FirstOrDefault();
            Assert.IsNotNull(model);
            Assert.AreEqual("N1.N2.N3", Roslyn.Extensions.GetFullyQualifiedNamespace(model));
            Assert.AreEqual("N1.N2.N3.T3", Roslyn.Extensions.GetFullTypeName(model));
        }

        [Test]
        public void DeclaringTypePrependTypeName()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;

            public struct Outer
            {
                public struct Inner
                {
                }
            }

            namespace T1.T2.T3
            {
                public struct Outer
                {
                    public struct InnerWithNS
                    {
                    }
                }
            }";

            var tree = CSharpSyntaxTree.ParseText(testData);
            var compilation = GeneratorTestHelpers.CreateCompilation(tree);
            var model = compilation.GetSymbolsWithName("Inner").FirstOrDefault();
            Assert.IsNotNull(model);
            var fullTypeName = Roslyn.Extensions.GetFullTypeName(model);
            Assert.AreEqual("Outer+Inner", fullTypeName);
            model = compilation.GetSymbolsWithName("InnerWithNS").FirstOrDefault();
            Assert.IsNotNull(model);
            fullTypeName = Roslyn.Extensions.GetFullTypeName(model);
            Assert.AreEqual("T1.T2.T3.Outer+InnerWithNS", fullTypeName);
        }

        [Test]
        public void SourceGenerator_PrimitiveTypes()
        {
            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(TestDataSource.TestComponentsData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(1, walker.receiver.Candidates.Count);
            //Check generated files match
            var resuls = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(3, resuls.GeneratedSources.Length, "Num generated files does not match");
            var outputTree = resuls.GeneratedSources[0].SyntaxTree;
            var snapshotDataSyntax = outputTree.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "Snapshot");
            Assert.IsNotNull(snapshotDataSyntax);
            var expected = new[]
            {
                //byte
                ("uint", "EnumValue8"),
                //short
                ("int", "EnumValue16"),
                //nothing (default int)
                ("int", "EnumValue32"),
                //long
                ("long", "EnumValue64"),
                ("int", "IntValue"),
                ("uint", "UIntValue"),
                ("long", "LongValue"),
                ("ulong", "ULongValue"),
                ("int", "ShortValue"),
                ("uint", "UShortValue"),
                ("int", "SByteValue"),
                ("uint", "ByteValue"),
                ("uint", "BoolValue"),
                ("float", "FloatValue"),
                ("float", "InterpolatedFloat"),
                ("int", "QuantizedFloat"),
                ("int", "InterpolatedQuantizedFloat")
            };
            var members = snapshotDataSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>().ToArray();
            Assert.AreEqual(expected.Length, members.Length);
            for (int i = 0; i < expected.Length; ++i)
            {
                Assert.AreEqual(expected[i].Item1, (members[i].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text,
                    $"{i}");
                Assert.AreEqual(expected[i].Item2, members[i].Declaration.Variables[0].Identifier.Text);
            }
        }

        [Test]
        public void SourceGenerator_CompositeTypes()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;
            public struct Compo1
            {
            public int x;
            public int y;
            }
            public struct Compo2
            {
            public float x;
            public float y;
            }
            public struct MyTest : IComponentData
            {
            [GhostField(Composite=true)] public Compo1 compo1;
            [GhostField(Composite=true)] public Compo2 compo2;
            }
            ";
            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(1, walker.receiver.Candidates.Count);

            var resuls = GeneratorTestHelpers.RunGenerators(tree);
            var outputTree = resuls.GeneratedSources[0].SyntaxTree;
            var snapshotDataSyntax = outputTree.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "Snapshot");
            Assert.IsNotNull(snapshotDataSyntax);
            var expected = new[]
            {
                ("int", "compo1_x"),
                ("int", "compo1_y"),
                ("float", "compo2_x"),
                ("float", "compo2_y"),
            };
            var maskBits = outputTree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(t => t.Declaration.Variables[0].Identifier.ValueText == "ChangeMaskBits");
            Assert.IsNotNull(maskBits);
            Assert.IsNotNull(maskBits.Declaration.Variables[0].Initializer);
            Assert.AreEqual("2", maskBits.Declaration.Variables[0].Initializer.Value.ToString());
            var members = snapshotDataSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>().ToArray();
            Assert.AreEqual(expected.Length, members.Length);
            for (int i = 0; i < expected.Length; ++i)
            {
                Assert.AreEqual(expected[i].Item1, (members[i].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text,
                    $"{i}");
                Assert.AreEqual(expected[i].Item2, members[i].Declaration.Variables[0].Identifier.Text);
            }
        }

        [Test]
        public void SourceGenerator_Mathematics()
        {
            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(TestDataSource.MathematicsTestData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(1, walker.receiver.Candidates.Count);

            //Check generated files match
            var resuls = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(3, resuls.GeneratedSources.Length, "Num generated files does not match");
            var outputTree = resuls.GeneratedSources[0].SyntaxTree;
            var snapshotDataSyntax = outputTree.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "Snapshot");
            Assert.IsNotNull(snapshotDataSyntax);
            var members = snapshotDataSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>().ToArray();
            //Each block generate 13 variables
            var numVariablePerBlock = 13;
            Assert.AreEqual(4 * numVariablePerBlock, members.Length);
            for (int i = 0; i < 2 * numVariablePerBlock; ++i)
            {
                Assert.AreEqual("float", (members[i].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text);
            }

            for (int i = 2 * numVariablePerBlock; i < 4 * numVariablePerBlock; ++i)
            {
                Assert.AreEqual("int", (members[i].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text);
            }

            var prefixes = new[] { "", "i", "q", "iq" };
            for (int i = 0, k = 0; i < 4; ++i, k += numVariablePerBlock)
            {
                Assert.AreEqual(prefixes[i] + "Float2Value_x", members[k + 0].Declaration.Variables[0].Identifier.Text);
                Assert.AreEqual(prefixes[i] + "Float2Value_y", members[k + 1].Declaration.Variables[0].Identifier.Text);
                Assert.AreEqual(prefixes[i] + "Float3Value_x", members[k + 2].Declaration.Variables[0].Identifier.Text);
                Assert.AreEqual(prefixes[i] + "Float3Value_y", members[k + 3].Declaration.Variables[0].Identifier.Text);
                Assert.AreEqual(prefixes[i] + "Float3Value_z", members[k + 4].Declaration.Variables[0].Identifier.Text);
                Assert.AreEqual(prefixes[i] + "Float4Value_x", members[k + 5].Declaration.Variables[0].Identifier.Text);
                Assert.AreEqual(prefixes[i] + "Float4Value_y", members[k + 6].Declaration.Variables[0].Identifier.Text);
                Assert.AreEqual(prefixes[i] + "Float4Value_z", members[k + 7].Declaration.Variables[0].Identifier.Text);
                Assert.AreEqual(prefixes[i] + "Float4Value_w", members[k + 8].Declaration.Variables[0].Identifier.Text);
                Assert.AreEqual(prefixes[i] + "QuaternionValueX",
                    members[k + 9].Declaration.Variables[0].Identifier.Text);
                Assert.AreEqual(prefixes[i] + "QuaternionValueY",
                    members[k + 10].Declaration.Variables[0].Identifier.Text);
                Assert.AreEqual(prefixes[i] + "QuaternionValueZ",
                    members[k + 11].Declaration.Variables[0].Identifier.Text);
                Assert.AreEqual(prefixes[i] + "QuaternionValueW",
                    members[k + 12].Declaration.Variables[0].Identifier.Text);
            }
        }

        [Test]
        public void SourceGenerator_GenerateCorrectFiles()
        {
            const string testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;

            public struct TestComponent : IComponentData
            {
                [GhostField] public int x;
            }
            ";
            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(1, walker.receiver.Candidates.Count);
            //Make a full pass: generate the code and write files to disk
            GeneratorTestHelpers.RunGeneratorsWithOptions(new Dictionary<string, string> { {GlobalOptions.WriteFilesToDisk, "1"}}, tree);
            Assert.IsTrue(File.Exists($"{GeneratorTestHelpers.OutputFolder}/{GeneratorTestHelpers.GeneratedAssemblyName}/TestComponentSerializer.cs"));
            Assert.IsTrue(File.Exists($"{GeneratorTestHelpers.OutputFolder}/{GeneratorTestHelpers.GeneratedAssemblyName}/GhostComponentSerializerCollection.cs"));
        }

        [Test]
        public void SourceGenerator_NestedTypes()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;
            public struct MyTest
            {
                public struct MyStruct
                {
                    public float x;
                    public float y;
                }
                public struct Nested
                {
                    public float2 f;
                }
                public struct InnerComponent : IComponentData
                {
                    [GhostField] public float x;
                    [GhostField] public float y;
                    [GhostField] public Nested n;
                    [GhostField(Composite=true)] public Nested m;
                }
            }";
            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(1, walker.receiver.Candidates.Count);

            var resuls = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(3, resuls.GeneratedSources.Length, "Num generated files does not match");
            var outputTree = resuls.GeneratedSources[0].SyntaxTree;
            var snapshotDataSyntax = outputTree.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "Snapshot");
            Assert.IsNotNull(snapshotDataSyntax);
            var expected = new[]
            {
                ("float", "x"),
                ("float", "y"),
                ("float", "n_f_x"),
                ("float", "n_f_y"),
                ("float", "m_f_x"),
                ("float", "m_f_y"),
            };
            var maskBits = outputTree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(t => t.Declaration.Variables[0].Identifier.ValueText == "ChangeMaskBits");
            Assert.IsNotNull(maskBits);
            Assert.IsNotNull(maskBits.Declaration.Variables[0].Initializer);
            Assert.AreEqual("5", maskBits.Declaration.Variables[0].Initializer.Value.ToString());
            var members = snapshotDataSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>().ToArray();
            Assert.AreEqual(expected.Length, members.Length);
            for (int i = 0; i < expected.Length; ++i)
            {
                Assert.AreEqual(expected[i].Item1, (members[i].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text,
                    $"{i}");
                Assert.AreEqual(expected[i].Item2, members[i].Declaration.Variables[0].Identifier.Text);
            }
        }

        [Test]
        public void SourceGenerator_FlatType()
        {
            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(TestDataSource.FlatTypeTest);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(1, walker.receiver.Candidates.Count);

            var results = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(0, results.Diagnostics.Count(d=>d.Severity == DiagnosticSeverity.Error));
            Assert.AreEqual(3, results.GeneratedSources.Length, "Num generated files does not match");
        }

        [Test]
        public void SourceGenerator_Recurse()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;
            using Unity.Transforms;
            namespace Unity.NetCode
            {
                public struct TestRecurse : IComponentData
                {
                    public int x;
                    public int this[int index]
                    {
                        get { return this.x; }
                        set { x = value; }
                    }
                    public TestRecurse DontSerialize { get { return new TestRecurse();} set {}}
                }

                public struct ProblematicType : IComponentData
                {
                    [GhostField] public TestRecurse MyType;
                }
            }";
            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(2, walker.receiver.Candidates.Count);

            var resuls = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(0, resuls.Diagnostics.Count(m=>m.Severity == DiagnosticSeverity.Error));
            Assert.AreEqual(3, resuls.GeneratedSources.Length, "Num generated files does not match");
        }

        [Test]
        public void SourceGenerator_TransformsVariants()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;
            using Unity.Transforms;
            namespace Unity.NetCode
            {
                [GhostComponentVariation(typeof(Transforms.Translation))]
                [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.All)]
                public struct TranslationVariant
                {
                    [GhostField(Composite=true,Smoothing=SmoothingAction.Interpolate)] public float3 Value;
                }

                //This in invalid and should report an error
                [GhostComponentVariation(typeof(Transforms.Rotation))]
                public struct InvalidRotation
                {
                    [GhostField] public float3 Value;
                }

                [GhostComponentVariation(typeof(Transforms.Rotation))]
                [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.All)]
                public struct RotationVariant
                {
                    [GhostField(Composite=true,Quantization=100, Smoothing=SmoothingAction.Interpolate)] public quaternion Value;
                }
            }";

            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            //All the variants are detected as candidates
            Assert.AreEqual(3, walker.receiver.Variants.Count);

            var resuls = GeneratorTestHelpers.RunGenerators(tree);
            var diagnostics = resuls.Diagnostics;
            //Expect to see one error
            Assert.AreEqual(1, diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error), "errorCount");
            Assert.AreEqual("InvalidRotation: Cannot find member Value type: float3 in Rotation",
                diagnostics.First(d => d.Severity == DiagnosticSeverity.Error).GetMessage());
            Assert.AreEqual(4, resuls.GeneratedSources.Length, "Num generated files does not match");

            var outputTree = resuls.GeneratedSources[0].SyntaxTree;
            var snapshotDataSyntax = outputTree.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "Snapshot");
            Assert.IsNotNull(snapshotDataSyntax);
            //Quantizatio not used
            var expected = new[]
            {
                ("float", "Value_x"),
                ("float", "Value_y"),
                ("float", "Value_z"),
            };
            var maskBits = outputTree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(t => t.Declaration.Variables[0].Identifier.ValueText == "ChangeMaskBits");
            Assert.IsNotNull(maskBits);
            Assert.IsNotNull(maskBits.Declaration.Variables[0].Initializer);
            Assert.AreEqual("1", maskBits.Declaration.Variables[0].Initializer.Value.ToString());
            var members = snapshotDataSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>().ToArray();
            Assert.AreEqual(expected.Length, members.Length);
            for (int i = 0; i < expected.Length; ++i)
            {
                Assert.AreEqual(expected[i].Item1, (members[i].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text,
                    $"{i}");
                Assert.AreEqual(expected[i].Item2, members[i].Declaration.Variables[0].Identifier.Text);
            }

            expected = new[]
            {
                ("int", "ValueX"),
                ("int", "ValueY"),
                ("int", "ValueZ"),
                ("int", "ValueW"),
            };
            outputTree = resuls.GeneratedSources[1].SyntaxTree;
            snapshotDataSyntax = outputTree.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "Snapshot");
            Assert.IsNotNull(snapshotDataSyntax);

            maskBits = outputTree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(t => t.Declaration.Variables[0].Identifier.ValueText == "ChangeMaskBits");
            Assert.IsNotNull(maskBits);
            Assert.IsNotNull(maskBits.Declaration.Variables[0].Initializer);
            Assert.AreEqual("1", maskBits.Declaration.Variables[0].Initializer.Value.ToString());
            members = snapshotDataSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>().ToArray();
            Assert.AreEqual(expected.Length, members.Length);
            for (int i = 0; i < expected.Length; ++i)
            {
                Assert.AreEqual(expected[i].Item1, (members[i].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text,
                    $"{i}");
                Assert.AreEqual(expected[i].Item2, members[i].Declaration.Variables[0].Identifier.Text);
            }
        }

        [Test]
        public void SourceGenerator_VariantUseCorrectClassTypeAndHash()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;
            using Unity.Transforms;
            namespace Unity.NetCode
            {
                [GhostComponentVariationAttribute(typeof(Transforms.Translation))]
                [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.All)]
                public struct VariantTest
                {
                    [GhostField(Smoothing=SmoothingAction.Interpolate)] public float3 Value;
                }

                //This in invalid and should report an error (type not present in the base class)
                [GhostComponentVariation(typeof(Transforms.Rotation))]
                public struct InvalidVariant
                {
                    [GhostField] public float3 Value;
                }
            }";

            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            //All the variants are detected as candidates
            Assert.AreEqual(2, walker.receiver.Variants.Count);
            var results = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(3, results.GeneratedSources.Length, "Num generated files does not match");
            var diagnostics = results.Diagnostics;
            //Expect to see one error
            Assert.AreEqual(1, diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));
            Assert.AreEqual("InvalidVariant: Cannot find member Value type: float3 in Rotation",
                diagnostics.First(d => d.Severity == DiagnosticSeverity.Error).GetMessage());
            //Parse the output and check for the class name match what we expect
            var outputTree = results.GeneratedSources[0].SyntaxTree;
            var initBlockWalker = new InializationBlockWalker();
            outputTree.GetCompilationUnitRoot().Accept(initBlockWalker);
            Assert.IsNotNull(initBlockWalker.intializer);
            var componentTypeAssignment = initBlockWalker.intializer.Expressions
                .FirstOrDefault(e => ((AssignmentExpressionSyntax)e).Left.ToString() == "ComponentType");
            Assert.IsNotNull(componentTypeAssignment);
            Assert.IsTrue(componentTypeAssignment.ToString().Contains("Unity.Transforms.Translation"),
                componentTypeAssignment.ToString());
            var variantHashField = initBlockWalker.intializer.Expressions
                .FirstOrDefault(e => ((AssignmentExpressionSyntax)e).Left.ToString() == "VariantHash");
            Assert.IsNotNull(variantHashField);
            Assert.IsTrue(variantHashField.IsKind(SyntaxKind.SimpleAssignmentExpression));
            Assert.AreNotEqual("0", ((AssignmentExpressionSyntax)variantHashField).Right.ToString());
            //Check that the GhostSerializerAttribute also is present and initialized correctly
            var serializationAttribute = outputTree.GetRoot().DescendantNodes()
                .FirstOrDefault(n =>
                    n.IsKind(SyntaxKind.Attribute) && ((AttributeSyntax)n).Name.ToString() == "GhostSerializer");
            Assert.IsNotNull(serializationAttribute);
            Assert.AreEqual(2, ((AttributeSyntax)serializationAttribute).ArgumentList?.Arguments.Count);
            Assert.AreEqual("typeof(Unity.Transforms.Translation)",
                ((AttributeSyntax)serializationAttribute).ArgumentList?.Arguments[0].ToString());
            Assert.AreNotEqual("0", ((AttributeSyntax)serializationAttribute).ArgumentList?.Arguments[1].ToString());
        }

        [Test]
        public void SourceGenerator_Command_GenerateBufferSerializer()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;

            [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.Predicted)]
            public struct CommandTest : ICommandData
            {
                [GhostField]public NetworkTick Tick {get;set;}
                [GhostField]public int Value;
            }
            ";

            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(1, walker.receiver.Candidates.Count);
            var results = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(4, results.GeneratedSources.Length, "Num generated files does not match");
            var diagnostics = results.Diagnostics;
            //Expect to see one error
            if (diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) != 0)
            {
                foreach (var d in diagnostics)
                {
                    if (d.Severity == DiagnosticSeverity.Error)
                        Console.WriteLine(d.GetMessage());
                }

                Assert.True(false, "Error found");
            }

            //Parse the output and check for the class name match what we expect
            var expected = new[] { ("int", "Value"), ("uint", "Tick") };

            var outputTree = results.GeneratedSources[0].SyntaxTree;
            var snapshotDataSyntax = outputTree.GetRoot().DescendantNodes()
                .OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "Snapshot");
            Assert.IsNotNull(snapshotDataSyntax);
            var members = snapshotDataSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>().ToArray();
            Assert.AreEqual(expected.Length, members.Length);
            for (int i = 0; i < expected.Length; ++i)
            {
                Assert.AreEqual(expected[i].Item1, (members[i].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text,
                    $"{i}");
                Assert.AreEqual(expected[i].Item2, members[i].Declaration.Variables[0].Identifier.Text);
            }
        }

        [Test]
        public void SourceGenerator_ErrorIsReportedIfBuffersDoesNotAnnotateAllFields()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;
            using Unity.Transforms;

            public struct Buffer : IBufferElementData
            {
                [GhostField] public int Value1;
                public int Value2;
                [GhostField] public int Value3;
            }
            public struct CommandData : ICommandData
            {
                public NetworkTick Tick {get;set;}
                public int Value1;
                [GhostField] public int Value2;
            }
            ";
            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(2, walker.receiver.Candidates.Count);
            var results = GeneratorTestHelpers.RunGenerators(tree);
            //only the command serializer
            Assert.AreEqual(2, results.GeneratedSources.Length, "Num generated files does not match");
            //But some errors are reported too
            var diagnostics = results.Diagnostics.Where(m=>m.Severity == DiagnosticSeverity.Error).ToArray();
            Assert.AreEqual(3, diagnostics.Length);
            Assert.True(diagnostics[0].GetMessage().StartsWith("GhostField missing on field Buffer.Value2"));
            Assert.True(diagnostics[1].GetMessage().StartsWith("GhostField missing on field CommandData.Value1"));
            Assert.True(diagnostics[2].GetMessage().StartsWith("GhostField missing on field CommandData.Tick"));
        }

        [Test]
        public void SourceGenerator_ErrorIsReported_IfStructInheritFromMultipleInterfaces()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;
            using Unity.Transforms;

            namespace Test
            {
                public struct Invalid1 : IComponentData, IRpcCommand
                {
                    public int Value1;
                }
                public struct Invalid2 : IComponentData, ICommandData
                {
                    public NetworkTick Tick {get;set}
                    public int Value1;
                }
                public struct Invalid3 : IComponentData, IBufferElementData
                {
                    public int Value1;
                }
                public struct Invalid4: IBufferElementData, ICommandData
                {
                    public NetworkTick Tick {get;set}
                    public int Value1;
                }
                public struct Invalid5 : IBufferElementData, IRpcCommand
                {
                    public int Value1;
                }
            }
            ";
            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(5, walker.receiver.Candidates.Count);
            var results = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(0, results.GeneratedSources.Length, "Num generated files does not match");
            var diagnostics = results.Diagnostics.Where(m=>m.Severity == DiagnosticSeverity.Error).ToArray();
            Assert.AreEqual(5, diagnostics.Length);
            Assert.True(diagnostics[0].GetMessage()
                .StartsWith("struct Test.Invalid1 cannot implement Component,Rpc interfaces at the same time"));
            Assert.True(diagnostics[1].GetMessage()
                .StartsWith("struct Test.Invalid2 cannot implement Component,CommandData interfaces at the same time"));
            Assert.True(diagnostics[2].GetMessage()
                .StartsWith("struct Test.Invalid3 cannot implement Component,Buffer interfaces at the same time"));
            Assert.True(diagnostics[3].GetMessage()
                .StartsWith("struct Test.Invalid4 cannot implement Buffer,CommandData interfaces at the same time"));
            Assert.True(diagnostics[4].GetMessage()
                .StartsWith("struct Test.Invalid5 cannot implement Buffer,Rpc interfaces at the same time"));
        }

        [Test]
        public void SourceGenerator_SubTypes()
        {
            var customTemplates = @"
            using Unity.NetCode;
            using System.Collections.Generic;
            namespace Unity.NetCode.Generators
            {
                internal static partial class UserDefinedTemplates
                {
                    static partial void RegisterTemplates(List<TypeRegistryEntry> templates)
                    {
                        templates.AddRange(new[]
                        {
                            new TypeRegistryEntry
                            {
                                Type = ""System.Single"",
                                Quantized = false,
                                Smoothing = SmoothingAction.Clamp
                                SupportCommand = false,
                                Composite = false,
                                SubType = 1
                                Template = $""NetCode.GhostSnapshotValueFloatUnquantized.cs""
                            },
                        });
                    }
                }
            }";
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;

            public struct MyType : IComponentData
            {
                [GhostField(SubType=1)] public float AngleType;
            }
            ";
            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(1, walker.receiver.Candidates.Count);
            //Check generated files match
            var templateTree = CSharpSyntaxTree.ParseText(customTemplates);
            var results = GeneratorTestHelpers.RunGenerators(tree, templateTree);
            Assert.AreEqual(3, results.GeneratedSources.Length, "Num generated files does not match");

            var outputTree = results.GeneratedSources[0].SyntaxTree;
            var snapshotDataSyntax = outputTree.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "Snapshot");
            Assert.IsNotNull(snapshotDataSyntax);
            var expected = new[]
            {
                ("float", "AngleType"),
            };
            var members = snapshotDataSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>().ToArray();
            Assert.AreEqual(expected.Length, members.Length);
            for (int i = 0; i < expected.Length; ++i)
            {
                Assert.AreEqual(expected[i].Item1, (members[i].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text,
                    $"{i}");
                Assert.AreEqual(expected[i].Item2, members[i].Declaration.Variables[0].Identifier.Text);
            }
        }

        [Test]
        public void SourceGenerator_GhostComponentWithNoFields()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;

            [GhostComponent]
            public struct MyData : IComponentData
            {
                public float MyField;
            }
            [GhostComponent]
            public struct MyCommand : ICommandData
            {
                public float MyField;
            }
            [GhostComponent]
            public struct MyBuffer : IBufferElementData
            {
                public float MyField;
            }
            ";

            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            var results = GeneratorTestHelpers.RunGenerators(tree);

            // No error during processing
            Assert.AreEqual(0, results.Diagnostics.Count(m => m.Severity == DiagnosticSeverity.Error));
            // No ghost snapshot serializer is generated (but does contain serializer collection with empty variants + client-to-server command serializer)
            Assert.AreEqual(3, results.GeneratedSources.Length, "Num generated files does not match");
            Assert.IsTrue(results.GeneratedSources[0].SourceText.ToString().Contains("AddEmptyVariant"));
            Assert.AreEqual(false, results.GeneratedSources[1].SyntaxTree.ToString().Contains("GhostComponentSerializer.State"));
        }

        [Test]
        public void SourceGenerator_GhostComponentWithInvalidField()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;

            [GhostComponent]
            public struct MyType : IComponentData
            {
                [GhostField] public char MyField;
            }
            ";

            var tree = CSharpSyntaxTree.ParseText(testData);
            var results = GeneratorTestHelpers.RunGenerators(tree);
            // foreach (var msg in results.Diagnostics)
            //     Console.WriteLine($"ERROR: {msg.GetMessage()}");
            var errors = results.Diagnostics.Where(m => m.Severity == DiagnosticSeverity.Error).ToArray();
            Assert.AreEqual(1, errors.Length);
            var errorMsg =
                "Could not find template for type char with parameters quantization=-1 smoothing=0 subtype=0. Default parameters can be omitted (non-quantized, no subtype, no interpolation/extrapolation).";
            Assert.AreEqual(errorMsg, errors[0].GetMessage());
        }

        [Test]
        public void SourceGenerator_QuantizeError()
        {
            var customTemplates = @"
            using Unity.NetCode;
            using System.Collections.Generic;
            namespace Unity.NetCode.Generators
            {
                internal static partial class UserDefinedTemplates
                {
                    static partial void RegisterTemplates(List<TypeRegistryEntry> templates)
                    {
                        templates.AddRange(new[]
                        {
                            new TypeRegistryEntry
                            {
                                Type = ""System.Single"",
                                Quantized = true,
                                Smoothing = SmoothingAction.Clamp
                                SupportCommand = false,
                                Composite = false,
                                SubType = 1
                                Template = $""NetCode.GhostSnapshotValueFloat.cs""
                            },
                        });
                    }
                }
            }";
            var testDataWrong = @"
            using Unity.Entities;
            using Unity.NetCode;

            public struct MyType : IComponentData
            {
                [GhostField(SubType=1)] public float AngleType;
            }
            ";
            var testDataCorrect = @"
            using Unity.Entities;
            using Unity.NetCode;

            public struct MyType : IComponentData
            {
                [GhostField(SubType=1, Quantization=1)] public float AngleType;
            }
            ";

            var tree = CSharpSyntaxTree.ParseText(testDataWrong);
            var templateTree = CSharpSyntaxTree.ParseText(customTemplates);
            var results = GeneratorTestHelpers.RunGenerators(tree, templateTree);
            var diagnostics = results.Diagnostics.Where(m => m.Severity == DiagnosticSeverity.Error).ToArray();
            Assert.AreEqual(1, diagnostics.Length);
            var expectedError =
                "Could not find template for type float with parameters quantization=-1 smoothing=0 subtype=1. Default parameters can be omitted (non-quantized, no subtype, no interpolation/extrapolation).";
            Assert.AreEqual(expectedError, diagnostics[0].GetMessage());

            tree = CSharpSyntaxTree.ParseText(testDataCorrect);
            templateTree = CSharpSyntaxTree.ParseText(customTemplates);
            results = GeneratorTestHelpers.RunGenerators(tree, templateTree);
            Assert.AreEqual(3, results.GeneratedSources.Length, "Num generated files does not match");
            var outputTree = results.GeneratedSources[0].SyntaxTree;
            var snapshotDataSyntax = outputTree.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "Snapshot");
            Assert.IsNotNull(snapshotDataSyntax);
            var expected = new[]
            {
                ("int", "AngleType"),
            };
            var members = snapshotDataSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>().ToArray();
            Assert.AreEqual(expected.Length, members.Length);
            for (int i = 0; i < expected.Length; ++i)
            {
                Assert.AreEqual(expected[i].Item1, (members[i].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text, $"{i}");
                Assert.AreEqual(expected[i].Item2, members[i].Declaration.Variables[0].Identifier.Text);
            }
        }

        [Test]
        public void SourceGenerator_SubTypeCompositeError()
        {
            var customTemplates = @"
            using Unity.NetCode;
            using System.Collections.Generic;
            namespace Unity.NetCode.Generators
            {
                internal static partial class UserDefinedTemplates
                {
                    static partial void RegisterTemplates(List<TypeRegistryEntry> templates)
                    {
                        templates.AddRange(new[]
                        {
                            new TypeRegistryEntry
                            {
                                Type = ""Unity.Mathematics.float3"",
                                SubType = 1,
                                Quantized = true,
                                Smoothing = SmoothingAction.InterpolateAndExtrapolate,
                                SupportCommand = false,
                                Composite = true,
                                Template = ""/Path/To/MyTemplate"",
                            }
                        });
                    }
                }
            }";
            var testData = @"
            using Unity.Mathematics;
            using Unity.NetCode;
            using Unity.Transforms;

            [GhostComponentVariation(typeof(Translation), ""Translation - 2D"")]
            [GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.All)]
            public struct Translation2d
            {
                [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate, SubType=1)]
                public float3 Value;
            }
            ";
            //this is an hacky way to make this supported by both 2020.x and 2021+
            //we se the templateId the same as the path, so this is resolved correclty in both case.
            var additionalTexts = ImmutableArray.Create(new AdditionalText[]
            {
                new GeneratorTestHelpers.InMemoryAdditionalFile(
                    $"/Path/To/MyTemplate{NetCodeSourceGenerator.NETCODE_ADDITIONAL_FILE}",
                    $"#templateid:/Path/To/MyTemplate\n{TestDataSource.CustomTemplate}")
            });

            var tree = CSharpSyntaxTree.ParseText(testData);
            {
                var templateTree = CSharpSyntaxTree.ParseText(customTemplates);
                var compilation = GeneratorTestHelpers.CreateCompilation(tree, templateTree);
                var driver = GeneratorTestHelpers.CreateGeneratorDriver().AddAdditionalTexts(additionalTexts);
                var results = driver.RunGenerators(compilation).GetRunResult();
                var diagnostics = results.Diagnostics.Where(m=>m.Severity == DiagnosticSeverity.Error).ToArray();
                var expectedError =
                    "Unity.Mathematics.float3: Subtype types should not also be defined as composite. Subtypes need to be explicitly defined in a template";
                Assert.That(diagnostics[0].GetMessage().StartsWith(expectedError));
            }

            customTemplates = customTemplates.Replace("Composite = true", "Composite = false");
            {
                // Fix issue and verify it now works as expected (composite true->false)
                var templateTree = CSharpSyntaxTree.ParseText(customTemplates);
                var compilation = GeneratorTestHelpers.CreateCompilation(tree, templateTree);
                var driver = GeneratorTestHelpers.CreateGeneratorDriver().AddAdditionalTexts(additionalTexts);
                var results = driver.RunGenerators(compilation).GetRunResult();
                Assert.AreEqual(3, results.GeneratedTrees.Length);
                var outputTree = results.GeneratedTrees[0];
                var snapshotDataSyntax = outputTree.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                    .FirstOrDefault(node => node.Identifier.ValueText == "Snapshot");
                Assert.IsNotNull(snapshotDataSyntax);
                var expected = new[]
                {
                    ("int", "ValueX"),
                    ("int", "ValueY"),
                };
                var members = snapshotDataSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>().ToArray();
                Assert.AreEqual(expected.Length, members.Length);
                for (int i = 0; i < expected.Length; ++i)
                {
                    Assert.AreEqual(expected[i].Item1, (members[i].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text, $"{i}");
                    Assert.AreEqual(expected[i].Item2, members[i].Declaration.Variables[0].Identifier.Text);
                }
            }
        }

        [Test]
        public void SourceGenerator_GhostComponentAttributeDefaultsAreCorrect()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            public struct DefaultComponent : IComponentData
            {
                [GhostField] public int Value;
            }";

            var tree = CSharpSyntaxTree.ParseText(testData);
            var results = GeneratorTestHelpers.RunGenerators(tree);

            //Parse the output and check that the flag on the generated class is correct (one source is registration system)
            Assert.AreEqual(3, results.GeneratedSources.Count(), "Num generated files does not match");
            var outputTree = results.GeneratedSources[0].SyntaxTree;
            var initBlockWalker = new InializationBlockWalker();
            outputTree.GetCompilationUnitRoot().Accept(initBlockWalker);
            Assert.IsNotNull(initBlockWalker.intializer);

            // SendTypeOptimization=GhostSendType.All and PrefabType=GhostPrefabType.All makes the SendMask interpolated+predicted
            var componentTypeAssignmet = initBlockWalker.intializer.Expressions.FirstOrDefault(e =>
                ((AssignmentExpressionSyntax) e).Left.ToString() == "SendMask") as AssignmentExpressionSyntax;
            Assert.IsNotNull(componentTypeAssignmet);
            Assert.AreEqual(componentTypeAssignmet.Right.ToString(),
                "GhostComponentSerializer.SendMask.Interpolated|GhostComponentSerializer.SendMask.Predicted");

            // OwnerSendType = SendToOwnerType.All
            componentTypeAssignmet = initBlockWalker.intializer.Expressions.FirstOrDefault(e =>
                ((AssignmentExpressionSyntax) e).Left.ToString() == "SendToOwner") as AssignmentExpressionSyntax;
            Assert.IsNotNull(componentTypeAssignmet);
            Assert.AreEqual(componentTypeAssignmet.Right.ToString(), "SendToOwnerType.All");

            // SendDataForChildEntity = false
            componentTypeAssignmet = initBlockWalker.intializer.Expressions.FirstOrDefault(e =>
                    ((AssignmentExpressionSyntax) e).Left.ToString() == "SendForChildEntities") as
                AssignmentExpressionSyntax;
            Assert.IsNotNull(componentTypeAssignmet);
            Assert.AreEqual(componentTypeAssignmet.Right.ToString(), "false");
        }

        [Test]
        public void SourceGenerator_SendToChildEntityIsSetCorrectly()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;
            using Unity.Transforms;
            namespace Unity.NetCode
            {
                public struct SendToChildDefault : IComponentData
                {
                    [GhostField] public int Value;
                }
                [GhostComponent(SendDataForChildEntity=true)]
                public struct SendToChild : IComponentData
                {
                    [GhostField] public int Value;
                }
                [GhostComponent(SendDataForChildEntity=false)]
                public struct DontSendToChild : IComponentData
                {
                    [GhostField] public int Value;
                }
            }";

            var tree = CSharpSyntaxTree.ParseText(testData);
            var results = GeneratorTestHelpers.RunGenerators(tree);

            Assert.AreEqual(5, results.GeneratedSources.Length, "Num generated files does not match");
            var diagnostics = results.Diagnostics;
            Assert.AreEqual(0, diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));
            //Parse the output and check that the flag on the generated class is correct
            for (int i = 0; i < 3; ++i)
            {
                var outputTree = results.GeneratedSources[i].SyntaxTree;
                var initBlockWalker = new InializationBlockWalker();
                outputTree.GetCompilationUnitRoot().Accept(initBlockWalker);
                Assert.IsNotNull(initBlockWalker.intializer);
                var componentTypeAssignmet = initBlockWalker.intializer.Expressions.FirstOrDefault(e =>
                        ((AssignmentExpressionSyntax) e).Left.ToString() == "SendForChildEntities") as
                    AssignmentExpressionSyntax;
                Assert.IsNotNull(componentTypeAssignmet);
                Assert.AreEqual(componentTypeAssignmet.Right.ToString(), (i == 1 ? "true" : "false"), "Only the GhostComponent explicitly sending child entities should have that flag.");
            }
        }

        [Test]
        [TestCase(GhostPrefabType.All, GhostSendType.AllClients,
            ExpectedResult =
                "GhostComponentSerializer.SendMask.Interpolated|GhostComponentSerializer.SendMask.Predicted")]
        [TestCase(GhostPrefabType.All, GhostSendType.OnlyPredictedClients,
            ExpectedResult = "GhostComponentSerializer.SendMask.Predicted")]
        [TestCase(GhostPrefabType.All, GhostSendType.OnlyInterpolatedClients,
            ExpectedResult = "GhostComponentSerializer.SendMask.Interpolated")]
        [TestCase(GhostPrefabType.PredictedClient, GhostSendType.OnlyPredictedClients,
            ExpectedResult = "GhostComponentSerializer.SendMask.Predicted")]
        [TestCase(GhostPrefabType.PredictedClient, GhostSendType.OnlyInterpolatedClients,
            ExpectedResult = "GhostComponentSerializer.SendMask.Predicted")]
        [TestCase(GhostPrefabType.InterpolatedClient, GhostSendType.OnlyPredictedClients,
            ExpectedResult = "GhostComponentSerializer.SendMask.Interpolated")]
        [TestCase(GhostPrefabType.InterpolatedClient, GhostSendType.OnlyInterpolatedClients,
            ExpectedResult = "GhostComponentSerializer.SendMask.Interpolated")]
        [TestCase(GhostPrefabType.Server, GhostSendType.AllClients, ExpectedResult = "GhostComponentSerializer.SendMask.None")]
        public string SourceGenerator_SendType_IsSetCorrectly(GhostPrefabType prefabType, GhostSendType sendType)
        {
            var testData = $@"
            using Unity.Entities;
            using Unity.NetCode;
            using Unity.Mathematics;
            using Unity.Transforms;

            [GhostComponent(PrefabType=GhostPrefabType.{prefabType}, SendTypeOptimization=GhostSendType.{sendType})]
            public struct SendToChild : IComponentData
            {{
                [GhostField] public int Value;
            }}
            }}";

            var tree = CSharpSyntaxTree.ParseText(testData);
            var results = GeneratorTestHelpers.RunGenerators(tree);

            Assert.AreEqual(3, results.GeneratedSources.Length, "Num generated files does not match");
            var diagnostics = results.Diagnostics;
            Assert.AreEqual(0, diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));
            //Parse the output and check that the flag on the generated class is correct
            var outputTree = results.GeneratedSources[0].SyntaxTree;
            var initBlockWalker = new InializationBlockWalker();
            outputTree.GetCompilationUnitRoot().Accept(initBlockWalker);
            Assert.IsNotNull(initBlockWalker.intializer);
            var componentTypeAssignmet = initBlockWalker.intializer.Expressions.FirstOrDefault(e =>
                ((AssignmentExpressionSyntax) e).Left.ToString() == "SendMask") as AssignmentExpressionSyntax;
            Assert.IsNotNull(componentTypeAssignmet);
            return componentTypeAssignmet.Right.ToString();
        }

        [Test]
        public void SourceGenerator_Validate_OnlyReport_KeywordNotSubst()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            namespace __GHOST_NAMESPACE__
            {
                public enum InvalidEnum
                {
                    Value = 0
                }
                public struct CantBeValid : IComponentData
                {
                    [GhostField]
                    public int field;
                }
            }
            namespace __UNDERSCORE_IS_WELCOME__
            {
                public struct __DUNNO_WHAT_BUT_IT_IS_VALID__ : IComponentData
                {
                    [GhostField]
                    public int __GHOST_IS_RESERVED;
                    [GhostField]
                    public int __ValidField;
                }

                public struct __My_Command__: ICommandData
                {
                    public NetworkTick Tick {get;set;}
                    public int __ValidField;
                    public int __COMMAND_IS_RESERVED;
                }
            }";


            var tree = CSharpSyntaxTree.ParseText(testData);
            var results = GeneratorTestHelpers.RunGenerators(tree);
            var errorCount = 0;
            for (int i = 0; i < results.Diagnostics.Length; ++i)
            {
                if (results.Diagnostics[i].Severity == DiagnosticSeverity.Error)
                {
                    Console.WriteLine(results.Diagnostics[i].ToString());
                    ++errorCount;
                }
            }

            Assert.AreEqual(3, errorCount, "errorCount");
        }

        [Test]
        public void SourceGenerator_DisambiguateEntity()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            namespace Unity.Entities
            {
                public struct Entity<T>
                {
                    public Entity ent;
                }
            }
            namespace B
            {
                public struct TestComponent : IComponentData
                {
                    [GhostField] public Entity<int> genericEntity;
                }

                public struct TestComponent2 : IComponentData
                {
                    [GhostField] public Entity entity;
                }
            }
            ";
            var tree = CSharpSyntaxTree.ParseText(testData);
            var results = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(0, results.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Error));
            Assert.AreEqual(4, results.GeneratedSources.Length, "Num generated files does not match");
            Assert.IsTrue(results.GeneratedSources[0].SourceText.ToString().Contains("TestComponent"));
            Assert.IsTrue(results.GeneratedSources[1].SourceText.ToString().Contains("TestComponent2"));

            var outputTree = results.GeneratedSources[0].SyntaxTree;
            var snapshotDataSyntax = outputTree.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>().FirstOrDefault(node => node.Identifier.ValueText == "Snapshot");
            Assert.IsNotNull(snapshotDataSyntax);
            var members = snapshotDataSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>().ToArray();
            Assert.AreEqual("int", (members[0].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text);
            Assert.AreEqual("genericEntity_ent", members[0].Declaration.Variables[0].Identifier.Text);
            Assert.AreEqual("uint", (members[1].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text);
            Assert.AreEqual("genericEntity_entSpawnTick", members[1].Declaration.Variables[0].Identifier.Text);

            outputTree = results.GeneratedSources[1].SyntaxTree;
            snapshotDataSyntax = outputTree.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>().FirstOrDefault(node => node.Identifier.ValueText == "Snapshot");
            Assert.IsNotNull(snapshotDataSyntax);
            members = snapshotDataSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>().ToArray();
            Assert.AreEqual("int", (members[0].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text);
            Assert.AreEqual("entity", members[0].Declaration.Variables[0].Identifier.Text);
            Assert.AreEqual("uint", (members[1].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text);
            Assert.AreEqual("entitySpawnTick", members[1].Declaration.Variables[0].Identifier.Text);
        }


        // NW: Test broken in master, not fixing in branch.
        [Test]
        public void SourceGenerator_SameClassInDifferentNamespace_UseCorrectHintName()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            namespace A
            {
                public struct TestComponent : IComponentData
                {
                    [GhostField] public int value;
                }
            }
            namespace B
            {
                public struct TestComponent : IComponentData
                {
                    [GhostField] public int value;
                }
            }
            ";
            var tree = CSharpSyntaxTree.ParseText(testData);
            var results = GeneratorTestHelpers.RunGenerators(tree);
            var errorCount = 0;
            for (int i = 0; i < results.Diagnostics.Length; ++i)
            {
                if (results.Diagnostics[i].Severity == DiagnosticSeverity.Error)
                {
                    Console.WriteLine(results.Diagnostics[i].ToString());
                    ++errorCount;
                }
            }
            Assert.AreEqual(0, errorCount);
            Assert.AreEqual(4, results.GeneratedSources.Length, "Num generated files does not match");
            var hintA=Generators.Utilities.TypeHash.FNV1A64(Path.Combine(GeneratorTestHelpers.GeneratedAssemblyName, "A_TestComponentSerializer.cs"));
            var hintB=Generators.Utilities.TypeHash.FNV1A64(Path.Combine(GeneratorTestHelpers.GeneratedAssemblyName, "B_TestComponentSerializer.cs"));
            var hintG=Generators.Utilities.TypeHash.FNV1A64(Path.Combine(GeneratorTestHelpers.GeneratedAssemblyName, "GhostComponentSerializerCollection.cs"));
            Assert.AreEqual($"{hintA}.cs",results.GeneratedSources[0].HintName);
            Assert.AreEqual($"{hintB}.cs",results.GeneratedSources[1].HintName);
            Assert.AreEqual($"{hintG}.cs",results.GeneratedSources[2].HintName);
        }

        // NW: Test broken in master, not fixing in branch.
        [Test]
        public void SourceGenerator_VeryLongFileName_Works()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            namespace VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG
            {
                public struct TestComponent : IComponentData
                {
                    [GhostField] public int value;
                }
            }
            ";
            var tree = CSharpSyntaxTree.ParseText(testData);
            var results = GeneratorTestHelpers.RunGenerators(tree);
            var errorCount = 0;
            for (int i = 0; i < results.Diagnostics.Length; ++i)
            {
                if (results.Diagnostics[i].Severity == DiagnosticSeverity.Error)
                {
                    Console.WriteLine(results.Diagnostics[i].ToString());
                    ++errorCount;
                }
            }
            Assert.AreEqual(0, errorCount);
            Assert.AreEqual(3, results.GeneratedSources.Length, "Num generated files does not match");
            var expetedHint1=Generators.Utilities.TypeHash.FNV1A64(Path.Combine(GeneratorTestHelpers.GeneratedAssemblyName,
                "VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG.VERYVERYVERYLONG_TestComponentSerializer.cs"));
            Assert.AreEqual($"{expetedHint1}.cs",results.GeneratedSources[0].HintName);
            var expetedHint2=Generators.Utilities.TypeHash.FNV1A64(Path.Combine(GeneratorTestHelpers.GeneratedAssemblyName, "GhostComponentSerializerCollection.cs"));
            Assert.AreEqual($"{expetedHint2}.cs",results.GeneratedSources[1].HintName);
        }

        [Test]
        public void SourceGenerator_InputComponentData()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            namespace Unity.Test
            {
                public struct PlayerInput : IInputComponentData
                {
                    public int Horizontal;
                    public int Vertical;
                    public InputEvent Jump;
                }
            }
            ";
            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(1, walker.receiver.Candidates.Count);

            // Should get input buffer struct (IInputBufferData etc) and the command data (ICommandDataSerializer etc) generated from that
            // and the registration system with the empty variant registration data
            var results = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(4, results.GeneratedSources.Length, "Num generated files does not match");
            var bufferSourceData = results.GeneratedSources[0].SyntaxTree;
            var commandSourceData = results.GeneratedSources[1].SyntaxTree;

            var inputBufferSyntax = bufferSourceData.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "PlayerInputInputBufferData");
            Assert.IsNotNull(inputBufferSyntax);
            var commandSyntax = commandSourceData.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "PlayerInputInputBufferDataSerializer");
            Assert.IsNotNull(commandSyntax);

            // Verify the 3 variables are being serialized in the command serialize methods (normal one and baseline one)
            var commandSerializerSyntax = commandSourceData.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(node => node.Identifier.ValueText == "Serialize");
            Assert.IsNotNull(commandSerializerSyntax);
            Assert.AreEqual(2, commandSerializerSyntax.Count());
            foreach (var serializerMethod in commandSerializerSyntax)
                Assert.AreEqual(3, serializerMethod.GetText().Lines.Where((line => line.ToString().Contains("data."))).Count());
        }

        [Test]
        public void SourceGenerator_InputComponentData_RemotePlayerInputPrediction()
        {
            var testData = @"
            using Unity.Entities;
            using Unity.NetCode;
            namespace Unity.Test
            {
                public struct PlayerInput : IInputComponentData
                {
                    [GhostField] public int Horizontal;
                    [GhostField] public int Vertical;
                    [GhostField] public InputEvent Jump;
                }
            }
            ";
            var receiver = GeneratorTestHelpers.CreateSyntaxReceiver();
            var walker = new TestSyntaxWalker { receiver = receiver };
            var tree = CSharpSyntaxTree.ParseText(testData);
            tree.GetCompilationUnitRoot().Accept(walker);
            Assert.AreEqual(1, walker.receiver.Candidates.Count);

            var results = GeneratorTestHelpers.RunGenerators(tree);
            Assert.AreEqual(5, results.GeneratedSources.Length, "Num generated files does not match");
            var bufferSourceData = results.GeneratedSources[0].SyntaxTree;
            var commandSourceData = results.GeneratedSources[1].SyntaxTree;
            var componentSourceData = results.GeneratedSources[2].SyntaxTree;
            var registrationSourceData = results.GeneratedSources[3].SyntaxTree;
            var inputBufferSyntax = bufferSourceData.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "PlayerInputInputBufferData");
            Assert.IsNotNull(inputBufferSyntax);

            var commandSyntax = commandSourceData.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "PlayerInputInputBufferDataSerializer");
            Assert.IsNotNull(commandSyntax);
            var sourceText = commandSyntax.GetText();
            Assert.AreEqual(0, sourceText.Lines.Where((line => line.ToString().Contains("data.Tick"))).Count());

            var componentSyntax = componentSourceData.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "PlayerInputInputBufferDataGhostComponentSerializer");
            Assert.IsNotNull(componentSyntax);

            // Verify the component snapshot data is set up correctly, this means the ghost fields
            // are configured properly in the generated input buffer for remote player prediction
            var snapshotSyntax = componentSyntax.DescendantNodes().OfType<StructDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "Snapshot");
            Assert.IsNotNull(snapshotSyntax);
            var fields = snapshotSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>().ToArray();
            Assert.AreEqual("int", (fields[0].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text);
            Assert.AreEqual("InternalInput_Horizontal", fields[0].Declaration.Variables[0].Identifier.Text);
            Assert.AreEqual("int", (fields[1].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text);
            Assert.AreEqual("InternalInput_Vertical", fields[1].Declaration.Variables[0].Identifier.Text);
            Assert.AreEqual("uint", (fields[2].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text);
            Assert.AreEqual("InternalInput_Jump_Count", fields[2].Declaration.Variables[0].Identifier.Text);
            Assert.AreEqual("uint", (fields[3].Declaration.Type as PredefinedTypeSyntax)?.Keyword.Text);
            Assert.AreEqual("Tick", fields[3].Declaration.Variables[0].Identifier.Text);

            // Verify the ghost component parameters are set up properly for the input buffer to synch
            // in the ghost snapshots for remote players
            sourceText = componentSyntax.GetText();
            Assert.AreEqual(1, sourceText.Lines.Where((line => line.ToString().Contains("PrefabType = GhostPrefabType.All"))).Count());
            Assert.AreEqual(1, sourceText.Lines.Where((line => line.ToString().Contains("SendMask = GhostComponentSerializer.SendMask.Interpolated|GhostComponentSerializer.SendMask.Predicted"))).Count());
            Assert.AreEqual(1, sourceText.Lines.Where((line => line.ToString().Contains("SendToOwner = SendToOwnerType.SendToNonOwner"))).Count());

            var maskBits = componentSyntax.DescendantNodes().OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(t => t.Declaration.Variables[0].Identifier.ValueText == "ChangeMaskBits");
            Assert.IsNotNull(maskBits);
            Assert.AreEqual("4", maskBits.Declaration.Variables[0].Initializer.Value.ToString());

            var registrationSyntax = registrationSourceData.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(node => node.Identifier.ValueText == "GhostComponentSerializerRegistrationSystem");
            Assert.IsNotNull(registrationSyntax);
            sourceText = registrationSyntax.GetText();
            Assert.AreEqual(1, sourceText.Lines.Where((line => line.ToString().Contains("AddSerializer(PlayerInputInputBufferDataGhostComponentSerializer.State)"))).Count());
        }
    }
}
