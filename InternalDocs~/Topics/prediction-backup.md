# PREDICTION BACKUP

## Components
- `GhostPredictionHistoryState`: internal singleton, hold the backup history mapping and history metedata.
- `PredictionBufferHistoryData` Data structure used to preserve the ability to retrieve or infer,
  even in case of structural changes, the prediction history data for a given entity.
- `GhostSnapshotLastBackupTick`: store the last backup tick
  GhostPredictionHistorySystem
- `PredictionBackupState` the header part of the backup data. Expose the methods to access the various bits of the
  backup.

## Systems
- `GhostPredictionHistorySystem`: is the system responsible to backup this prediction hystory information.

## Backup Usage
- `GhostUpdateSystem`: uses the backup for running partial ticks.
- `GhostPredictionSmoothingSystem`: uses the backup for running partial ticks.

## High Level Logic

The `GhostPredictionHistorySystem` run only for 'full ticks' and process all chunks with predicted ghosts.

The replicated components and buffers data is stored "per-chunk".

```
For each chunk:
- Allocate a contigous array of bytes, large enough to store the necesasry data (with proper alignment)
- The allocated buffer is store in a hashmap, mapping chunk -> backup data.
- memcpy all replicated component and buffer data into the state backup for the chunk. It does that for both root and child entities.
```

> Rationale: we trade off clarity for speed and flexibity (no need to complicate our life with c# limitation). However,
> the maintainability of this is hard and make the code brittle to changes (and hard to debug)

## Backup data layout

The backup data is composed by two parts:
- an header with a well structured data (the `PredictionBackupState`)
- a byte of raw data, with a complex layout.

Following a "json-like" representation of the data

```json lines
Data
{
   Entities: [],
   RootComponent: {
     "Component1": {
       EnableBits: "one bit per entity",
       Version: "chunk version",
       Data: [
         "byte array for entity 1",
         "byte array for entity 2"
       ]
     },
     "Component2": {
       EnableBits: "one bit per entity",
       Version: "chunk version",
       Data: [
         "data for entity 1",
         "data for entity 2"
       ]
     },
     "Buffer1": {
       EnableBits: "one bit per entity",
       Version: "chunk version",
       Data: [
         {
           "Lenght": "len for entity 1",
           "Offset": "offset where the data is stored in the dynamic data portion of the buffer",
         },
         {
           "Lenght": "len for entity 2",
           "Offset": "offset where the data is stored in the dynamic data portion of the buffer",
         }
       ]
     }
   },
  ChildComponents: {
    "Child Component 1": {
      EnableBits: "one bit per chunk entity (root chunk)",
      Version: [
        "entity1->child X, chunk version for the chunk for the child X entity",
        "entity1->child X, chunk version for the chunk for the child X entity",
      ],
      "Data": [
        "data for entity 1->child X",
        "data for entity 2->child X"
      ]
    },
    "Child Component 2": {
      EnableBits: "one bit per chunk entity (root chunk)",
      Version: [
        "entity1->child X1, chunk version for the chunk for the child X1 entity",
        "entity1->child X1, chunk version for the chunk for the child X1 entity",
      ],
      "Data": [
        "data for entity 1->child X1",
        "data for entity 2->child X1"
      ]
    },
    //the next component is for the same child entity
    "Child Component 2": {
      EnableBits: "one bit per chunk entity (root chunk)",
      Version: [
        "entity1->child X1, chunk version for the chunk for the child X1 entity",
        "entity1->child X1, chunk version for the chunk for the child X1 entity",
      ],
      "Data": [
        "data for entity 1->child X1",
        "data for entity 2->child X1"
      ]
    },
    //the next component is for another for another child entity
    "Child Component 3": {
      EnableBits: "one bit per chunk entity (root chunk)",
      Version: [
        "chunk version child chunk1",
        "chunk version child chunk2"
      ],
      Data: [
        "data for entity 1, child chunk 2",
        "data for entity 2, child chunk 2"
      ]
    },
    //and so on.. same for buffer (as above)
  },
  DynamicBufferData: {
    "Buffer1": ["data for entity1", "data for entity N"],
    "Buffer2": ["data for entity1", "data for entity N"],
    "BufferN": ["data for entity1", "data for entity N"],
  }
}
```

