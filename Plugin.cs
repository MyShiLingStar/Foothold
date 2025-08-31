using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Foothold;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;

    internal static Scene currentScene;
    internal static MainCamera mainCamera;
    internal static bool activated = false;
    private static readonly List<GameObject> balls = []; // good description of this mod
    private static readonly List<GameObject> redBalls = [];
    private static readonly List<GameObject> pool_balls = [];
    private static readonly List<GameObject> pool_redBalls = [];

	private void Awake()
    {
        Logger = base.Logger;

        SceneManager.sceneLoaded += OnSceneLoaded;

        Logger.LogInfo($"Loaded Foothold? version {MyPluginInfo.PLUGIN_VERSION}");
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode _)
    {
        currentScene = scene;
        // Checking for mainCamera in update because it PROBABLY spawns after scene load for networking reasons but this is a complete guess

        if (currentScene.name.StartsWith("Level_"))
        {
            // make pools

            // normal
            for (int i = 0; i < 2000; i++)
            {
                GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.SetActive(false);
                ball.GetComponent<Renderer>().material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                ball.GetComponent<Collider>().enabled = false;
                ball.transform.localScale = Vector3.one / 5;
                pool_balls.Add(ball);
            }
            // red
            for (int i = 0; i < 2000; i++)
            {
                GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.SetActive(false);
                Material mat = new(Shader.Find("Universal Render Pipeline/Lit"));
                mat.color = Color.red;
                ball.GetComponent<Renderer>().material = mat;
                ball.GetComponent<Collider>().enabled = false;
                ball.transform.localScale = Vector3.one / 5;
                pool_redBalls.Add(ball);
            }
        }
        else
        {
            // clear pools (this isn't destroying the objects but unloading the scene already did that)
            pool_balls.Clear();
            pool_redBalls.Clear();
        }
    }

    private void Update()
    {
        if (currentScene.name.StartsWith("Level_"))
        {
            if (mainCamera == null)
            {
                mainCamera = FindFirstObjectByType<MainCamera>();
                return;
            }
            CheckHotkeys();
        }
    }

    // Keeping Update clean by factoring this out
    private void CheckHotkeys()
    {
        if (Input.GetKeyDown(KeyCode.F)) // TODO: Keybind configuration
        {
            activated = !activated;
            if (activated) // The check is after the change, so this is checking if it has just been toggled on
            {
                RenderVisualization();
            }
            else
            {
                ReturnBallsToPool();
            }
        }
    }

    private void ReturnBallsToPool()
    {
        List<GameObject> ballsCopy = [.. balls]; // Shouldn't modify the list while iterating so we iterate a clone
        foreach (GameObject ball in ballsCopy)
        {
            ball.SetActive(false);
            balls.Remove(ball);
            pool_balls.Add(ball);
        }
        List<GameObject> redBallsCopy = [.. redBalls]; // Shouldn't modify the list while iterating so we iterate a clone
        foreach (GameObject ball in redBallsCopy)
        {
            ball.SetActive(false);
            redBalls.Remove(ball);
            pool_redBalls.Add(ball);
        }
    }

    // Technically CheckAndPlaceBallAt does the actual rendering, but also technically Unity does the rendering,
    // but also technically Vulkan/DX12/DX11 does the rendering, but also technically the GPU does the rendering,
    // all this to say I don't care and I'll name my methods whatever I want
    private void RenderVisualization()
    {
        ReturnBallsToPool();
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
                    GameObject ball = pool_balls.First();
                    ball.transform.position = raycastHit.point;
                    balls.Add(ball);
                    pool_balls.Remove(ball);
                    ball.SetActive(true);
                }
                else if (angle >= 50f && pool_redBalls.Count > 0)
                {
                    GameObject ball = pool_redBalls.First();
                    ball.transform.position = raycastHit.point;
                    redBalls.Add(ball);
                    pool_redBalls.Remove(ball);
                    ball.SetActive(true);
                }
            }
        }
    }
}
