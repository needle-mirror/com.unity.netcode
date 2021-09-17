using Unity.Entities;

namespace Unity.NetCode
{
    public static class StorageInfoFromEntityExtensions
    {
        public static bool TryGetValue(this StorageInfoFromEntity self, Entity ent, out EntityStorageInfo info)
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