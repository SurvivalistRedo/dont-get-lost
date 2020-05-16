﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class chunk : MonoBehaviour
{
    // The size of the chunk in meters, also
    // defines the resolution of the terrain
    public const int SIZE = 64;

    // The chunk coordinates
    public int x { get; private set; }
    public int z { get; private set; }

    // My parent biome
    biome _biome;
    biome biome
    {
        get
        {
            if (_biome == null)
                _biome = GetComponentInParent<biome>();
            return _biome;
        }
    }

    // Keep a quick lookup for chunks
    static two_int_dictionary<chunk> generated_chunks =
        new two_int_dictionary<chunk>();

    //##################//
    // COORDINATE TOOLS //
    //##################//

    // Check if this chunk is within render range
    // (essentially testing if the render range circle 
    //  intersects the chunk square)
    bool in_range()
    {
        Vector2 player_xz = new Vector2(
            player.current.transform.position.x,
            player.current.transform.position.z
        );

        Vector2 this_xz = new Vector2(
            SIZE * (x + 0.5f),
            SIZE * (z + 0.5f)
        );

        return utils.circle_intersects_square(player_xz, game.render_range, this_xz, SIZE, SIZE);
    }

    // Get the chunk coords at a given location
    public static int[] coords(Vector3 location)
    {
        return new int[]
        {
            Mathf.FloorToInt(location.x / SIZE),
            Mathf.FloorToInt(location.z / SIZE)
        };
    }

    // Returns the chunk at the given location
    public static chunk at(Vector3 location, bool generated_only = false)
    {
        var c = coords(location);
        var gc = generated_chunks.get(c[0], c[1]);
        if (gc != null) return gc;
        if (generated_only) return null;
        Debug.Log("chunk.at fallback triggered.");
        return GameObject.Find("chunk_" + c[0] + "_" + c[1])?.GetComponent<chunk>();
    }

    // Is this chunk enabled in the world?
    new public bool enabled
    {
        get
        {
            // If I have been destroyed, I'm definately not enabled
            if (this == null) return false;

            // If I have enabled children, I'm active
            return transform.childCount > 0 &&
                   transform.GetChild(0).gameObject.activeInHierarchy;
        }

        private set
        {
            // No change
            if (value == enabled)
                return;

            // If enabled, ensure generation has begun
            if (value && !generation_begun)
                begin_generation();

            // Disable, or enable all children
            foreach (Transform t in transform)
                t.gameObject.SetActive(value);
        }
    }

    //#################//
    // UNITY CALLBACKS //
    //#################//

    private void Update()
    {
        // Enabled if the player is in range
        enabled = in_range();
    }

    // Highlight the chunk if enabled
    private void OnDrawGizmos()
    {
        var color = Color.cyan;
        if (!enabled) color = Color.black;
        color.a = 0.1f;
        Gizmos.color = color;

        Gizmos.DrawWireCube(
            transform.position + new Vector3(1, 0, 1) * SIZE / 2f,
            new Vector3(SIZE, 0.01f, SIZE));
    }

    void OnDestroy()
    {
        generated_chunks.remove(x, z);
    }

    //##################//
    // CHUNK GENERATION //
    //##################//

    // The resolution of the terrain is set so that 
    // each terrain square is one square meter
    public const int TERRAIN_RES = SIZE + 1;
    public Terrain terrain { get; private set; }

    // The chunk needs a seperate random number generator from the
    // biome, because chunks within a biome can be generated in any
    // order (based on the route the player takes). This would mess up
    // the determinism of the pseudorandom number generation.
    public System.Random random { get; private set; }

    // The biome points, saved post blending
    biome.point[,] blended_points = new biome.point[TERRAIN_RES, TERRAIN_RES];

    // Create a chunk with chunk coordinates x, z
    public static chunk create(int x, int z, int seed)
    {
        // Save my chunk-coordinates for
        // later use in generation
        var c = new GameObject("chunk " + x + " " + z).AddComponent<chunk>();
        c.x = x;
        c.z = z;
        c.random = new System.Random(seed);

        // Setup the transform of the chunk
        c.transform.position = new Vector3(x, 0, z) * SIZE;

        return c;
    }

    // Returns true if generation of the chunk has begun
    bool generation_begun { get { return terrain != null; } }

    // Trigger the generation of this chunk
    void begin_generation()
    {
        // Create the water level
        var water = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(water.GetComponent<Collider>());
        water.transform.SetParent(transform);
        water.transform.localPosition = new Vector3(
            SIZE / 2, world.SEA_LEVEL, SIZE / 2);
        water.transform.localScale = Vector3.one * SIZE;
        water.transform.forward = -Vector3.up;
        var ren = water.gameObject.GetComponent<MeshRenderer>();
        ren.material = Resources.Load<Material>("materials/standard_shader/water");
        ren.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        // Create the underside of the water
        var water_bottom = water.inst();
        Destroy(water_bottom.GetComponent<Collider>());
        water_bottom.transform.SetParent(water.transform);
        water_bottom.transform.localPosition = Vector3.zero;
        water_bottom.transform.forward = Vector3.up;

        // Create the terrain object, collider and datastructure
        terrain = new GameObject("terrain").AddComponent<Terrain>();
        var tc = terrain.gameObject.AddComponent<TerrainCollider>();
        terrain.transform.SetParent(transform);
        terrain.transform.localPosition = Vector3.zero;
        var td = new TerrainData();
        terrain.terrainData = td;
        tc.terrainData = td;
        terrain.terrainData.heightmapResolution = TERRAIN_RES;
        terrain.terrainData.size = new Vector3(SIZE, world.MAX_ALTITUDE, SIZE);
        terrain.enabled = false;

        // Start the gradual chunk generator
        var generator = new GameObject("generator").AddComponent<gradual_chunk_generator>();
        generator.transform.SetParent(transform);
        generator.chunk = this;
    }

    // Continue generation, bit by bit
    // returns true when generation is complete
    delegate bool gen_func();
    gen_func[] gen_todo;
    int gen_stage = 0;
    System.Diagnostics.Stopwatch gen_sw;

    bool continue_generation()
    {
        if (gen_todo == null)
        {
            // Create the generation stages
            gen_todo = new gen_func[]
            {
                continue_set_points,
                continue_set_alphamaps,
                continue_apply_heights,
                continue_gen_objects
            };
            gen_sw = System.Diagnostics.Stopwatch.StartNew();
        }

        // Generation complete
        if (gen_stage >= gen_todo.Length)
            return true;

        // Continue this generation stage
        if (gen_todo[gen_stage]())
        {
            utils.log("Chunk " + x + "_" + z + " generation stage: " + gen_stage +
                      " took " + gen_sw.ElapsedMilliseconds + " ms", "chunk_generation");
            // Advance generation stage
            gen_stage++;
        }

        return false;
    }

    int points_i = 0;
    bool continue_set_points()
    {
        if (points_i >= TERRAIN_RES)
            return true;

        int i = points_i;
        for (int j = 0; j < TERRAIN_RES; ++j)
        {
            // Get the blended point descibing this part of the world
            Vector3 world_pos = new Vector3(i, 0, j) + transform.position;
            blended_points[i, j] = biome.blended_point(world_pos);
            blended_points[i, j].apply_global_rules();
        }

        ++points_i;
        return false;
    }

    int alphamaps_i = 0;
    float[,,] alphamaps = new float[TERRAIN_RES, TERRAIN_RES, 1];
    Color[] pixels = new Color[TERRAIN_RES * TERRAIN_RES];
    bool continue_set_alphamaps()
    {
        if (alphamaps_i >= TERRAIN_RES)
            return true;

        // Generate alphamaps
        int i = alphamaps_i;
        for (int j = 0; j < TERRAIN_RES; ++j)
        {
            pixels[j * TERRAIN_RES + i] = blended_points[i, j].terrain_color;
            alphamaps[i, j, 0] = 1.0f;
        }

        if (alphamaps_i == TERRAIN_RES - 1)
        {
            // Create the terrain texture
            SplatPrototype[] splats = new SplatPrototype[1];
            var tex = new Texture2D(TERRAIN_RES, TERRAIN_RES);
            tex.wrapMode = TextureWrapMode.Clamp;

            // Create the terain layers
            var terrain_layers = new TerrainLayer[1];
            terrain_layers[0] = new TerrainLayer();
            terrain_layers[0].diffuseTexture = tex;
            terrain_layers[0].tileSize = new Vector2(1f, 1f) * SIZE;
            terrain.terrainData.terrainLayers = terrain_layers;
            terrain.materialTemplate = Resources.Load<Material>("materials/terrain");

            // Apply the alphmaps
            tex.SetPixels(pixels);
            tex.Apply();
            terrain.terrainData.SetAlphamaps(0, 0, alphamaps);

            alphamaps = null;
            pixels = null;
        }

        ++alphamaps_i;
        return false;
    }

    int heights_i = 0;
    float[,] heights = new float[TERRAIN_RES, TERRAIN_RES];
    bool continue_apply_heights()
    {
        if (heights_i >= TERRAIN_RES) return true;

        // Map world onto chunk
        for (int j = 0; j < TERRAIN_RES; ++j)
            // Heightmap (note it is the transpose for some reason)
            heights[j, heights_i] = blended_points[heights_i, j].altitude / world.MAX_ALTITUDE;
        ++heights_i;

        if (heights_i >= TERRAIN_RES)
        {
            // Apply the heigtmap
            terrain.terrainData.SetHeights(0, 0, heights);
            heights = null;
            terrain.enabled = true;
        }
        return false;
    }

    int objects_i = 0;
    bool continue_gen_objects()
    {
        if (objects_i >= SIZE) return true;

        // Generate objects
        int i = objects_i;
        for (int j = 0; j < SIZE; ++j)
        {
            var point = blended_points[i, j];

            // Check if there is a world object at this point
            if (point.object_to_generate == null) continue;

            // Get the terrain normals
            float xf = i / (float)TERRAIN_RES;
            float zf = j / (float)TERRAIN_RES;
            Vector3 terrain_normal = terrain.terrainData.GetInterpolatedNormal(xf, zf);

            if (point.object_to_generate.can_place(point, terrain_normal))
            {
                // Place the world object
                var wo = point.object_to_generate.inst();
                wo.transform.SetParent(transform);
                wo.transform.localPosition = new Vector3(i, point.altitude, j);
                wo.on_placement(terrain_normal, point, this, i, j);
            }
        }

        ++objects_i;
        return false;
    }

    // Called when the chunk has finished generating
    void on_generation_complete()
    {
        generated_chunks.add(x, z, this);

        // Load all the characters
        for (int i = 0; i < SIZE; ++i)
            for (int j = 0; j < SIZE; ++j)
            {
                var point = blended_points[i, j];
                if (point.character_to_generate != null)
                {
                    var c = point.character_to_generate.inst();
                    c.transform.position = transform.position + new Vector3(i, point.altitude, j);
                }
            }

        biome.update_chunk_neighbours();
    }

    // This component is attached to a chunk that is
    // being generated and is responsible for spreading
    // the load of generation across multiple frames
    class gradual_chunk_generator : MonoBehaviour
    {
        public chunk chunk;
        static int steps_per_frame = 1;

        private void Update()
        {
            // Do more generation steps if the framerate is
            // acceptably high
            int framerate = (int)(1 / Time.deltaTime);
            if (framerate < 60) --steps_per_frame;
            else ++steps_per_frame;

            // Generation must always progress
            if (steps_per_frame < 1) steps_per_frame = 1;

            // Generate every frame until
            // we need not generate any more
            for (int step = 0; step < steps_per_frame; ++step)
                if (chunk.continue_generation())
                {
                    // Generation complete
                    chunk.on_generation_complete();

                    // Destroy this generator
                    Destroy(gameObject);
                    return;
                }
        }
    }
}