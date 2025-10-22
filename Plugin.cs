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

    private static readonly int poolSize = 3000; 
    private static bool isVisualizationRunning = false;

    private void Awake()
    {
        Logger = base.Logger;

        configStandableBallColor = Config.Bind("General", "Standable ground Color", StandableColor.White, "Change the ball color of standable ground.");
        configNonStandableBallColor = Config.Bind("General", "Non-standable ground Color", NonStandableColor.Red, "Change the ball color of non-standable ground.");

        configActivationKey = Config.Bind("General", "Activation Key", KeyCode.F);

        configMode = Config.Bind("General", "Activation Mode", Mode.Toggle, """
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
        mat.renderQueue = (int)RenderQueue.Transparent;
        baseMaterial = mat;

        SceneManager.sceneLoaded += OnSceneLoaded;

        configStandableBallColor.SettingChanged += Color_SettingChanged;
        configNonStandableBallColor.SettingChanged += Color_SettingChanged;
        configMode.SettingChanged += ConfigMode_SettingChanged;

        Logger.LogInfo($"Loaded Foothold? version {MyPluginInfo.PLUGIN_VERSION}");
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
        GUILayout.Label("balls: " + balls.Count);
        GUILayout.Label("redBalls: " + redBalls.Count);
        GUILayout.Label("pool_balls: " + pool_balls.Count);
        GUILayout.Label("pool_redBalls: " + pool_redBalls.Count);
        GUILayout.Label("alpha: " + alpha);
        GUILayout.Label("isVisualizationRunning: " + isVisualizationRunning.ToString());
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
        }
    }

    private GameObject CreateBall(Color ballColor)
    {
        GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.SetActive(false);
        ball.GetComponent<Renderer>().material = new(baseMaterial);
        ball.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
        ball.GetComponent<Renderer>().receiveShadows = false;
        ball.GetComponent<Collider>().enabled = false;
        ball.GetComponent<Renderer>().material.SetColor("_BaseColor", ballColor);
        ball.transform.localScale = Vector3.one / 5;
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
        ReturnBallsToPool();
        lastScanTime = Time.time;

        float freq = 0.5f;
        float yFreq = 1f;
        int totalCalls = 0;
        List<Vector3> visiblePositions = [];
        List<Vector3> nonVisiblePositions = [];

        Camera theCamera = Camera.main;

        // First pass: Separate positions into visible and non-visible
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

                    Vector3 viewportPoint = theCamera.WorldToViewportPoint(position);
                    bool isVisible =
                        viewportPoint.x >= 0 && viewportPoint.x <= 1 &&
                        viewportPoint.y >= 0 && viewportPoint.y <= 1 &&
                        viewportPoint.z > 0;

                    if (isVisible)
                        visiblePositions.Add(position);
                    else
                        nonVisiblePositions.Add(position);

                    if (totalCalls++ % 5000 == 0)
                        yield return null;
                }
            }
        }

        // Second pass: Process visible positions first
        foreach (Vector3 pos in visiblePositions)
        {
            CheckAndPlaceBallAt(pos);
            if (totalCalls++ % 1000 == 0)
                yield return null;
        }

        // Third pass: Process non-visible positions
        foreach (Vector3 pos in nonVisiblePositions)
        {
            CheckAndPlaceBallAt(pos);
            if (totalCalls++ % 1000 == 0)
                yield return null;
        }
        isVisualizationRunning = false;
    }

    // Most of this code was "borrowed" from CharacterMovement.RaycastGroundCheck
    private void CheckAndPlaceBallAt(Vector3 position)
    {
        Vector3 to = position + Vector3.down * 1;
        RaycastHit raycastHit = HelperFunctions.LineCheck(position, to, HelperFunctions.LayerType.TerrainMap, 0f, QueryTriggerInteraction.Ignore);
        if (raycastHit.transform)
        {
            CollisionModifier component = raycastHit.collider.GetComponent<CollisionModifier>();
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
