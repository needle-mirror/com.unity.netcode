using System.Reflection;
using Unity.Entities;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// Intenal class used by the GhostComponentInspector to store UI and component
    /// related states.
    /// TODO: merge the SerializedComponentData into that class instead of using both whence the first
    /// porting pass is done
    /// </summary>
    class ComponentItem
    {
        public GhostAuthoringComponentEditor.SerializedComponentData comp;
        public GhostAuthoringComponent.ComponentOverride prefabOverride;
        readonly public int entityIndex;
        readonly public EntityGuid entityGuid;

        //Enable/Disable overrides
        public bool modifyPrefabType;
        public bool modifySendType;
        public bool modifySendForChild;
        public bool modifyVariant;

        //UI related
        public string header;
        public bool expanded;
        public bool hasDataToSend => comp.fields?.Length > 0 && (entityIndex == 0 || SendForChild);
        public bool useAllDefaults => !modifyPrefabType && !modifySendType && !modifySendForChild && !modifyVariant;
        public string[] variantNames;
        public ulong[] availableVariants;
        readonly public bool supportPrefabOverrides;

        public ComponentItem(GhostAuthoringComponentEditor.SerializedComponentData component,
            GhostAuthoringComponent.ComponentOverride prefabOverrides, EntityGuid guid, int index)
        {
            comp = component;
            expanded = false;
            entityGuid = guid;
            entityIndex = index;
            supportPrefabOverrides = comp.managedType.GetCustomAttribute<DontSupportPrefabOverrides>() == null;
            prefabOverride = prefabOverrides;
            if (prefabOverride != null)
            {
                modifyPrefabType = prefabOverride.PrefabType !=
                                   GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
                modifySendType = prefabOverride.OwnerPredictedSendType !=
                                 GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
                modifySendForChild = prefabOverride.SendForChild !=
                                     GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
                modifyVariant = prefabOverride.ComponentVariant != 0;
            }
            UpdateHeader();
        }

        //This function is called when prefab has changed for some reason or when the SerializeObject need
        //to be synched (unfortunately every frame)
        public void Update(GhostAuthoringComponent authoringComponent)
        {
            var newPrefabOverride = authoringComponent.GetPrefabModifier(comp.managedType.FullName, entityGuid);
            if (newPrefabOverride != null)
            {
                var prefabType = PrefabType;
                var hasPrefabModifiers = prefabOverride != null;

                modifyPrefabType = newPrefabOverride.PrefabType !=
                                   GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
                modifySendType = newPrefabOverride.OwnerPredictedSendType !=
                                 GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
                modifySendForChild = newPrefabOverride.SendForChild !=
                                     GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
                modifyVariant = newPrefabOverride.ComponentVariant != 0;
                prefabOverride = newPrefabOverride;

                if (!hasPrefabModifiers || prefabType != PrefabType)
                    UpdateHeader();
            }
            else if(prefabOverride != null)
            {
                prefabOverride = null;
                modifyPrefabType = false;
                modifySendType = false;
                modifySendForChild = false;
                modifyVariant = false;
                UpdateHeader();
            }
        }

        public GhostPrefabType PrefabType => prefabOverride != null && modifyPrefabType
            ? (GhostPrefabType)prefabOverride.PrefabType
            : comp.attribute.PrefabType;
        public GhostSendType SendType => prefabOverride != null && modifySendType
            ? (GhostSendType) prefabOverride.OwnerPredictedSendType
            : comp.attribute.OwnerPredictedSendType;
        public bool SendForChild => prefabOverride != null && modifySendForChild
                ? prefabOverride.SendForChild != 0
                : comp.attribute.SendDataForChildEntity;
        public ulong Variant => prefabOverride != null && modifyVariant
            ? prefabOverride.ComponentVariant
            : 0;

        public void UpdateGhostComponent(GhostComponentAttribute compAttr)
        {
            comp.attribute = compAttr ?? new GhostComponentAttribute();
            UpdateHeader();
        }

        public void UpdatePrefabOverrides(GhostPrefabType prefabType, GhostSendType sendType, bool sendForChild, ulong variant)
        {
            //Apply prefab modifier only if they are different from the default values
            prefabOverride.PrefabType = modifyPrefabType
                ? (int) prefabType
                : GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
            prefabOverride.OwnerPredictedSendType = modifySendType
                ? (int) sendType
                : GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
            prefabOverride.SendForChild = modifySendForChild
                ? sendForChild ? 1 : 0
                : GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
            prefabOverride.ComponentVariant = modifyVariant
                ? variant
                : 0;
            UpdateHeader();
        }

        public void OnRemoveOverride()
        {
            prefabOverride = null;
            modifyPrefabType = false;
            modifySendType = false;
            modifySendForChild = false;
            modifyVariant = false;
            UpdateHeader();
        }

        public void UpdateHeader()
        {
            var prefabType = PrefabType;
            header = string.Format("{0}{1} ({2}/{3}/{4}){5}",
                entityIndex != 0 ? "Child " + (entityIndex - 1) + ": " : "",
                comp.name, (prefabType & GhostPrefabType.Server) != 0 ? "S" : "-",
                (prefabType & GhostPrefabType.InterpolatedClient) != 0 ? "IC" : "-",
                (prefabType & GhostPrefabType.PredictedClient) != 0 ? "PC" : "-",
                prefabOverride != null ? "*" : "");
        }
    }
}
