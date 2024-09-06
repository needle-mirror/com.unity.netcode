# Prediction

Use prediction to manage latency in your game.

| **Topic**                       | **Description**                  |
| :------------------------------ | :------------------------------- |
| **[Introduction to prediction](intro-to-prediction.md)** | Client prediction allows clients to use their own inputs to locally simulate the game, without waiting for the server's simulation result. |
| **[Prediction in Netcode for Entities](prediction-n4e.md)** | Implement client prediction in Netcode for Entities. |
| **[Prediction smoothing](prediction-smoothing.md)**  | The `GhostPredictionSmoothingSystem` system provides a way of reconciling and reducing prediction errors over time, to make the transitions between states smoother. |
| **[Prediction switching](prediction-switching.md)** | Netcode supports opting into prediction on a per-client, per-ghost basis, based on some criteria (for example, predict all ghosts inside this radius of my clients' character controller). This feature is called prediction switching. |
| **[Prediction edge cases and known issues](prediction-details.md)** | When using client-side prediction, there are a few known edge cases you should be aware of. |
