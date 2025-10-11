using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Unity.NetCode.Generators;

public static class FixedListUtils
{
    //Fixed element require to be unmanaged (this is already ensured) and that the layout is sequential.
    //we don't allow for auto layout.
    //Notice that there is sometime some little weirdness with bool, that cause auto-layout problem.
    //However, for sake of sizeof() the struct alignment should be the same (and we can consider bool as byte aligned)
    public static Diagnostic VerifyFixedListStructRequirement(ITypeSymbol fixedListType)
    {
        var structLayoutAttribute = Roslyn.Extensions.GetAttribute(fixedListType, "System.Runtime.InteropServices", "StructLayoutAttribute");
        if (structLayoutAttribute == null)
            return null;
        if (structLayoutAttribute.ConstructorArguments.Length == 0 ||
            structLayoutAttribute.ConstructorArguments[0].Type.Name != "LayoutKind")
            return null;
        //We require
        var layoutKind = (structLayoutAttribute.ConstructorArguments[0]).ToCSharpString();
        if (layoutKind != "System.Runtime.InteropServices.LayoutKind.Sequential")
        {
            var diagnosticDescriptor = Diagnostic.Create(DiagnosticHelper.CreateErrorDescriptor($"Unsupported {layoutKind} layout type specified for {fixedListType.ToDisplayString()}. The only supported layout type for FixedList[32,64,128,512,4096]<T> type argument is the LayoutKind.Sequential."),
                fixedListType.Locations[0]);
            return diagnosticDescriptor;
        }
        return null;
    }

    public static (int, int) CalculateStructSizeOf(ITypeSymbol typeSymbol)
    {
        return CalculateStructSizeOf_Recursive(typeSymbol);
    }
    public static int CalculateNumElements(ITypeSymbol fixedListSymbol)
    {
        var sizeAndAlignment = CalculateStructSizeOf_Recursive(((INamedTypeSymbol)fixedListSymbol).TypeArguments[0]);
        var byteSize = fixedListSymbol.Name.Substring(9, fixedListSymbol.Name.IndexOf('B')-9);
        //-2 because the first 2 bytes are reserved for the list length.
        //Then there is the padding to align the element. I could avoid that honestly but for sake of complete
        //"compatibility" (same calculation) I added here as well.
        var storageSize = int.Parse(byteSize) - 2 - PaddingBytes(sizeAndAlignment.Item2);
        int numElements = storageSize / Math.Max(sizeAndAlignment.Item1, 1); // TODO - Handle zero as error.
        return numElements;
    }

    private static int PaddingBytes(int alignment)
    {
        return System.Math.Min(0, System.Math.Min(6, alignment - 2));
    }

    private static (int, int) CalculateStructSizeOf_Recursive(ITypeSymbol typeSymbol)
    {
        if (Roslyn.Extensions.IsEnum(typeSymbol))
        {
            int alignment = Roslyn.Extensions.PrimitiveTypeAlignment(((INamedTypeSymbol)typeSymbol).EnumUnderlyingType);
            return (alignment, alignment);
        }
        if (typeSymbol.SpecialType != SpecialType.None)
        {
            int alignment = Roslyn.Extensions.PrimitiveTypeAlignment(typeSymbol);
            return (alignment, alignment);
        }
        int structSize = 0;
        int structAlignment = 1;
        var members = typeSymbol.GetMembers();
        foreach (var f in members)
        {
            if(f.IsStatic)
                continue;
            if (f.Kind != SymbolKind.Field && f.Kind != SymbolKind.Property)
                continue;
            if(f.Kind == SymbolKind.Property && ((f as IPropertySymbol).IsIndexer || !f.IsImplicitlyDeclared))
                continue;

            int fieldSize = 0, fieldAlignment = 1;
            if (f.Kind == SymbolKind.Field)
            {
                (fieldSize, fieldAlignment) = CalculateStructSizeOf_Recursive(((IFieldSymbol)f).Type);
            }
            else if(f.Kind == SymbolKind.Property && f.IsImplicitlyDeclared)
            {
                (fieldSize, fieldAlignment) = CalculateStructSizeOf_Recursive(((IPropertySymbol)f).Type);
            }
            //else nothing will add size to the struct
            if ((structSize % fieldAlignment) != 0)
            {
                structSize = (structSize + fieldAlignment - 1) & ~(fieldAlignment - 1);
            }
            structSize += fieldSize;
            if (fieldAlignment > structAlignment)
                structAlignment = fieldAlignment;
        }
        structSize = structSize + (structAlignment - 1) & ~(structAlignment - 1);
        return (structSize, structAlignment);
    }
}
