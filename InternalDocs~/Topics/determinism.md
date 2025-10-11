### DETERMINISTIC REQUIREMENT (OR NON REQUIREMENT)

Netcode for Entities use state replication and provide an `Eventual Consistent` synchronization between server and client.

It is not requires at any point in time that all snapshot must have been received by the client to reconstruct the current server state on the client.
It is also not required or mandatory that all commands sent by the client must be processed and received by the server in time.

Being the server the `authority`, the final state on the client at a certain point in the future will and must always converge to the server state.
It is client responsibiity to correct its state accordingly to the authority all the time.

However, to have a proper implemententation of `Client Side Prediction`, the client should perform the "same" simulation locally as the server does
(or as close as possible) for a given simulation tick, command and state.

In a perfect world scenerio, both client and server simulation should be completely deterministic. However, because of:

- Floating point math
- Different entity chunks on client and server and partition
- Different entity iteration order (even with the same partition)
- Quantization

it is impossible to guarantee bit-level determism both on the same architecture (i.e x64) and cross-platform (i.e x64 and arm64).

Given these premises, Netcode for Enities **does not strictly require determistic behaviour from the application.**

However, some requirement are necessary to still ensure "close-enough" results/ouput from the simulation loop:

### Requirement 1
Systems that runs in the prediction loop (especially client side, but that apply in general to both) should always use replicated data to modify the state of
predicted ghost entities.
On the server, modifing the state of non-predicted ghost (i.e always interpolated) does not require such limitation.

### Requirement 2
Given the same initial state before the PredictedSimulationSystemGroup update, re-simulating the same tick must produce the same result.

With state here we are referring to:
- The replicated predicted ghost data
- Any local data for the given world used by the simulation.


### The achievable ideal deterministic update
Even in the ideal case scenario when the client and server has the same identical data for the next simulated tick (all replicated for example),
Netcode for Entities can't expect a full determistic output from any system in the PredictedSimulationSystemGroup. I.e, because of different chunk iteration order.

However, in the limit in which:
- The simulated entities are independent.
- The intra-dependency (if they exist) are guaranteed to be handled in the same order by client and server (i.e sorted arrays).
- The Requirement 1 and 2 are fullfilled.
- Server and Client runs on the same process or even distinct but same architecture (or better processor).
- No quantization is used for transmitting floating point.

the simulations can be expected to be very close to be fully deterministic (almost identical results) in between client and server, or at most up to a very small `fuzzy factor`.

