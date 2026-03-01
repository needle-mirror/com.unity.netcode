#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID

using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// System that invoke the <see cref="GhostBehaviour.GatherInput"/> method on <see cref="GhostBehaviour"/>
    /// This ensure the input data are sampled in the same way their entities counterpart are and
    /// that the <see cref="GhostOwnerIsLocal"/> enable state is up-to-date, avoiding writing data to entities/object
    /// the client does not own and have input authority over.
    /// </summary>
    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    partial class GhostBehaviourInputUpdateSystem : BaseNetcodeUpdateCaller
    {
        protected override void InitQueryForGhosts()
        {
            var builder = new EntityQueryBuilder(Allocator.Temp);
            //All these are pretty much required for now. The PredictedGhost and AutoCommandTarget are currently mandatory.
            // TODO-release@potentialOptim GhostOwnerIsLocal means this system runs on a bunch of ghosts with local owner, but not necessarily with any inputs. We can probably generate one system per input component type instead. Or just add a filter for AutoCommandTarget? This way we're sure this is a ghost containing an input? However, right now we filter GhostBehaviours on whether it contains a GatherInput callback. Do we want to allow GatherInput setting the input on other ghosts? Do we really want to link GatherInput being executed only on ghosts with an input component? I could have a "main input gatherer" script with 4-5 target ghosts and I apply inputs on those from that "main" ghost (for example if I have a pet and want to use the input stream to send that pet's ghost commands). --> should test with a sample first to see what we want to support there before doing this optim.
            // OR could also gather the list of existing input component types and add a "Any" filter on the query for each of these types.

            CommandSendSystemGroup.SetBuilderToGhostsWithInputQuery(ref builder);
            builder.WithAll<GhostGameObjectLink, GhostBehaviour.GhostBehaviourTracking>();
            m_GhostsToRunOn = GetEntityQuery(builder);
        }

        protected override bool HasUpdate(in GhostBehaviourTypeInfo typeInfo)
        {
            return typeInfo.HasInputUpdate;
        }

        // TODO-next@inputImprovements have a way to detect if the script has an input, but no ownership server side, that users messed their ownership setup. Or just set things up for users correctly. Or just have owners for everyone
        protected override void RunMethodOnBehaviour(GhostBehaviour behaviour, float deltaTime)
        {
            // This should execute on a limited amount of ghosts, it's expected to usually only have 1 ghost with this method.
            behaviour.GatherInput(SystemAPI.Time.DeltaTime);
        }
    }
}

#endif
