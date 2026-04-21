using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Foothold;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;

    internal static Scene currentScene;
    internal static MainCamera mainCamera;
    internal static bool activated = false;
    internal static Material baseMaterial;
    internal static float alpha = 1;

    private static readonly Queue<GameObject> balls = [];
    private static readonly Queue<GameObject> redBalls = [];
    private static readonly Queue<GameObject> pool_balls = [];
    private static readonly Queue<GameObject> pool_redBalls = [];
    private static float lastScanTime = 0; // should only be needed when fade away is on
    private static float lastAlphaChangeTime = 0; // should only be needed when fade away is on

    private ConfigEntry<KeyCode> configActivationKey;
    private ConfigEntry<bool> configDebugMode;
    private ConfigEntry<Mode> configMode;
    private ConfigEntry<StandableColor> configStandableBallColor;
    private ConfigEntry<NonStandableColor> configNonStandableBallColor;
    private Color LastStandableColor = Color.white;
    private Color LastNonStandableColor = Color.red;

    private static readonly int poolSize = 10000; 
    private static bool isVisualizationRunning = false;
    private static bool isPruning = false;
    private static bool cancelPrune = false;

    private static readonly int MainYield = 8000;
    private static readonly int PlaceYield = 3000;
    private static readonly int SlowPlaceYield = 1000;

    private Coroutine _backgroundPruneCoroutine;
    private const float PruneIntervalSeconds = 5f; // Prune every 5 seconds


    // Grid parameters are intentionally fixed and not user-configurable.
    // Changing these values significantly impacts performance:
    // - Smaller freq = more grid points = more raycasts and memory usage
    // - Larger range = more grid points = more raycasts and memory usage
    // Total grid points = 78,141 (61 × 21 × 61)

    public static float xFreq = 0.5f;
    public static float yFreq = 1.0f;
    public static float zFreq = 0.5f;

    public static int xRange = 15;
    public static int yRange = 10;
    public static int zRange = 15;

    // Pre-calculated at class initialization time.
    // If grid parameters change, this value must be recalculated.
    private static readonly int totalGridPoints = ((int)((xRange - (-xRange)) / xFreq) + 1) * ((int)((yRange - (-yRange)) / yFreq) + 1) * ((int)((zRange - (-zRange)) / zFreq) + 1);

    private List<Vector3> visiblePositions = new(totalGridPoints);
    private List<Vector3> nonVisiblePositions = new(totalGridPoints);

    private static readonly int MaxCacheSize = totalGridPoints * 3;

    private Dictionary<Vector3Int, CachedResult> _positionCache = new Dictionary<Vector3Int, CachedResult>(totalGridPoints * 5);
    private const float SnapSize = 0.5f;

    private List<Vector3Int> _pruneBuffer = new List<Vector3Int>(totalGridPoints * 3);

    private int _raycastCount = 0;
    private int _cacheHitCount = 0;

    /*
     * Yield thresholds for coroutine execution to prevent frame drops.
     *
     * MainYield is used during the preprocessing phase:
     * - Iterates through the 3D grid and separates positions into [visiblePositions] and [nonVisiblePositions].
     * - Sorts both lists by distance to the camera.
     * - This is a lightweight operation and should complete within 5 frames.
     *
     * PlaceYield is used during the ball placement phase:
     * - Processes each position by calling CheckAndPlaceBallAt.
     * - Prioritizes [visiblePositions] first, then [nonVisiblePositions].
     * - This is a heavier operation, as it involves a RaycastHit for each of the 35,281 positions.
     *
     * The yield values are chosen to balance performance and responsiveness:
     * - A higher yield value allows more work per frame but increases the risk of frame drops.
     * - At 120 FPS, the current setting results in less than 5 FPS drop, which is negligible.
     * - In laggy scenarios (e.g., 20 FPS), the coroutine should still complete in under 2 seconds without noticeable impact.
     */

    private void Awake()
    {
        Logger = base.Logger;

        configStandableBallColor = Config.Bind("General", "Standable ground Color", StandableColor.Green, "Change the ball color of standable ground.");
        configNonStandableBallColor = Config.Bind("General", "Non-standable ground Color", NonStandableColor.Magenta, "Change the ball color of non-standable ground.");

        configActivationKey = Config.Bind("General", "Activation Key", KeyCode.F);

        configMode = Config.Bind("General", "Activation Mode", Mode.Trigger, """
            Toggle: Press once to activate; press again to hide the indicator.
            Fade Away: Activates every time the button is pressed. The indicator will fade away after 3 seconds. Credit to VicVoss on GitHub for the idea.
            Trigger: Activates every time the button is pressed. The indicator will remain visible.
            """);
        configDebugMode = Config.Bind("General", "Debug Mode", false, "Show debug information");

        Material mat = new(Shader.Find("Universal Render Pipeline/Lit"));
        // permanently borrowed from https://discussions.unity.com/t/how-to-make-a-urp-lit-material-semi-transparent-using-script-and-then-set-it-back-to-being-solid/942231/3
        mat.SetFloat("_Surface", 1);
        mat.SetFloat("_Blend", 0);
        mat.SetInt("_IgnoreProjector", 1);           // Ignore projectors (like rain)
        mat.SetInt("_ReceiveShadows", 0);            // Disable shadow reception
        mat.SetInt("_ZWrite", 0);                    // Disable z-writing
        mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        //mat.renderQueue = (int)RenderQueue.Transparent;
        mat.renderQueue = 3500; // Renders after standard transparent objects
        baseMaterial = mat;

        SceneManager.sceneLoaded += OnSceneLoaded;

        configStandableBallColor.SettingChanged += Color_SettingChanged;
        configNonStandableBallColor.SettingChanged += Color_SettingChanged;
        configMode.SettingChanged += ConfigMode_SettingChanged;

        Logger.LogMessage($"          Plugin {MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} is loaded!");
    }

    private void ConfigMode_SettingChanged(object sender, EventArgs e)
    {
        ReturnBallsToPool();

        if (configMode.Value != Mode.FadeAway)
        {
            foreach (GameObject ball in balls.Concat(redBalls))
            {
                Material mat = ball.GetComponent<Renderer>().material;
                Color baseColor = mat.GetColor("_BaseColor");
                baseColor.a = alpha;
                mat.SetColor("_BaseColor", baseColor);
            }
        }
    }

    private void Color_SettingChanged(object sender, EventArgs e)
    {
        if (currentScene.name.StartsWith("Level_") || currentScene.name.StartsWith("Airport"))
        {
            ReturnBallsToPool();

            Color standable;
            if (configStandableBallColor.Value == StandableColor.Green)
            {
                standable = Color.green;
            }
            else
            {
                standable = Color.white;
            }

            Color NonStandable;
            if (configNonStandableBallColor.Value == NonStandableColor.Magenta)
            {
                NonStandable = Color.magenta;
            }
            else
            {
                NonStandable = Color.red;
            }

            if (LastStandableColor != standable)
            {
                pool_balls.Clear();
                for (int i = 0; i < poolSize; i++)
                {
                    pool_balls.Enqueue(CreateBall(standable));
                }
                LastStandableColor = standable;
            }

            if (LastNonStandableColor != NonStandable)
            {
                pool_redBalls.Clear();
                for (int i = 0; i < poolSize; i++)
                {
                    pool_redBalls.Enqueue(CreateBall(NonStandable));
                }
                LastNonStandableColor = NonStandable;
            }
        }
    }

    private void OnGUI()
    {
        if (!configDebugMode.Value) return;
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("");
        GUILayout.Label("Camera: " + Camera.main.transform.position);
        GUILayout.Label("");
        GUILayout.Label("balls: " + balls.Count);
        GUILayout.Label("redBalls: " + redBalls.Count);
        GUILayout.Label("pool_balls: " + pool_balls.Count);
        GUILayout.Label("pool_redBalls: " + pool_redBalls.Count);
        GUILayout.Label("alpha: " + alpha);
        GUILayout.Label("isVisualizationRunning: " + isVisualizationRunning.ToString());
        GUILayout.Label("isPruning: " + isPruning.ToString());
        GUILayout.Label("");
        GUILayout.Label("totalGridPoints: " + totalGridPoints);
        GUILayout.Label("_positionCache: " + _positionCache.Count);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode _)
    {
        currentScene = scene;
        // Checking for mainCamera in update because it PROBABLY spawns after scene load for networking reasons but this is a complete guess

        balls.Clear();
        redBalls.Clear();
        pool_balls.Clear();
        pool_redBalls.Clear();

        if (currentScene.name.StartsWith("Level_") || currentScene.name.StartsWith("Airport"))
        {
            // make pools

            Color standable;
            if (configStandableBallColor.Value == StandableColor.Green)
            {
                standable = Color.green;
            }
            else
            {
                standable = Color.white;
            }

            Color NonStandable;
            if (configNonStandableBallColor.Value == NonStandableColor.Magenta)
            {
                NonStandable = Color.magenta;
            }
            else
            {
                NonStandable = Color.red;
            }

            LastStandableColor = standable;
            LastNonStandableColor = NonStandable;

            for (int i = 0; i < poolSize; i++)
            {
                pool_balls.Enqueue(CreateBall(standable));
                pool_redBalls.Enqueue(CreateBall(NonStandable));
            }

            if (_backgroundPruneCoroutine != null)
            {
                StopCoroutine(_backgroundPruneCoroutine);
            }

            _backgroundPruneCoroutine = StartCoroutine(BackgroundPruneCoroutine());
        }
        else
        {
            if (_backgroundPruneCoroutine != null)
            {
                StopCoroutine(_backgroundPruneCoroutine);
                _backgroundPruneCoroutine = null;
            }
        }
    }

    private GameObject CreateBall(Color ballColor)
    {
        GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.SetActive(false);

        // Assign to a rendering layer
        // Try UI layer first, fall back to Default if not found
        int layer = LayerMask.NameToLayer("UI");
        ball.layer = layer != -1 ? layer : LayerMask.NameToLayer("Default");

        Renderer renderer = ball.GetComponent<Renderer>();
        Material material = new(baseMaterial);

        renderer.material = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        ball.GetComponent<Collider>().enabled = false;

        // Ensure alpha is fully opaque
        ballColor.a = 1.0f;
        renderer.material.SetColor("_BaseColor", ballColor);

        ball.transform.localScale = Vector3.one / 5;

        /*
        // Debug logging
        Logger.LogMessage($"Ball created:");
        Logger.LogMessage($"  Layer: {ball.layer} ({LayerMask.LayerToName(ball.layer)})");
        Logger.LogMessage($"  Render Queue: {material.renderQueue}");
        Logger.LogMessage($"  Color: {ballColor}");
        Logger.LogMessage($"  Material Shader: {material.shader.name}");
        */

        return ball;
    }

    private void Update()
    {
        if (currentScene.name.StartsWith("Level_") || currentScene.name.StartsWith("Airport"))
        {
            if (mainCamera == null)
            {
                mainCamera = FindFirstObjectByType<MainCamera>();
                return;
            }
            CheckHotkeys();
            if (configMode.Value == Mode.FadeAway) SetBallAlphas();
        }
    }

    // Keeping Update clean by factoring this out
    private void CheckHotkeys()
    {
        if (Input.GetKeyDown(configActivationKey.Value))
        {
            if (isVisualizationRunning) return;

            if (configMode.Value == Mode.Trigger)
            {
                isVisualizationRunning = true;

                StartCoroutine(RenderVisualizationCoroutine());
                return;
            }

            if (configMode.Value == Mode.Toggle)
                activated = !activated;
            if (activated || configMode.Value == Mode.FadeAway) // The activated check is after the change, so this is checking if it has just been toggled on
            {
                isVisualizationRunning = true;

                StartCoroutine(RenderVisualizationCoroutine());
            }
            else
            {
                ReturnBallsToPool();
            }
        }
    }

    private void ReturnBallsToPool()
    {
        foreach (GameObject ball in balls.ToList())
        {
            ball.SetActive(false);
            balls.Dequeue();
            pool_balls.Enqueue(ball);
        }
        foreach (GameObject ball in redBalls.ToList())
        {
            ball.SetActive(false);
            redBalls.Dequeue();
            pool_redBalls.Enqueue(ball);
        }
    }

    // Technically CheckAndPlaceBallAt does the actual rendering, but also technically Unity does the rendering,
    // but also technically Vulkan/DX12/DX11 does the rendering, but also technically the GPU does the rendering,
    // all this to say I don't care and I'll name my methods whatever I want
    private void RenderVisualization()
    {
        ReturnBallsToPool();
        lastScanTime = Time.time;
        float freq = 0.5f;
        float yFreq = 1f;
        for (float x = -10; x <= 10; x += freq)
        {
            for (float y = -10; y <= 10; y += yFreq)
            {
                for (float z = -10; z <= 10; z += freq)
                {
                    Vector3 position = new(
                        mainCamera.transform.position.x + x,
                        mainCamera.transform.position.y + y,
                        mainCamera.transform.position.z + z
                    );
                    CheckAndPlaceBallAt(position);
                }
            }
        }
    }

    
    private IEnumerator RenderVisualizationCoroutine()
    {
        // If pruning is running, interrupt it immediately
        if (isPruning)
        {
            cancelPrune = true;

            // Wait for the pruning coroutine to acknowledge the cancellation
            while (isPruning)
                yield return null;

            cancelPrune = false;
        }

        ReturnBallsToPool();
        lastScanTime = Time.time;

        int totalCalls = 0;
        visiblePositions.Clear();
        nonVisiblePositions.Clear();

        Camera theCamera = Camera.main;
        Vector3 cameraPos = theCamera.transform.position; // Cache position once

        //yield return StartCoroutine(PruneCacheCoroutine(cameraPos, range: 25f));
        //PruneCache(cameraPos, range: 25f);

        // First pass: Separate positions into visible and non-visible
        for (float x = -xRange; x <= xRange; x += xFreq)
        {
            for (float y = -yRange; y <= yRange; y += yFreq)
            {
                for (float z = -zRange; z <= zRange; z += zFreq)
                {
                    Vector3 position = new(
                        cameraPos.x + x,
                        cameraPos.y + y,
                        cameraPos.z + z
                    );

                    Vector3Int gridKey = SnapToGrid(position);

                    // Check cache FIRST — skip frustum check if cached
                    if (_positionCache.TryGetValue(gridKey, out CachedResult cached))
                    {
                        // Use cached result directly — no frustum check needed
                        if (cached.HasHit && cached.IsStandable && cached.Angle > 30f)
                        {
                            PlaceBall(cached.HitPoint, cached.Angle);
                        }

                        _cacheHitCount++;

                        if (totalCalls++ % PlaceYield == 0)
                            yield return null;

                        continue; // Skip to next position
                    }

                    // Uses WorldToViewportPoint() to check if each position is within the camera's view frustum (viewport coordinates 0-1 and z > 0)

                    Vector3 viewportPoint = theCamera.WorldToViewportPoint(position);
                    bool isVisible =
                        viewportPoint.x >= 0 && viewportPoint.x <= 1 &&
                        viewportPoint.y >= 0 && viewportPoint.y <= 1 &&
                        viewportPoint.z > 0;

                    if (isVisible)
                    {
                        visiblePositions.Add(position);
                    }
                    else
                    {
                        nonVisiblePositions.Add(position);
                    }

                    if (totalCalls++ % MainYield == 0)
                        yield return null;
                }
            }
        }

        // Calls SortWithYield() on both lists to sort by distance to camera for optimization)
        yield return StartCoroutine(SortWithYield(visiblePositions, cameraPos, MainYield));
        yield return StartCoroutine(SortWithYield(nonVisiblePositions, cameraPos, MainYield));

        totalCalls = 0;

        // Calls CheckAndPlaceBallAt() for each position, starting with visible ones

        // Second pass: Process visible positions first
        foreach (Vector3 pos in visiblePositions)
        {
            if (pool_balls.Count <= 0)
                break;
            CheckAndPlaceBallAtWithCache(pos);
            //CheckAndPlaceBallAt(pos);
            if (totalCalls++ % PlaceYield == 0)
                yield return null;
        }

        // Third pass: Process non-visible positions
        foreach (Vector3 pos in nonVisiblePositions)
        {
            if (pool_redBalls.Count <= 0)
                break;
            CheckAndPlaceBallAtWithCache(pos);
            //CheckAndPlaceBallAt(pos);
            if (totalCalls++ % SlowPlaceYield == 0)
                yield return null;
        }

        if (_positionCache.Count > MaxCacheSize)
        {
            if (configDebugMode.Value)
                Logger.LogMessage($"Cache size {_positionCache.Count} exceeded threshold. Pruning now.");
            yield return StartCoroutine(PruneCacheCoroutine(cameraPos, range: 25f));
        }

        isVisualizationRunning = false;

        // Log the statistics
        float cacheEfficiency = _cacheHitCount > 0
            ? (_cacheHitCount / (float)(_raycastCount + _cacheHitCount)) * 100f
            : 0f;
        if (configDebugMode.Value)
        {
            Logger.LogMessage($"Raycast Statistics:");
            Logger.LogMessage($"  Raycasts performed: {_raycastCount}");
            Logger.LogMessage($"  Cache hits: {_cacheHitCount}");
            Logger.LogMessage($"  Cache efficiency: {cacheEfficiency:F1}%");
            Logger.LogMessage($"  Total cached entries: {_positionCache.Count}");
        }

        // Reset counters for the next scan
        _raycastCount = 0;
        _cacheHitCount = 0;
    }

    IEnumerator SortWithYield(List<Vector3> list, Vector3 cameraPosition, int chunkSize)
    {
        int n = list.Count;
        int totalIterations = 0;

        // Create a custom comparison delegate
        Comparison<Vector3> compare = (a, b) =>
        {
            float distA = (a - cameraPosition).sqrMagnitude;
            float distB = (b - cameraPosition).sqrMagnitude;
            return distA.CompareTo(distB);
        };

        // Sort in chunks
        for (int i = 0; i < n; i += chunkSize)
        {
            int end = Mathf.Min(i + chunkSize, n);
            List<Vector3> chunk = list.GetRange(i, end - i);

            chunk.Sort(compare);

            // Replace the chunk in the original list
            for (int j = 0; j < chunk.Count; j++)
            {
                list[i + j] = chunk[j];
                totalIterations++;

                if (totalIterations % MainYield == 0)
                {
                    yield return null;
                }
            }
        }
    }

    /*
    IEnumerator SortWithYield(List<Vector3> list, Vector3 cameraPosition)
    {
        list.Sort((a, b) =>
        {
            float distA = (a - cameraPosition).sqrMagnitude;
            float distB = (b - cameraPosition).sqrMagnitude;
            return distA.CompareTo(distB);
        });

        yield return null; // Single yield to not block the frame
    }
    */

    private Dictionary<Collider, CollisionModifier> _colliderModifierCache = new();

    private CollisionModifier GetCachedModifier(Collider collider)
    {
        if (!_colliderModifierCache.TryGetValue(collider, out CollisionModifier modifier))
        {
            modifier = collider.GetComponent<CollisionModifier>();
            _colliderModifierCache[collider] = modifier;
        }
        return modifier;
    }

    // Cache this once in Awake() or as a static field
    private static readonly int TerrainLayerMask = HelperFunctions.GetMask(HelperFunctions.LayerType.TerrainMap);

    // Optimized replacement for the specific downward check in CheckAndPlaceBallAt
    private static bool TryRaycastDown(Vector3 position, float distance, out RaycastHit hit)
    {
        return Physics.Raycast(
            position,
            Vector3.down,
            out hit,
            distance,
            TerrainLayerMask,
            QueryTriggerInteraction.Ignore
        );
    }

    private void CheckAndPlaceBallAt(Vector3 position)
    {
        if (!TryRaycastDown(position, 1f, out RaycastHit raycastHit))
            return;

        CollisionModifier component = GetCachedModifier(raycastHit.collider);
        if (component != null && !component.standable)
            return;

        float angle = Vector3.Angle(Vector3.up, raycastHit.normal);
        if (angle <= 30f) return;

        if (angle < 50f && pool_balls.Count > 0)
        {
            GameObject ball = pool_balls.Dequeue();
            ball.transform.position = raycastHit.point;
            balls.Enqueue(ball);
            ball.SetActive(true);
        }
        else if (angle >= 50f && pool_redBalls.Count > 0)
        {
            GameObject ball = pool_redBalls.Dequeue();
            ball.transform.position = raycastHit.point;
            redBalls.Enqueue(ball);
            ball.SetActive(true);
        }
    }

    private struct CachedResult
    {
        public bool HasHit;
        public Vector3 HitPoint;
        public float Angle;
        public bool IsStandable;
    }

    private Vector3Int SnapToGrid(Vector3 position)
    {
        return new Vector3Int(
            Mathf.RoundToInt(position.x / SnapSize),
            Mathf.RoundToInt(position.y / SnapSize),
            Mathf.RoundToInt(position.z / SnapSize)
        );
    }

    private void CheckAndPlaceBallAtWithCache(Vector3 position)
    {
        Vector3Int gridKey = SnapToGrid(position);

        // Position is NOT cached (already checked in the grid loop)
        // Perform the raycast
        _raycastCount++;

        if (!TryRaycastDown(position, 1f, out RaycastHit raycastHit))
        {
            _positionCache[gridKey] = new CachedResult { HasHit = false };
            return;
        }

        CollisionModifier component = GetCachedModifier(raycastHit.collider);
        bool isStandable = component == null || component.standable;
        float angle = Vector3.Angle(Vector3.up, raycastHit.normal);

        // Cache the result
        _positionCache[gridKey] = new CachedResult
        {
            HasHit = true,
            HitPoint = raycastHit.point,
            Angle = angle,
            IsStandable = isStandable,
        };

        if (!isStandable || angle <= 30f)
            return;

        PlaceBall(raycastHit.point, angle);
    }

    private void PlaceBall(Vector3 hitPoint, float angle)
    {
        if (angle < 50f && pool_balls.Count > 0)
        {
            GameObject ball = pool_balls.Dequeue();
            ball.transform.position = hitPoint;
            balls.Enqueue(ball);
            ball.SetActive(true);
        }
        else if (angle >= 50f && pool_redBalls.Count > 0)
        {
            GameObject ball = pool_redBalls.Dequeue();
            ball.transform.position = hitPoint;
            redBalls.Enqueue(ball);
            ball.SetActive(true);
        }
    }

    private void PruneCache(Vector3 cameraPos, float range = 25f)
    {
        float rangeSqr = range * range;
        List<Vector3Int> toRemove = [];

        foreach (var key in _positionCache.Keys)
        {
            Vector3 worldPos = new Vector3(key.x, key.y, key.z) * SnapSize;
            if ((worldPos - cameraPos).sqrMagnitude > rangeSqr)
                toRemove.Add(key);
        }

        foreach (var key in toRemove)
            _positionCache.Remove(key);

        //Plugin.Log.LogMessage($"Cache pruned. Remaining entries: {_positionCache.Count}");
    }

    private IEnumerator BackgroundPruneCoroutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(PruneIntervalSeconds);

            try
            {
                // Only prune if we are in a valid scene and the camera is available
                if (mainCamera == null) continue;
                if (!currentScene.name.StartsWith("Level_") && !currentScene.name.StartsWith("Airport")) continue;
                if (isVisualizationRunning) continue;
            }
            catch (Exception e)
            {
                Logger.LogError($"BackgroundPruneCoroutine failed during scene check: {e.Message}");
                yield break;
            }

            isPruning = true;
            yield return StartCoroutine(PruneCacheCoroutine(mainCamera.transform.position, range: 25f));
            isPruning = false;

            if (configDebugMode.Value)
                Logger.LogMessage($"Background prune completed. Cache size: {_positionCache.Count}");
        }
    }
    private IEnumerator PruneCacheCoroutine(Vector3 cameraPos, float range = 25f)
    {
        isPruning = true;
        float rangeSqr = range * range;
        _pruneBuffer.Clear();

        int totalChecked = 0;

        // First pass: collect keys to remove
        foreach (var key in _positionCache.Keys)
        {
            if (cancelPrune)
            {
                if (configDebugMode.Value)
                    Logger.LogMessage("PruneCacheCoroutine: Interrupted by scan request.");
                isPruning = false;
                yield break;
            }

            Vector3 worldPos = new Vector3(key.x, key.y, key.z) * SnapSize;
            if ((worldPos - cameraPos).sqrMagnitude > rangeSqr)
                _pruneBuffer.Add(key);

            if (totalChecked++ % MainYield == 0)
                yield return null;
        }

        // Second pass: remove entries spread across frames
        int totalRemoved = 0;
        foreach (var key in _pruneBuffer)
        {
            if (cancelPrune)
            {
                if (configDebugMode.Value)
                    Logger.LogMessage("PruneCacheCoroutine: Interrupted during removal.");
                isPruning = false;
                yield break;
            }

            _positionCache.Remove(key);

            if (totalRemoved++ % MainYield == 0)
                yield return null;
        }

        if (configDebugMode.Value)
            Logger.LogMessage($"Background prune completed. Removed: {_pruneBuffer.Count}. Cache size: {_positionCache.Count}");

        isPruning = false;
    }

    /*
    // Most of this code was "borrowed" from CharacterMovement.RaycastGroundCheck
    private void CheckAndPlaceBallAt(Vector3 position)
    {
        Vector3 to = position + Vector3.down * 1;
        RaycastHit raycastHit = HelperFunctions.LineCheck(position, to, HelperFunctions.LayerType.TerrainMap, 0f, QueryTriggerInteraction.Ignore);
        if (raycastHit.transform)
        {
            CollisionModifier component = GetCachedModifier(raycastHit.collider);
            if (component)
            {
                if (!component.standable)
                {
                    return;
                }
            }
            float angle = Vector3.Angle(Vector3.up, raycastHit.normal);
            if (angle > 30f) // lower limit on the angle because showing balls on flat ground is pretty pointless
            {
                if (angle < 50f && pool_balls.Count > 0)
                {
                    GameObject ball = pool_balls.Dequeue();
                    ball.transform.position = raycastHit.point;
                    balls.Enqueue(ball);
                    ball.SetActive(true);
                }
                else if (angle >= 50f && pool_redBalls.Count > 0)
                {
                    GameObject ball = pool_redBalls.Dequeue();
                    ball.transform.position = raycastHit.point;
                    redBalls.Enqueue(ball);
                    ball.SetActive(true);
                }
            }
        }
    }
    */

    // This method is dedicated to VicVoss
    private void SetBallAlphas()
    {
        if (configMode.Value != Mode.FadeAway) return; // this shouldn't be needed but it's good to be safe

        // effectively restrict framerate to 20 for performance
        if (Time.time - lastAlphaChangeTime < 0.05) return;
        lastAlphaChangeTime = Time.time;

        alpha = Mathf.Lerp(1f, 0f, Mathf.Clamp01((Time.time - (lastScanTime + 3)) / 3));

        foreach (GameObject ball in balls.Concat(redBalls))
        {
            Material mat = ball.GetComponent<Renderer>().material;
            Color baseColor = mat.GetColor("_BaseColor");
            baseColor.a = alpha;
            mat.SetColor("_BaseColor", baseColor);
        }

        if (alpha <= 0)
            {
                if (balls.Count > 0)
                {
                    foreach (GameObject ball in balls.ToList()) // ToList used to clone the list because you can't modify what you're enumerating
                    {
                        ball.SetActive(false);
                        balls.Dequeue();
                        pool_balls.Enqueue(ball);
                    }
                }
                if (redBalls.Count > 0)
                {
                    foreach (GameObject ball in redBalls.ToList()) // ToList used to clone the list because you can't modify what you're enumerating
                    {
                        ball.SetActive(false);
                        redBalls.Dequeue();
                        pool_redBalls.Enqueue(ball);
                    }
                }
            }
    }

    internal enum StandableColor
    {
        White,
        Green
    }

    internal enum NonStandableColor
    {
        Red,
        Magenta
    }
    
    internal enum Mode
    {
        Toggle,
        FadeAway,
        Trigger
    }
}
