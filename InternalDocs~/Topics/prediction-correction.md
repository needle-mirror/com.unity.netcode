# CORRECT MISPREDICTION ERROR

## Components
`GhostPredictionSmoothing` : singleton that hold the smoothing functions

## Systems
`GhostPredictionSmoothingSystem` system responsible to apply the misprediction corrections over time.

## High Level Logic

1. Register smoothing delegate for a specific component type (i.e Transform). Should be done at runtime (i.e a system)
2. The GhostPredictionHistorySystem record the last full predicted tick state backup for `Tick X`
3. The Client receive new predicted ghost data from the server.
4. The prediction restart from Tick Y up to Tick Y (Y >= X)
   5. On the last full re-simulation tick (Tick == X):
      6. The `GhostPredictionSmoothingSystem` run.
      ```
      Collect the entities that has been re-simulated.
      For each component with a smoothing function registered:
        Extract the state from the prediction backup
        Invoke the smoothing function by passing the old and new state for the component
           - Should calculate the delta/error in between the current state (resimulated) and the old value (last predicted value)
           - Reduce the error toward 0 by applying some correction (i.e exponetial decay)
       ```
      7. The `GhostPredictionHistorySystem` run again, and store the current state of all predicted ghosts,
   that ca be partially corrected by the smoothing system.

> Rationale: The server and the client exchange messages at high frequency (i. 60hz). That means, on the best
> or average scenario, the client may receive new updates every single frame.

On average, the client rollback and repredict quite often. The smoothing action run over all predicted
ghosts for that registered smothing function and that has been re-predicted, by reducing the error over time.

### Caveats and inacurracy of the original design

- It is not intuitive. It is expected that the ghost tend to converge the error toward 0 over time continously.
- It does not work well if the predicted ghosts are not received often enough (the correction may look not smooth)
- Does not work at all for "static" ghosts (for which it may not be necessary but still)
- With enough or large number of ghosts (not only predicted), the smoothing may not run for long time, if the server
does not, or it is unable to prioritize them.



