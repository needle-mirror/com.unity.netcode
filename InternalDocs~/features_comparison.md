| FEATURE                                         | NGO  | N4E  | FUSION | SNAPNET | COHERENCE | MIRROR | FISHNET |
|-------------------------------------------------|------|------|--------|---------|-----------|--------|---------|
| CAN REPLICATE GAMEOBJECT COMPONENTS             | Y    | N    | Y      | Y       | y         | Y      | Y       |
| BIT-WISE DETERMINISM                            | N    | N    | N      | N       | N         | N      | N       |
| SEMI-DETERMINISTIC                              | N    | Y    | N      | Y       | Y         | N      | N       |
| GAME QUALITY AFFECTED BY OTHER PLAYERS          | X    | X    | X      | N       | N         | N      | N       |
| LATENCY AFFECT CPU COST                         | N    | Y    | Y      | Y       | N         | Y      | Y       |
| SERVER AUTHORITY                                | Y    | Y    | Y      | Y       | Y         | Y      | Y       |
| CLIENT AUTHORITY                                | Y    | N    | Y      | ?       | Y         | Y      | Y       |
| CLIENT SIDE PREDICTION                          | N    | Y    | Y      | Y       | N         | Y      | Y       |
| CLIENT SIDE PREDICTED PHYSICS                   | X    | Y    | Y      | Y       | N         | Y      | Y       |
| ANIMATION REPLICATION                           | Y    | Y(5) | Y      | Y       | Y         | Y      | Y       |
| PHYSICS REPLICATION                             | Y    | Y    | Y      | Y       | Y         | Y      | Y       |
| IN-SCENE OBJECT REPLICATION                     | Y    | Y    | Y      | Y       | Y         | Y      | Y       |
| LOCAL INPUT LAG                                 | N    | N    | Y      | Y       | N         | N      | N       |
| SERVER REWIND (LAG COMPENSATION)                | N    | Y    | Y      | Y       | N         | Y      | Y       |
| STATE INTERPOLATION                             | Y    | Y    | Y      | Y       | Y         | Y      | Y       |
| RENDERING AND SIMULATION DECOUPLED              | N    | Y    | Y      | Y       | Y         | Y      | Y       |
| TIMELINE SWAPPING (RENDERING,PREDICTION,SERVER) | N/A  | Y    | Y      | N       | N         | ?      | ?       |
| AREA OF INTEREST                                | X    | Y    | Y      | y       | Y         | Y      | Y       |
| PRIORITIZATION                                  | X    | Y    | Y      | y       | Y         | Y      | Y       |
| REPLICATED EVENTS                               | X    | N    | Y      | y       | N         | N      | N       |
| PREDICTED EVENTS                                | X    | N    | Y      | y       | N         | N      | N       |
| PARTIAL WORLD STATE UPDATE                      | Y    | Y    | Y      | y       | Y         | Y      | Y       |
| FULL MATCH REPLAY                               | N    | N    | Y      | y       | N         | N      | N       |
| ON-DEMAND IN-GAME REPLAY                        | N    | N    | Y      | y       | N         | N      | N       |
| CHEAT DETECTION                                 | N    | N    | N      | y       | N         | N      | N       |
| ENCRIPTION                                      | Y    | Y    | Y      | y       | Y         | Y      | Y       |
| UNRELIABLE RPCS                                 | Y    | N    | Y      | y       | N         | Y      | Y       |
| UNRELIABLE EVENTS                               | N    | N    | Y      | y       | N         | N      | N       |
| NETWORK SIMULATION                              | Y    | Y    | Y      | y       | Y         | Y      | Y       |
| PACKET COMPRESSION                              | Y    | Y    | Y      | y       | Y         | Y      | Y       |
| ADDRESSABLE SUPPORT                             | Y(3) | N(4) | ?      | ?       | Y         | Y      | Y       |
| LARGE PACKETS ( > 1MTU)                         | Y    | Y    | ?      | Y       | Y         | Y      | Y       |
| SCENE MANAGEMENT                                | Y    | N    | Y      | Y       | Y         | Y      | Y       |
| OFFLINE MODE                                    | Y(2) | Y(2) | N      | N       | N         | N      | N       |
| SEAMLESS ONLINE/OFFLINE MODE                    | X    | Y    | N      | N       | N         | ?      | ?       |
| SESSION MANAGEMENT                              | Y(1) | Y(1) | N      | N       | N         | N      | N       |
| COUCH-COOP SUPPORT                              | N    | N    | N      | N       | N         | N      | N       |


> `1` via Multiplayer SDK
> `2` Achieveable, not "out of the box" support
> `3` Some limitation or user code necessary
> `4` Not out of the box. But possible to implement similar flow with the new Archive system and using addressable to
construct at runtime ghost prefab
> `5` Playable support with prediction and root motion handling.
