using System;

namespace Unity.Netcode
{
    /// <summary>
    /// This attribute is used to disable code generation for a struct implementing ICommandData or IRpcCommand
    /// </summary>
    [AttributeUsage(AttributeTargets.Class|AttributeTargets.Struct)]
    public class NetcodeDisableCommandCodeGenAttribute : Attribute
    {
    }
}
