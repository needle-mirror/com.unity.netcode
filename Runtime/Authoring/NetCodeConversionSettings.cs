#if UNITY_EDITOR
using Unity.Build;
#endif

public enum NetcodeConversionTarget
{
    Undefined = 0,
    Server = 1,
    Client = 2
}

#if UNITY_EDITOR
public class NetCodeConversionSettings : IBuildSettingsComponent
{
    public NetcodeConversionTarget Target;
    public string Name => "NetCode Conversion Settings";

    public bool OnGUI()
    {
        UnityEditor.EditorGUI.BeginChangeCheck();
        Target = (NetcodeConversionTarget) UnityEditor.EditorGUILayout.EnumPopup("Target", Target);
        return UnityEditor.EditorGUI.EndChangeCheck();
    }
}
#endif
