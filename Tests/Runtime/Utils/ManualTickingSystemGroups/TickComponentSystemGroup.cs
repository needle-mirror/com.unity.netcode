using System.Collections.Generic;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Base class for all the tick system, provide a common update mehod that deal with proper and safe
    /// handling of system removal at runtime, in particular when the world in which those systems are created is destroyd.
    /// </summary>
    internal abstract partial class TickComponentSystemGroup : ComponentSystemGroup
    {
        struct UpdateGroup
        {
            public World world;
            public ComponentSystemGroup group;
        }
        private List<UpdateGroup> m_UpdateGroups = new List<UpdateGroup>();
        private List<int> m_InvalidUpdateGroups = new List<int>();

        /// <summary>
        /// Add the group to the update list.
        /// </summary>
        /// <param name="grp"></param>
        public void AddSystemGroupToTickList(ComponentSystemGroup grp)
        {
            m_UpdateGroups.Add(new UpdateGroup{world = grp.World, group = grp});
            AddSystemToUpdateList(grp);
        }

        /// <summary>
        /// Update all the children groups and remove them from the update list if they become invalid or destroyed.
        /// </summary>
        protected override void OnUpdate()
        {
            for (int i = 0; i < m_UpdateGroups.Count; ++i)
            {
                if (!m_UpdateGroups[i].world.IsCreated)
                    m_InvalidUpdateGroups.Add(i);
            }
            if (m_InvalidUpdateGroups.Count > 0)
            {
                // Rever order to make sure we remove largest indices first
                for (int i = m_InvalidUpdateGroups.Count - 1; i >= 0; --i)
                {
                    var idx = m_InvalidUpdateGroups[i];
                    RemoveSystemFromUpdateList(m_UpdateGroups[idx].group);
                    m_UpdateGroups.RemoveAt(idx);
                }
                m_InvalidUpdateGroups.Clear();
            }
            base.OnUpdate();
        }
    }

}
