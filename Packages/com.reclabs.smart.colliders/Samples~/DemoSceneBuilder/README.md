# Demo Scene Builder

After importing the sample, run **Tools > Smart Colliders > Samples > Create Demo Scene**.

It builds three objects:

- **Rover (Per Mesh)**: seven meshes, each fitted with its cheapest primitive.
- **Arch (Decompose)**: one concave mesh, decomposed into several convex colliders.
- **Sprite (2D)**: a diamond sprite with `Smart Colliders 2D`. It previews as a rotated box, or as a polygon if you raise *Min Accuracy*.

To try them, select an object and press **Edit in Scene** (keys 1–5, G, M, X), or open **Window > Smart Colliders**. Then **Bake**.

The meshes and sprite are created in memory. Save the scene and hull meshes before you build prefabs from them.
