# Design Rationale and questions

## How ghost snapshot are interpolated ?

The snapshot are interpolated field by field, based on the `GhostField.Smoothing` property.
The interpolation code is code-generated and customizable (partially) via custom template.

## What systems are responsible to interpolate ghost snapshots ?

The snapshot interpolation is done **before** prediction by the `GhostUpdateSystem`. The system run every frame,
at client frame rate, and take care of interpolating and extrapolating the snapshot.


-
