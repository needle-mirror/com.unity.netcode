using Unity.Entities;

namespace Unity.Netcode
{
    internal static class EntityStorageInfoLookupExtensions
    {
        public static bool TryGetValue(this EntityStorageInfoLookup self, Entity ent, out EntityStorageInfo info)
        {
            if (!self.Exists(ent))
            {
                info = default;
                return false;
            }
            info = self[ent];
            return true;
       }
    }
}
