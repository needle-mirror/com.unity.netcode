using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// Extract from the prefab the converted entities components, in respect to the selected variant and default
    /// mapping provided by the user
    /// </summary>
    class EntityPrefabComponentsPreview
    {
        struct ComponentNameComparer : IComparer<ComponentType>
        {
            public int Compare(ComponentType x, ComponentType y) =>
                x.GetManagedType().FullName.CompareTo(y.GetManagedType().FullName);
        }

        private GhostComponentVariantLookup variantLookup;

        public EntityPrefabComponentsPreview(GhostComponentVariantLookup lookup)
        {
            variantLookup = lookup;
        }

        public List<ComponentItem> GetComponentsPreview(GhostAuthoringComponent authoringComponent)
        {
            using(var world = new World("TempGhostConversion"))
            {
                using var blobAssetStore = new BlobAssetStore();
                authoringComponent.ForcePrefabConversion = true;
                var settings = GameObjectConversionSettings.FromWorld(world, blobAssetStore);
                settings.ConversionFlags = GameObjectConversionUtility.ConversionFlags.AddEntityGUID;
                var convertedEntity = GameObjectConversionUtility.ConvertGameObjectHierarchy(authoringComponent.gameObject, settings);
                authoringComponent.ForcePrefabConversion = false;

                return GetComponentItems(authoringComponent, convertedEntity, world);
            }
        }

        public void RefreshComponentInfo(ComponentItem item)
        {
            //Get the correct serialization variant for the component.
            var variantType = variantLookup.GetVariantForComponent(ComponentType.ReadWrite(item.comp.managedType), item.Variant);
            if (variantType == null)
                variantType = item.comp.managedType;
            ExtractComponentInfo(item, variantType);
        }

        public List<ComponentItem> GetComponentItems(GhostAuthoringComponent authoringComponent, Entity convertedEntity, World world)
        {
            var newComponents = new List<ComponentItem>();
            AddToComponentList(newComponents, world, convertedEntity, 0, authoringComponent);
            if (world.EntityManager.HasComponent<LinkedEntityGroup>(convertedEntity))
            {
                var linkedEntityGroup = world.EntityManager.GetBuffer<LinkedEntityGroup>(convertedEntity);
                for (int i = 1; i < linkedEntityGroup.Length; ++i)
                {
                    AddToComponentList(newComponents, world, linkedEntityGroup[i].Value, i, authoringComponent);
                }
            }

            foreach (var compItem in newComponents)
            {
                var variantType = variantLookup.GetVariantForComponent(ComponentType.ReadWrite(compItem.comp.managedType), compItem.Variant);
                if (variantType == null)
                    variantType = compItem.comp.managedType;
                ExtractComponentInfo(compItem, variantType);
            }

            return newComponents;
        }

       static void AddToComponentList(List<ComponentItem> newComponents, World world, Entity convertedEntity,
           int entityIndex, GhostAuthoringComponent authoringComponent)
       {
            var compTypes = world.EntityManager.GetComponentTypes(convertedEntity);
            compTypes.Sort(default(ComponentNameComparer));

            for (int i = 0; i < compTypes.Length; ++i)
            {
                var managedType = compTypes[i].GetManagedType();
                if (managedType == typeof(Prefab) || managedType == typeof(LinkedEntityGroup))
                    continue;

                var guid = world.EntityManager.GetComponentData<EntityGuid>(convertedEntity);
                var prefabModifier = authoringComponent.GetPrefabModifier(managedType.FullName, guid);
                var compData = new GhostAuthoringComponentEditor.SerializedComponentData
                {
                    name = managedType.FullName,
                    managedType = managedType,
                    attribute = new GhostComponentAttribute()
                };
                var componentItem = new ComponentItem(compData, prefabModifier, guid, entityIndex);
                newComponents.Add(componentItem);
            }
        }

        static void ExtractComponentInfo(ComponentItem item, System.Type variantType)
        {
            item.UpdateGhostComponent(variantType.GetCustomAttribute<GhostComponentAttribute>());
            var fields = new List<GhostAuthoringComponentEditor.ComponentField>();
            foreach (var member in variantType.GetMembers(BindingFlags.Instance | BindingFlags.Public))
            {
                if(member.GetCustomAttribute<DefaultMemberAttribute>() != null ||
                   member.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
                    continue;

                var attr = member.GetCustomAttribute<GhostFieldAttribute>();
                if (attr == null || !attr.SendData)
                    continue;

                switch (member.MemberType)
                {
                    case MemberTypes.Field:
                        FillSubFields(member, ((FieldInfo) member).FieldType, attr.Quantization, attr.Smoothing, fields);
                        break;
                    case MemberTypes.Property:
                    {
                        if (IsValidProperty((PropertyInfo) member))
                            FillSubFields(member, ((PropertyInfo) member).PropertyType, attr.Quantization, attr.Smoothing, fields);
                        break;
                    }
                }
            }

            item.comp.fields = fields.ToArray();
        }

        static private void FillSubFields(MemberInfo field, System.Type memberType, int quantization,
            SmoothingAction smoothing, List<GhostAuthoringComponentEditor.ComponentField> fieldsList, string parentPrefix = "")
        {
            if (!memberType.IsValueType)
                return;

            if (memberType.IsPrimitive || memberType.IsEnum)
            {
                fieldsList.Add(new GhostAuthoringComponentEditor.ComponentField
                {
                    name = parentPrefix + field.Name,
                    quantization = quantization,
                    smoothing = smoothing
                });
                return;
            }

            parentPrefix = parentPrefix + field.Name + "_";
            foreach (var member in memberType.GetMembers(BindingFlags.Instance | BindingFlags.Public))
            {
                var fieldAttr = member.GetCustomAttribute<GhostFieldAttribute>();
                if (fieldAttr != null && !fieldAttr.SendData)
                    continue;
                if (fieldAttr != null)
                {
                    quantization = fieldAttr.Quantization != -1
                        ? fieldAttr.Quantization
                        : quantization;
                    smoothing = fieldAttr.Smoothing;
                }
                switch (member.MemberType)
                {
                    case MemberTypes.Field:
                        FillSubFields(member, ((FieldInfo)member).FieldType, quantization, smoothing, fieldsList,
                            parentPrefix);
                        break;
                    case MemberTypes.Property:
                    {
                        if (IsValidProperty((PropertyInfo) member))
                            FillSubFields(member, ((PropertyInfo)member).PropertyType, quantization, smoothing, fieldsList,
                                parentPrefix);
                        break;
                    }
                }
            }
        }

        static bool IsValidProperty(PropertyInfo propertyInfo)
        {
            //Skip indexer like properties or anything that return non primitive types
            if (propertyInfo.GetIndexParameters()?.Length != 0)
                return false;
            if(!propertyInfo.PropertyType.IsPrimitive && !propertyInfo.PropertyType.IsEnum)
                return false;
            return (propertyInfo.GetMethod != null && propertyInfo.SetMethod != null &&
                    propertyInfo.GetMethod.IsPublic && propertyInfo.SetMethod.IsPublic);
        }
    }
}
